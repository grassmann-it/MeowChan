using System.Runtime.InteropServices;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Installation;
using MeowChan.Persistence;
using MeowChan.Transfer;
using MeowChan.Testing;

namespace MeowChan.DesktopTests;

public sealed class PluginImportStagingTests : IDisposable
{
    private readonly string _root = TemporaryWorkspacePaths.CreateDirectory("meowchan-plugin-staging-");
    private readonly string _plugins;
    private string Staging => Path.Combine(_plugins, PluginImportStaging.DirectoryName);
    private const string OwnStem = ".plugin-import-0123456789abcdef0123456789abcdef";

    public PluginImportStagingTests()
    {
        _plugins = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(_plugins);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void CreatePrivateStaging()
    {
        using PluginImportStaging unused = PluginImportStaging.Create(_plugins);
    }

    private (string Lease, string Payload) CrashPair(string stem = OwnStem)
    {
        string lease = Path.Combine(Staging, stem + ".lock");
        string payload = Path.Combine(Staging, stem + ".tmp");
        File.WriteAllBytes(lease, []);
        File.WriteAllText(payload, "// incomplete crash remnant");
        return (lease, payload);
    }

    [Fact]
    public async Task StagingIsPrivateInsidePluginsAndPublishMovesCompleteBytesWithoutOverwriting()
    {
        using PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        Assert.Equal(Staging, Path.GetDirectoryName(stage.PayloadPath));
        Assert.StartsWith(".", Path.GetFileName(Staging));
        if (OperatingSystem.IsWindows()) Assert.True(File.GetAttributes(Staging).HasFlag(FileAttributes.Hidden));
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Staging));
        byte[] bytes = "// staged ü source"u8.ToArray();
        await stage.WriteAsync(bytes, CancellationToken.None);
        string destination = Path.Combine(_plugins, "plugin.cs");
        File.WriteAllText(destination, "// existing");

        Assert.Throws<IOException>(() => stage.Publish(destination, CancellationToken.None));
        Assert.Equal("// existing", File.ReadAllText(destination));
        Assert.Equal(bytes, File.ReadAllBytes(stage.PayloadPath));
        File.Delete(destination);
        stage.Publish(destination, CancellationToken.None);
        Assert.False(File.Exists(stage.PayloadPath));
        Assert.Equal(bytes, File.ReadAllBytes(destination));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root), path => Path.GetFileName(path).StartsWith(".plugin-import-"));
    }

    [Fact]
    public async Task ARealFileStreamIsFlushedToDiskAndClosedBeforePublication()
    {
        using PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        RecordingPayloadStream? output = null;
        byte[] bytes = "// flushed source"u8.ToArray();
        await stage.WriteAsync(bytes, CancellationToken.None, path => output = new(path, "none"));

        Assert.NotNull(output);
        Assert.Equal(new[] { "written", "flush-to-disk", "closed" }, output.Events);
        string destination = Path.Combine(_plugins, "flushed.cs");
        stage.Publish(destination, CancellationToken.None);
        Assert.Equal(bytes, File.ReadAllBytes(destination));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("close")]
    public async Task AWriteFlushOrCloseFailureNeverMakesThePartialPayloadPublishable(string failure)
    {
        PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        RecordingPayloadStream? output = null;
        string destination = Path.Combine(_plugins, "failed.cs");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => stage.WriteAsync("// incomplete source"u8.ToArray(),
                CancellationToken.None, path => output = new(path, failure)));
            Assert.NotNull(output);
            Assert.Contains("closed", output.Events);
            Assert.Throws<InvalidOperationException>(() => stage.Publish(destination, CancellationToken.None));
            Assert.False(File.Exists(destination));
            PluginImportStaging.CleanupAbandoned(_plugins);
            Assert.True(File.Exists(stage.PayloadPath)); // The live lease still protects the failed write.
            await Assert.ThrowsAsync<InvalidOperationException>(() => stage.WriteAsync("// retry"u8.ToArray(), CancellationToken.None));
        }
        finally { stage.Dispose(); }
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
    }

    [Fact]
    public async Task DisposalJoinsTheWriterBeforeItReleasesFileOwnership()
    {
        PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource disposalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task write = stage.WriteAsync("// complete after barrier"u8.ToArray(), CancellationToken.None,
            path => new PausedPayloadStream(path, entered, resume));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task dispose = Task.Run(() => { disposalEntered.SetResult(); stage.Dispose(); });
        try
        {
            await disposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(dispose.IsCompleted);
            PluginImportStaging.CleanupAbandoned(_plugins);
            Assert.True(File.Exists(stage.PayloadPath));
            Assert.True(File.Exists(Path.ChangeExtension(stage.PayloadPath, ".lock")));
        }
        finally
        {
            resume.TrySetResult();
            await write.WaitAsync(TimeSpan.FromSeconds(10));
            await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
        Assert.Throws<ObjectDisposedException>(() => stage.Publish(Path.Combine(_plugins, "late.cs"), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDuringWriteCannotPublishEvenWhenSomeBytesWereWritten()
    {
        PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task write = stage.WriteAsync("// partial cancelled source"u8.ToArray(), cancellation.Token,
                path => new PausedPayloadStream(path, entered, resume));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            Assert.Throws<InvalidOperationException>(() => stage.Publish(Path.Combine(_plugins, "cancelled.cs"), CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(_plugins, "cancelled.cs")));
            PluginImportStaging.CleanupAbandoned(_plugins);
            Assert.True(File.Exists(stage.PayloadPath));
        }
        finally { resume.TrySetResult(); stage.Dispose(); }
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
    }

    [Fact]
    public async Task ATruncatedOwnedPayloadIsNotPublished()
    {
        using PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        await stage.WriteAsync("// complete source"u8.ToArray(), CancellationToken.None);
        File.WriteAllBytes(stage.PayloadPath, []);
        string destination = Path.Combine(_plugins, "truncated.cs");
        Assert.Throws<IOException>(() => stage.Publish(destination, CancellationToken.None));
        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeitherPublishNorDisposeClaimsAFileThatCreateNewDidNotCreate(bool attemptWrite)
    {
        PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        string lease = Path.ChangeExtension(stage.PayloadPath, ".lock");
        File.WriteAllText(stage.PayloadPath, "foreign bytes");
        try
        {
            if (attemptWrite)
                await Assert.ThrowsAsync<IOException>(() => stage.WriteAsync("// must not replace"u8.ToArray(), CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => stage.Publish(Path.Combine(_plugins, "foreign.cs"), CancellationToken.None));
        }
        finally { stage.Dispose(); }

        Assert.False(File.Exists(lease)); // No crash-pair authority over the foreign entry is left behind.
        PluginImportStaging.CleanupAbandoned(_plugins);
        Assert.Equal("foreign bytes", File.ReadAllText(stage.PayloadPath));
        Assert.False(File.Exists(Path.Combine(_plugins, "foreign.cs")));
    }

    [Fact]
    public void LeaseIdentityFailureQuarantinesTheCreatedLeaseWithoutDeletingByName()
    {
        Assert.Throws<IOException>(() => PluginImportStaging.Create(_plugins,
            static (FileStream _) => (RegularProfileFileIdentity?)null));

        string lease = Assert.Single(Directory.EnumerateFileSystemEntries(Staging));
        Assert.EndsWith(".lock", lease, StringComparison.Ordinal);
        Assert.Equal(1, new FileInfo(lease).Length);

        PluginImportStaging.CleanupAbandoned(_plugins);
        Assert.True(File.Exists(lease)); // The unowned lease is quarantined, never guessed at by startup cleanup.
    }

    [Fact]
    public async Task PayloadIdentityFailureQuarantinesPayloadAndLease()
    {
        PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => stage.WriteAsync("// identity unavailable"u8.ToArray(),
                CancellationToken.None,
                path => new RecordingPayloadStream(path, "none"),
                static (FileStream _) => (RegularProfileFileIdentity?)null));
            Assert.True(File.Exists(stage.PayloadPath));
        }
        finally { stage.Dispose(); }

        string lease = Path.ChangeExtension(stage.PayloadPath, ".lock");
        Assert.True(File.Exists(stage.PayloadPath));
        Assert.Equal(1, new FileInfo(lease).Length);
        PluginImportStaging.CleanupAbandoned(_plugins);
        Assert.True(File.Exists(stage.PayloadPath));
        Assert.True(File.Exists(lease));
    }

    [Fact]
    public async Task UnknownPayloadIdentityNeverLetsCleanupDeleteAForeignReplacement()
    {
        if (OperatingSystem.IsWindows()) return;
        PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        string retained = Path.Combine(_root, "retained-unknown-payload.tmp");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => stage.WriteAsync("// must not be adopted"u8.ToArray(),
                CancellationToken.None,
                path =>
                {
                    FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    File.Move(path, retained);
                    File.WriteAllText(path, "foreign replacement");
                    return output;
                },
                static (FileStream _) => (RegularProfileFileIdentity?)null));
        }
        finally { stage.Dispose(); }

        PluginImportStaging.CleanupAbandoned(_plugins);
        Assert.Equal("foreign replacement", File.ReadAllText(stage.PayloadPath));
        Assert.True(File.Exists(retained));
        Assert.True(File.Exists(Path.ChangeExtension(stage.PayloadPath, ".lock")));
    }

    [Fact]
    public async Task PublicationAndDisposalDoNotClaimAReplacementRegularPayload()
    {
        PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        await stage.WriteAsync("// owned bytes"u8.ToArray(), CancellationToken.None);
        string retained = Path.Combine(_root, "retained-original.tmp");
        File.Move(stage.PayloadPath, retained); // Keep the original inode alive; never rely on inode reuse timing.
        File.WriteAllText(stage.PayloadPath, "foreign replacement");
        try
        {
            Assert.Throws<IOException>(() => stage.Publish(Path.Combine(_plugins, "foreign.cs"), CancellationToken.None));
        }
        finally { stage.Dispose(); }

        PluginImportStaging.CleanupAbandoned(_plugins);
        Assert.Equal("foreign replacement", File.ReadAllText(stage.PayloadPath));
        Assert.Equal("// owned bytes", File.ReadAllText(retained)); // No deletion outside our owned pathname.
        Assert.False(File.Exists(Path.ChangeExtension(stage.PayloadPath, ".lock")));
    }

    [Fact]
    public async Task SuccessfulPublicationReleasesTheStagingPathAndDisposalIsIdempotent()
    {
        PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        await stage.WriteAsync("// published"u8.ToArray(), CancellationToken.None);
        string destination = Path.Combine(_plugins, "published.cs");
        string lease = Path.ChangeExtension(stage.PayloadPath, ".lock");
        stage.Publish(destination, CancellationToken.None);
        File.WriteAllText(stage.PayloadPath, "new unowned occupant");
        Assert.Throws<InvalidOperationException>(() => stage.Publish(Path.Combine(_plugins, "again.cs"), CancellationToken.None));
        stage.Dispose();
        PluginImportStaging.CleanupAbandoned(_plugins);
        Assert.Equal("new unowned occupant", File.ReadAllText(stage.PayloadPath));
        Assert.Equal("// published", File.ReadAllText(destination));
        Assert.False(File.Exists(lease));

        File.WriteAllText(lease, "foreign lease after disposal");
        stage.Dispose();
        Assert.Equal("foreign lease after disposal", File.ReadAllText(lease));
        Assert.Equal("new unowned occupant", File.ReadAllText(stage.PayloadPath));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stage.WriteAsync("// too late"u8.ToArray(), CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => stage.Publish(Path.Combine(_plugins, "late.cs"), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostInspectionDiscardDeletesOnlyTheExactPublishedFile(bool replace)
    {
        using PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        await stage.WriteAsync("// owned publication"u8.ToArray(), CancellationToken.None);
        string destination = Path.Combine(_plugins, "reviewed.cs");
        string retained = Path.Combine(_root, "retained-publication.cs");
        stage.Publish(destination, CancellationToken.None);
        if (replace)
        {
            File.Move(destination, retained);
            File.WriteAllText(destination, "foreign publication");
        }

        stage.DiscardPublished();
        if (replace)
        {
            Assert.Equal("foreign publication", File.ReadAllText(destination));
            Assert.Equal("// owned publication", File.ReadAllText(retained));
        }
        else Assert.False(File.Exists(destination));
        File.WriteAllText(destination, "later publication");
        stage.DiscardPublished(); // Even a second rejection has no authority over a new occupant.
        Assert.Equal("later publication", File.ReadAllText(destination));
    }

    [Fact]
    public async Task PublicationNeverAdoptsAPreexistingDestinationNameThatAlreadyLinksToThePayload()
    {
        // Deterministic witness of the no-clobber contract. Unix File.Move(..., overwrite: false) treats a destination
        // that is the same inode as "renameable"; rename(2) then succeeds without moving anything. That name was not
        // created by this import, so it must not become our publication (and must not be discarded as ours later).
        if (OperatingSystem.IsWindows()) return;
        PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        string destination = Path.Combine(_plugins, "linked-elsewhere.cs");
        try
        {
            await stage.WriteAsync("// owned bytes, foreign name"u8.ToArray(), CancellationToken.None);
            Assert.Equal(0, HardLink(stage.PayloadPath, destination));

            Assert.Throws<IOException>(() => stage.Publish(destination, CancellationToken.None));
            stage.DiscardPublished(); // Nothing was published by us: no authority over the foreign name.
            Assert.Equal("// owned bytes, foreign name", File.ReadAllText(destination));
            Assert.True(File.Exists(stage.PayloadPath)); // Still our owned staging name until disposal.
        }
        finally { stage.Dispose(); }

        Assert.Equal("// owned bytes, foreign name", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging)); // Neither a stranded payload nor a lease.
    }

    [Fact]
    public async Task UnixPublicationLeavesExactlyOneNameForThePublishedFile()
    {
        if (OperatingSystem.IsWindows()) return;
        using PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        await stage.WriteAsync("// single name"u8.ToArray(), CancellationToken.None);
        RegularProfileFileIdentity staged = RegularProfileFile.TryGetIdentity(stage.PayloadPath)!.Value;
        string destination = Path.Combine(_plugins, "single.cs");

        stage.Publish(destination, CancellationToken.None);

        RegularProfileFileIdentity published = RegularProfileFile.TryGetIdentity(destination)!.Value;
        Assert.Equal((staged.Device, staged.Inode), (published.Device, published.Inode));
        Assert.False(File.Exists(stage.PayloadPath));
        Assert.Equal(Path.ChangeExtension(stage.PayloadPath, ".lock"), Assert.Single(Directory.EnumerateFileSystemEntries(Staging)));
        stage.DiscardPublished();
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void RestartCleanupAfterACrashBetweenPublicationAndStagingUnlinkKeepsTheInstalledPlugin()
    {
        // A crash after link(2) but before the staging name was dropped leaves an abandoned pair whose payload is a
        // second name of the installed plugin. Startup removes only the two staging names, never the plugin file.
        if (OperatingSystem.IsWindows()) return;
        CreatePrivateStaging();
        string installed = Path.Combine(_plugins, "installed.cs");
        File.WriteAllText(installed, "// installed plugin");
        string lease = Path.Combine(Staging, OwnStem + ".lock");
        string payload = Path.Combine(Staging, OwnStem + ".tmp");
        File.WriteAllBytes(lease, []);
        Assert.Equal(0, HardLink(installed, payload));

        PluginImportStaging.CleanupAbandoned(_plugins);

        Assert.Equal("// installed plugin", File.ReadAllText(installed));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
    }

    [Fact]
    public async Task CancellationAfterWritingDoesNotPublishAndDisposalRemovesOnlyOwnedFiles()
    {
        string payload;
        using (PluginImportStaging stage = PluginImportStaging.Create(_plugins))
        {
            payload = stage.PayloadPath;
            await stage.WriteAsync("// complete bytes"u8.ToArray(), CancellationToken.None);
            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => stage.Publish(Path.Combine(_plugins, "cancelled.cs"), cancelled.Token));
            Assert.True(File.Exists(payload));
        }
        Assert.False(File.Exists(payload));
        Assert.False(File.Exists(Path.Combine(_plugins, "cancelled.cs")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
    }

    [Fact]
    public async Task PublicationCannotEscapeTheAuthorizedPluginDirectory()
    {
        using PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        await stage.WriteAsync("// private payload"u8.ToArray(), CancellationToken.None);
        string escaped = Path.Combine(_root, "escaped.cs");

        Assert.Throws<IOException>(() => stage.Publish(escaped, CancellationToken.None));

        Assert.False(File.Exists(escaped));
        Assert.Equal("// private payload", File.ReadAllText(stage.PayloadPath));
    }

    [Fact]
    public async Task PublicationRefusesAPayloadReplacedByASymlink()
    {
        if (OperatingSystem.IsWindows()) return;
        using PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        await stage.WriteAsync("// private payload"u8.ToArray(), CancellationToken.None);
        string outside = Path.Combine(_root, "outside.cs");
        File.WriteAllText(outside, "// must not be moved");
        File.Delete(stage.PayloadPath);
        File.CreateSymbolicLink(stage.PayloadPath, outside);
        string destination = Path.Combine(_plugins, "plugin.cs");

        Assert.Throws<IOException>(() => stage.Publish(destination, CancellationToken.None));

        Assert.False(File.Exists(destination));
        Assert.Equal("// must not be moved", File.ReadAllText(outside));
        File.Delete(stage.PayloadPath);
    }

    [Fact]
    public void StartupCleanupRemovesCrashPairsButNotUnknownFilesOrProfileRootRemnants()
    {
        CreatePrivateStaging();
        (string lease, string payload) = CrashPair();
        string[] untouched =
        [
            Path.Combine(Staging, "notes.txt"),
            Path.Combine(Staging, ".plugin-import-not-a-guid.tmp"),
            Path.Combine(Staging, ".plugin-import-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.tmp"), // no matching lease
            Path.Combine(_root, OwnStem + ".tmp"), // legacy location: never sweep the profile root
            Path.Combine(_plugins, "manual.cs")
        ];
        foreach (string path in untouched) File.WriteAllText(path, "keep");

        PluginImportStaging.CleanupAbandoned(_plugins);

        Assert.False(File.Exists(lease));
        Assert.False(File.Exists(payload));
        foreach (string path in untouched) Assert.Equal("keep", File.ReadAllText(path));
    }

    [Fact]
    public async Task ActiveImportSurvivesCleanupEvenWithAnOldTimestampAndAnOtherwiseAbandonedPair()
    {
        using PluginImportStaging active = PluginImportStaging.Create(_plugins);
        await active.WriteAsync("// active import"u8.ToArray(), CancellationToken.None);
        File.SetLastWriteTimeUtc(active.PayloadPath, DateTime.UnixEpoch);
        (string lease, string payload) = CrashPair();

        PluginImportStaging.CleanupAbandoned(_plugins);

        Assert.Equal("// active import", File.ReadAllText(active.PayloadPath));
        Assert.False(File.Exists(lease));
        Assert.False(File.Exists(payload));
        string destination = Path.Combine(_plugins, "active.cs");
        active.Publish(destination, CancellationToken.None);
        PluginImportStaging.CleanupAbandoned(_plugins);
        Assert.True(File.Exists(Path.ChangeExtension(active.PayloadPath, ".lock"))); // still inspecting the published copy
        Assert.Equal("// active import", File.ReadAllText(destination));
    }

    [Fact]
    public void CleanupRequiresAnEmptyLeaseAndNeverRecursesIntoDirectories()
    {
        CreatePrivateStaging();
        (string lease, string payload) = CrashPair();
        File.WriteAllText(lease, "not our empty lease");
        string nested = Path.Combine(Staging, ".plugin-import-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.lock");
        Directory.CreateDirectory(nested);
        string child = Path.Combine(nested, OwnStem + ".tmp");
        File.WriteAllText(child, "keep");

        PluginImportStaging.CleanupAbandoned(_plugins);

        Assert.True(File.Exists(payload));
        Assert.Equal("not our empty lease", File.ReadAllText(lease));
        Assert.Equal("keep", File.ReadAllText(child));
    }

    [Theory]
    [InlineData(".plugin-import-not-a-guid")]
    [InlineData(".plugin-import-ABCDEF0123456789abcdef0123456789")]
    [InlineData(".plugin-import-0123456789abcdef0123456789abcdef-extra")]
    public void CleanupNeverClaimsSimilarButNotExactNames(string stem)
    {
        CreatePrivateStaging();
        (string lease, string payload) = CrashPair(stem);

        PluginImportStaging.CleanupAbandoned(_plugins);

        Assert.True(File.Exists(lease));
        Assert.True(File.Exists(payload));
    }

    [Fact]
    public void AnExternallyHeldLeaseIsNeverTreatedAsACrashRemnant()
    {
        CreatePrivateStaging();
        (string lease, string payload) = CrashPair();
        using (FileStream anotherInstance = new(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            PluginImportStaging.CleanupAbandoned(_plugins);
            Assert.True(File.Exists(lease));
            Assert.True(File.Exists(payload));
        }
        PluginImportStaging.CleanupAbandoned(_plugins);
        Assert.False(File.Exists(lease));
        Assert.False(File.Exists(payload));
    }

    [Fact]
    public void APluginDirectoryReachedThroughALinkIsNotTraversed()
    {
        if (OperatingSystem.IsWindows()) return;
        CreatePrivateStaging();
        (string lease, string payload) = CrashPair();
        string alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, _root);
        string aliasedPlugins = Path.Combine(alias, "plugins");

        PluginImportStaging.CleanupAbandoned(aliasedPlugins);
        Assert.Throws<IOException>(() => PluginImportStaging.Create(aliasedPlugins));

        Assert.True(File.Exists(lease));
        Assert.True(File.Exists(payload));
        Directory.Delete(alias);
    }

    [Fact]
    public void MissingStagingIsNotCreatedByCleanup()
    {
        PluginImportStaging.CleanupAbandoned(_plugins);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_plugins));
    }

    [Fact]
    public void CleanupAndCreationRejectAStagingDirectorySymlink()
    {
        if (OperatingSystem.IsWindows()) return;
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Staging, outside);
        (string lease, string payload) = CrashPair();

        PluginImportStaging.CleanupAbandoned(_plugins);
        Assert.Throws<IOException>(() => PluginImportStaging.Create(_plugins));

        Assert.True(File.Exists(lease));
        Assert.True(File.Exists(payload));
        Directory.Delete(Staging);
    }

    [Fact]
    public void ADanglingStagingDirectoryLinkIsNotCreatedThroughOrRemoved()
    {
        if (OperatingSystem.IsWindows()) return;
        string missing = Path.Combine(_root, "must-not-be-created");
        Directory.CreateSymbolicLink(Staging, missing);

        PluginImportStaging.CleanupAbandoned(_plugins);
        Assert.Throws<IOException>(() => PluginImportStaging.Create(_plugins));

        Assert.False(Directory.Exists(missing));
        Assert.Equal(missing, new DirectoryInfo(Staging).LinkTarget);
        // On Unix a dangling directory symlink is an unlinkable file entry, not an existing directory.
        File.Delete(Staging);
        Assert.Null(new FileInfo(Staging).LinkTarget);
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void CleanupDoesNotFollowLeaseOrPayloadSymlinks(bool linkLease, bool dangling)
    {
        if (OperatingSystem.IsWindows()) return;
        CreatePrivateStaging();
        (string lease, string payload) = CrashPair();
        string outside = Path.Combine(_root, "external.txt");
        if (!dangling) File.WriteAllText(outside, "external bytes");
        string linked = linkLease ? lease : payload;
        File.Delete(linked);
        File.CreateSymbolicLink(linked, outside);

        PluginImportStaging.CleanupAbandoned(_plugins);

        if (dangling) Assert.False(File.Exists(outside));
        else Assert.Equal("external bytes", File.ReadAllText(outside));
        Assert.Equal(outside, new FileInfo(linked).LinkTarget);
        Assert.Contains(lease, Directory.EnumerateFileSystemEntries(Staging));
        Assert.Contains(payload, Directory.EnumerateFileSystemEntries(Staging));
        File.Delete(linked);
    }

    [Fact]
    public void CleanupRejectsAPermissiveStagingDirectoryInsteadOfAdoptingIt()
    {
        if (OperatingSystem.IsWindows()) return;
        CreatePrivateStaging();
        (string lease, string payload) = CrashPair();
        File.SetUnixFileMode(Staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);

        PluginImportStaging.CleanupAbandoned(_plugins);
        Assert.Throws<IOException>(() => PluginImportStaging.Create(_plugins));

        Assert.True(File.Exists(lease));
        Assert.True(File.Exists(payload));
    }

    [Fact]
    public async Task PrivateStagingDoesNotConsumeTheInventoryCandidateBudget()
    {
        CreatePrivateStaging();
        string source = Path.Combine(_plugins, "visible.cs");
        File.WriteAllText(source, "// an actual plugin candidate");

        PluginInventory scan = await new PluginInstallationInventory(maximumCandidates: 1).ScanAsync(_plugins);

        Assert.Equal(source, Assert.Single(scan.Candidates).Path);
        Assert.False(scan.Truncated);
        Assert.Empty(scan.Diagnostics);
    }

    [Fact]
    public async Task SimilarDirectoriesRemainVisibleAsInventoryProblems()
    {
        CreatePrivateStaging();
        string other = Path.Combine(_plugins, PluginImportStaging.DirectoryName + "-not-ours");
        Directory.CreateDirectory(other);

        PluginInventory scan = await new PluginInstallationInventory().ScanAsync(_plugins);

        PluginInstallationCandidate problem = Assert.Single(scan.Candidates);
        Assert.Equal(other, problem.Path);
        Assert.Contains(problem.Diagnostics, diagnostic => diagnostic.Code == "MCENTRY");
    }

    [Fact]
    public async Task ActiveAndAbandonedPayloadsDoNotEnterProfileTransferOrBackup()
    {
        // The real profile transfer/backup is a bounded store image, not a recursive directory copy.
        // Only a newly created temporary fixture profile is opened here; no user profile/store is involved.
        using ProfileLease lease = ProfileLease.Acquire(_root);
        PostFilterStore store = new(_root, lease);
        PostFilterTransferExport baseline = PostFilterProfileTransfer.Export(store);
        Assert.True(baseline.IsExported);
        using PluginImportStaging active = PluginImportStaging.Create(_plugins);
        await active.WriteAsync("// active plugin bytes, not profile policy"u8.ToArray(), CancellationToken.None);
        CrashPair();

        PostFilterTransferExport transfer = PostFilterProfileTransfer.Export(store);
        PostFilterTransferExport backup = PostFilterProfileBackup.Capture(store);

        Assert.True(transfer.IsExported);
        Assert.True(backup.IsExported);
        Assert.Equal(baseline.Bytes, transfer.Bytes);
        Assert.Equal(baseline.Bytes, backup.Bytes);
        Assert.Empty(transfer.Snapshot!.Rules);
        Assert.Empty(transfer.Snapshot.HiddenPosts);
        Assert.True(File.Exists(active.PayloadPath));
    }

    [Fact]
    public async Task PrivateStagingIsNotAnInventoryCandidate()
    {
        using PluginImportStaging stage = PluginImportStaging.Create(_plugins);
        await stage.WriteAsync("// active source"u8.ToArray(), CancellationToken.None);
        PluginInventory scan = await new PluginInstallationInventory().ScanAsync(_plugins);
        // Integration gate: the host must skip the exact reserved directory before its candidate limit.
        // That one-line host hook is outside the permitted file scope; see the handoff report.
        Assert.Empty(scan.Candidates);
        Assert.Empty(scan.Diagnostics);
    }

    // Test-only hard link on a fresh temporary fixture; Unix only (callers return early on Windows).
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int HardLink([MarshalAs(UnmanagedType.LPUTF8Str)] string existing,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string created);

    private sealed class PausedPayloadStream(string path, TaskCompletionSource entered, TaskCompletionSource resume)
        : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous)
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            entered.SetResult();
            await resume.Task.WaitAsync(cancellationToken);
        }
    }

    // Real temporary FileStream handles: no plugin execution, public endpoint or private profile.
    private sealed class RecordingPayloadStream(string path, string failure)
        : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous)
    {
        internal List<string> Events { get; } = [];

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (failure == "write")
            {
                await base.WriteAsync(buffer[..Math.Max(1, buffer.Length / 2)], cancellationToken);
                throw new IOException("Synthetic partial write failure.");
            }
            await base.WriteAsync(buffer, cancellationToken);
            Events.Add("written");
        }

        public override void Flush(bool flushToDisk)
        {
            if (flushToDisk)
            {
                Events.Add("flush-to-disk");
                if (failure == "flush") throw new IOException("Synthetic durable flush failure.");
            }
            base.Flush(flushToDisk);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Events.Add("closed");
            if (failure == "close") throw new IOException("Synthetic close failure.");
        }
    }
}
