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
        Assert.NotNull(new DirectoryInfo(Staging).LinkTarget);
        Directory.Delete(Staging);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CleanupDoesNotFollowLeaseOrPayloadSymlinks(bool linkLease)
    {
        if (OperatingSystem.IsWindows()) return;
        CreatePrivateStaging();
        (string lease, string payload) = CrashPair();
        string outside = Path.Combine(_root, "external.txt");
        File.WriteAllText(outside, "external bytes");
        string linked = linkLease ? lease : payload;
        File.Delete(linked);
        File.CreateSymbolicLink(linked, outside);

        PluginImportStaging.CleanupAbandoned(_plugins);

        Assert.Equal("external bytes", File.ReadAllText(outside));
        Assert.NotNull(new FileInfo(linked).LinkTarget);
        Assert.True(File.Exists(lease));
        Assert.True(File.Exists(payload));
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
}
