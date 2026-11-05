using System.Security.Cryptography;
using MeowChan.Desktop;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopTests;

// Real isolated stores/leases; events hold actual pinned I/O, never substitute for a save.
public sealed class DirectSettingsStoreAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundStandaloneEntriesPersistInStartingAndReadyWithoutPhantomReservations(bool preview)
    {
        using Workspace workspace = new();
        var kinds = new List<ProfileAdmissionKind>();
        var tokens = new List<ProfileAdmissionToken>();
        workspace.SetHooks(preview, token => { kinds.Add(token.Kind); tokens.Add(token); }, token =>
        {
            Assert.Same(tokens[^1], token);
            Assert.Empty(workspace.TemporaryFiles());
        });
        workspace.SetNextPayload();
        workspace.Save(preview);
        workspace.AssertSaved(preview);
        workspace.Lease.Admission.CompleteStartup(); // A leaked standalone owner would reject this.
        workspace.Save(preview);
        workspace.AssertSaved(preview);
        Assert.Equal(new[] { ProfileAdmissionKind.Startup, ProfileAdmissionKind.Ordinary }, kinds);
        Assert.NotSame(tokens[0], tokens[1]);
        using CancellationTokenSource deadline = new(Deadline);
        using ProfileAdmissionToken exclusive = await workspace.Lease.Admission.EnterExclusiveAsync(deadline.Token);
        Assert.Equal(ProfileAdmissionKind.Exclusive, exclusive.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingAndActiveExclusiveRejectNewEntriesButBorrowedOwnerStillWrites(bool preview)
    {
        using Workspace workspace = new();
        workspace.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = workspace.Lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancel = new();
        Task<ProfileAdmissionToken> pending = workspace.Lease.Admission.EnterExclusiveAsync(cancel.Token).AsTask();
        try
        {
            Assert.False(pending.IsCompleted);
            string[] before = workspace.Files();
            Assert.Throws<InvalidOperationException>(() => workspace.Save(preview));
            Assert.Equal(before, workspace.Files());
            workspace.SetNextPayload();
            ProfileAdmissionToken? observed = null;
            workspace.SetHooks(preview, token => observed = token, null);
            workspace.SaveAdmitted(preview, owner);
            Assert.Same(owner, observed);
            workspace.AssertSaved(preview);
            Assert.False(pending.IsCompleted); // Store borrowed; it did not release the owner's token.
            owner.Dispose();
            using ProfileAdmissionToken granted = await pending.WaitAsync(Deadline);
            before = workspace.Files();
            Assert.Throws<InvalidOperationException>(() => workspace.Save(preview));
            Assert.Throws<InvalidOperationException>(() => workspace.SaveAdmitted(preview, granted));
            Assert.Equal(before, workspace.Files());
        }
        finally
        {
            owner.Dispose();
            cancel.Cancel();
            try { (await pending.WaitAsync(Deadline)).Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false, "recovery")]
    [InlineData(true, "recovery")]
    [InlineData(false, "stopping")]
    [InlineData(true, "stopping")]
    [InlineData(false, "stopped")]
    [InlineData(true, "stopped")]
    [InlineData(false, "disposed")]
    [InlineData(true, "disposed")]
    public void ClosedPhasesRejectStandaloneBeforeAnyFileEffect(bool preview, string phase)
    {
        using Workspace workspace = new();
        switch (phase)
        {
            case "recovery": workspace.Lease.Admission.FailStartup(); break;
            case "stopping": workspace.Lease.Admission.BeginShutdown(); break;
            case "stopped":
                workspace.Lease.Admission.BeginShutdown();
                workspace.Lease.Admission.CompleteShutdown();
                break;
            case "disposed": workspace.Lease.Dispose(); break;
            default: throw new ArgumentException(nameof(phase));
        }
        int hooks = 0;
        workspace.SetHooks(preview, _ => hooks++, _ => hooks++);
        string[] before = workspace.Files();
        workspace.SetNextPayload();
        if (phase == "disposed") Assert.Throws<ObjectDisposedException>(() => workspace.Save(preview));
        else Assert.Throws<InvalidOperationException>(() => workspace.Save(preview));
        Assert.Equal(0, hooks);
        Assert.Equal(before, workspace.Files());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullForeignUnissuedReleasedAndDisposedLeaseFailClosedEvenForUnchangedPayload(bool preview)
    {
        using Workspace workspace = new();
        using Workspace other = new();
        using ProfileAdmissionToken foreign = other.Lease.Admission.EnterStartupWriter();
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(workspace.Lease.Admission, ProfileAdmissionKind.Startup);
        using ProfileAdmissionToken released = workspace.Lease.Admission.EnterStartupWriter();
        released.Dispose();
        int hooks = 0;
        workspace.SetHooks(preview, _ => hooks++, _ => hooks++);
        string[] before = workspace.Files();
        foreach (ProfileAdmissionToken? token in new ProfileAdmissionToken?[] { null, foreign, unissued, released })
            Assert.Throws<InvalidOperationException>(() => workspace.SaveAdmitted(preview, token!));
        Assert.Equal(0, hooks);
        Assert.Equal(before, workspace.Files());
        using ProfileAdmissionToken accepted = workspace.Lease.Admission.EnterStartupWriter();
        workspace.Lease.Dispose();
        // Already accepted authority cannot bypass the disposed OS lease.
        Assert.Throws<ObjectDisposedException>(() => workspace.SaveAdmitted(preview, accepted));
        Assert.Equal(0, hooks);
        Assert.Equal(before, workspace.Files());
        accepted.Dispose();
        Assert.True(workspace.Lease.Admission.DrainAsync().IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AcceptedStorePinSurvivesReleaseRequestThroughRealIoAndCleanup(bool preview, bool shutdown)
    {
        using Workspace workspace = new();
        workspace.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = workspace.Lease.Admission.EnterOrdinaryWriter();
        using ManualResetEventSlim beforeIo = new(), allowIo = new(), afterCleanup = new(), allowReturn = new();
        using CancellationTokenSource cancelReservation = new(), cancelObserver = new();
        workspace.SetNextPayload();
        workspace.SetHooks(preview, token =>
        {
            Assert.Same(owner, token);
            beforeIo.Set();
            Assert.True(allowIo.Wait(Deadline), "Actual store I/O was not released.");
        }, token =>
        {
            Assert.Same(owner, token);
            Assert.Empty(workspace.TemporaryFiles());
            afterCleanup.Set();
            Assert.True(allowReturn.Wait(Deadline), "Actual post-cleanup store return was not released.");
        });
        string[] before = workspace.Files();
        Task write = Task.Run(() => workspace.SaveAdmitted(preview, owner));
        Task<ProfileAdmissionToken>? exclusive = null;
        Task? drain = null;
        try
        {
            Assert.True(beforeIo.Wait(Deadline), "The admitted save never reached its real pre-I/O pin.");
            if (shutdown)
            {
                workspace.Lease.Admission.BeginShutdown();
                drain = workspace.Lease.Admission.DrainAsync();
                Task observer = workspace.Lease.Admission.DrainAsync(cancelObserver.Token);
                cancelObserver.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer);
            }
            else exclusive = workspace.Lease.Admission.EnterExclusiveAsync(cancelReservation.Token).AsTask();
            owner.Dispose();
            Task completion = drain ?? exclusive!;
            Assert.False(completion.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => workspace.SaveAdmitted(preview, owner));
            Assert.Throws<InvalidOperationException>(() => workspace.Save(preview));
            Assert.Equal(before, workspace.Files());
            Assert.ThrowsAny<IOException>(() => ProfileLease.Acquire(workspace.Path));
            allowIo.Set();
            Assert.True(afterCleanup.Wait(Deadline), "The real commit/cleanup did not finish.");
            Assert.False(completion.IsCompleted); // Successful disk I/O alone does not end the use.
            Assert.False(write.IsCompleted);
            Assert.NotEqual(string.Join("\n", before), string.Join("\n", workspace.Files()));
            Assert.ThrowsAny<IOException>(() => ProfileLease.Acquire(workspace.Path));
            if (shutdown) Assert.Throws<InvalidOperationException>(workspace.Lease.Admission.CompleteShutdown);
            allowReturn.Set();
            await write.WaitAsync(Deadline);
            workspace.AssertSaved(preview);
            if (shutdown)
            {
                await drain!.WaitAsync(Deadline);
                workspace.Lease.Admission.CompleteShutdown();
            }
            else
            {
                using ProfileAdmissionToken granted = await exclusive!.WaitAsync(Deadline);
                Assert.Equal(ProfileAdmissionKind.Exclusive, granted.Kind);
            }
        }
        finally
        {
            allowIo.Set(); allowReturn.Set(); owner.Dispose(); cancelReservation.Cancel();
            await write.WaitAsync(Deadline);
            if (exclusive != null)
            {
                try { (await exclusive.WaitAsync(Deadline)).Dispose(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedStartupOwnerCanFinishAfterShutdownButCannotBeReusedAfterRelease(bool preview)
    {
        using Workspace workspace = new();
        using ProfileAdmissionToken owner = workspace.Lease.Admission.EnterStartupWriter();
        workspace.Lease.Admission.BeginShutdown();
        Task drain = workspace.Lease.Admission.DrainAsync();
        workspace.SetNextPayload();
        workspace.SaveAdmitted(preview, owner);
        workspace.AssertSaved(preview);
        Assert.False(drain.IsCompleted);
        owner.Dispose();
        Assert.True(drain.IsCompletedSuccessfully);
        string[] before = workspace.Files();
        Assert.Throws<InvalidOperationException>(() => workspace.SaveAdmitted(preview, owner));
        Assert.Equal(before, workspace.Files());
        workspace.Lease.Admission.CompleteShutdown();
    }

    [Fact]
    public async Task BoundCloneAliasesAndReadPathsKeepTheSameDomainAndRejectForeignDestinations()
    {
        using Workspace workspace = new();
        using Workspace other = new();
        workspace.Lease.Admission.CompleteStartup();
        var observed = new List<ProfileAdmissionToken>();
        workspace.Config.BeforeAdmittedSaveMutationForTests = token => observed.Add(token);
        workspace.Config.AfterAdmittedSaveMutationForTests = token => Assert.Same(observed[^1], token);
        Config clone = workspace.Config.Clone();
        Assert.Same(workspace.Config.BeforeAdmittedSaveMutationForTests, clone.BeforeAdmittedSaveMutationForTests);
        Assert.Same(workspace.Config.AfterAdmittedSaveMutationForTests, clone.AfterAdmittedSaveMutationForTests);
        clone.Language = "de";
        clone.SaveConfig();
        Assert.Equal("de", Config.LoadConfig(workspace.Path).Language);
        clone.Language = "fr";
        clone.SaveConfig(workspace.Path);
        Assert.Equal("fr", Config.LoadConfig(workspace.Path).Language);
        Assert.Equal(2, observed.Count);
        Assert.All(observed, token => Assert.Equal(ProfileAdmissionKind.Ordinary, token.Kind));
        Assert.NotSame(observed[0], observed[1]);
        Assert.DoesNotContain("AdmittedSaveMutationForTests", File.ReadAllText(System.IO.Path.Combine(workspace.Path, "settings.json")));
        using ProfileAdmissionToken owner = workspace.Lease.Admission.EnterOrdinaryWriter();
        string[] before = workspace.Files(), otherBefore = other.Files();
        Assert.Throws<ArgumentException>(() => clone.SaveConfigAdmitted(other.Path, owner));
        Assert.Equal(before, workspace.Files());
        Assert.Equal(otherBefore, other.Files());
        using CancellationTokenSource cancel = new();
        Task<ProfileAdmissionToken> pending = workspace.Lease.Admission.EnterExclusiveAsync(cancel.Token).AsTask();
        try
        {
            Assert.Throws<InvalidOperationException>(() => clone.SaveConfig());
            Assert.Throws<InvalidOperationException>(() => clone.SaveConfig(workspace.Path));
            // Read-only paths do not enter a store writer or mutate/quarantine files.
            Assert.Equal("fr", Config.LoadConfig(workspace.Path, workspace.Lease).Language);
            Assert.Equal("initial", workspace.Store.Load()!.Rows[0].Title);
            Assert.Equal(before, workspace.Files());
        }
        finally
        {
            owner.Dispose(); cancel.Cancel();
            try { (await pending.WaitAsync(Deadline)).Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void OfflineCompatibilityDoesNotAdoptAProvidedTokenOrChangeIntoBoundAuthority()
    {
        using Workspace workspace = new();
        using ProfileAdmissionToken token = workspace.Lease.Admission.EnterStartupWriter();
        Config offline = new() { Language = "de" };
        string[] before = workspace.Files();
        Assert.Throws<InvalidOperationException>(() => offline.SaveConfigAdmitted(workspace.Path, token));
        Assert.Equal(before, workspace.Files());
        // Explicitly offline only: no claim that this API enrolls arbitrary live leased paths.
        string path = TemporaryWorkspacePaths.CreateDirectory("meowchan-direct-settings-offline-");
        try
        {
            offline.SaveConfig(path);
            offline.Language = "fr";
            offline.SaveConfig();
            Assert.Equal("fr", Config.LoadConfig(path).Language);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Theory]
    [InlineData("backup", "BackupRecovery")]
    [InlineData("damaged", "Damaged")]
    [InlineData("future", "Unsupported")]
    [InlineData("missing", "ExistingMissingSettings")]
    public void ValidTokenCannotRepairRecoveredDamagedUnsupportedOrMissingSettings(string kind, string expected)
    {
        string path = TemporaryWorkspacePaths.CreateDirectory("meowchan-direct-settings-recovery-");
        try
        {
            string settings = System.IO.Path.Combine(path, "settings.json");
            switch (kind)
            {
                case "backup":
                    File.WriteAllText(settings, "{broken");
                    File.WriteAllText(settings + ".bak", "{\"Language\":\"fr\",\"SourceMediaIntegrityPermissions\":{\"synthetic/source\":{\"PluginOwnerId\":\"synthetic\",\"SourceId\":\"source\",\"AllowProviderSizeMd5Mismatch\":true,\"Revision\":1}}}");
                    break;
                case "damaged": File.WriteAllText(settings, "{broken"); break;
                case "future": File.WriteAllText(settings, "{\"SchemaVersion\":99}"); break;
                case "missing": File.WriteAllText(System.IO.Path.Combine(path, "saved_urls.json"), "[]"); break;
                default: throw new ArgumentException(nameof(kind));
            }
            using ProfileLease lease = ProfileLease.Acquire(path);
            Config config = Config.LoadConfig(path, lease);
            Assert.Equal(expected, config.Origin.ToString());
            if (kind == "backup") Assert.All(config.SourceMediaIntegrityPermissions.Values,
                permission => Assert.False(permission.AllowProviderSizeMd5Mismatch));
            string[] before = FileState(path);
            using ProfileAdmissionToken token = lease.Admission.EnterStartupWriter();
            int hooks = 0;
            config.BeforeAdmittedSaveMutationForTests = _ => hooks++;
            config.AfterAdmittedSaveMutationForTests = _ => hooks++;
            Assert.Throws<InvalidOperationException>(() => config.SaveConfigAdmitted(path, token));
            Assert.Throws<InvalidOperationException>(() => config.Clone().SaveConfigAdmitted(path, token));
            Assert.Equal(expected, config.Origin.ToString());
            Assert.Equal(0, hooks);
            Assert.Equal(before, FileState(path));
            Assert.Throws<InvalidOperationException>(lease.Admission.CompleteStartup);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void ConfirmedNewBoundProfileCanActuallyCommitWithoutAdoptingExistingMissingSettings()
    {
        using Workspace workspace = new(seed: false);
        Assert.Equal(ProfileOrigin.ConfirmedNew, workspace.Config.Origin);
        using (ProfileAdmissionToken token = workspace.Lease.Admission.EnterStartupWriter())
            workspace.Config.SaveConfigAdmitted(workspace.Path, token);
        Assert.Equal(ProfileOrigin.ExistingValid, workspace.Config.Origin);
        Assert.True(Config.LoadConfig(workspace.Path).FirstRunSetupPending);
        Assert.Empty(workspace.TemporaryFiles());
        workspace.Lease.Admission.CompleteStartup();
    }

    [Fact]
    public void ActualConfigReplaceFailureCleansItsTemporaryAndDoesNotReleaseBorrowedOwner()
    {
        using Workspace workspace = new();
        workspace.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = workspace.Lease.Admission.EnterOrdinaryWriter();
        string primary = System.IO.Path.Combine(workspace.Path, "settings.json");
        string before = File.ReadAllText(primary);
        string unrelated = System.IO.Path.Combine(workspace.Path, ".unrelated.tmp");
        File.WriteAllText(unrelated, "not-owned");
        int after = 0;
        workspace.Config.Language = "de";
        // Destination validation has completed. This forces real File.Replace, not a mock failure.
        workspace.Config.BeforeAdmittedSaveMutationForTests = token =>
        {
            Assert.Same(owner, token);
            Directory.CreateDirectory(primary + ".bak");
        };
        workspace.Config.AfterAdmittedSaveMutationForTests = _ => after++;
        Exception? error = Record.Exception(() => workspace.Config.SaveConfigAdmitted(workspace.Path, owner));
        Assert.NotNull(error);
        Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
        Assert.Equal(before, File.ReadAllText(primary));
        Assert.Equal("not-owned", File.ReadAllText(unrelated));
        Assert.Empty(Directory.GetFiles(workspace.Path, ".settings.json.*.tmp"));
        Assert.Equal(0, after);
        using CancellationTokenSource cancel = new();
        Task<ProfileAdmissionToken> pending = workspace.Lease.Admission.EnterExclusiveAsync(cancel.Token).AsTask();
        Assert.False(pending.IsCompleted);
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
        workspace.Config.BeforeAdmittedSaveMutationForTests = null;
        Directory.Delete(primary + ".bak");
        workspace.Config.SaveConfigAdmitted(workspace.Path, owner); // The failed use did not retire its owner.
        Assert.Equal("de", Config.LoadConfig(workspace.Path).Language);
        Assert.Equal(1, after);
    }

    [Fact]
    public void PreviewDestinationPrevalidationRejectsDirectoryBeforeMutation()
    {
        using Workspace workspace = new();
        workspace.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = workspace.Lease.Admission.EnterOrdinaryWriter();
        string path = System.IO.Path.Combine(workspace.Path, "startup-preview.json");
        string parked = System.IO.Path.Combine(workspace.Path, "startup-preview-prevalidation.parked");
        byte[] original = File.ReadAllBytes(path);
        string unrelated = System.IO.Path.Combine(workspace.Path, ".startup-preview-unrelated.tmp");
        byte[] unrelatedBytes = new byte[] { 0, 1, 0xFF, 42 };
        File.WriteAllBytes(unrelated, unrelatedBytes);
        int before = 0, after = 0;
        workspace.Store.BeforeAdmittedSaveMutationForTests = _ => before++;
        workspace.Store.AfterAdmittedSaveMutationForTests = _ => after++;
        File.Move(path, parked);
        try
        {
            Directory.CreateDirectory(path);
            // The first RejectLinks must fail, before either the observer or temp creation.
            Assert.Throws<InvalidDataException>(() => workspace.Store.SaveAdmitted(Snapshot("new"), owner));
            Assert.Equal(0, before);
            Assert.Equal(0, after);
            Assert.True(Directory.Exists(path));
            Assert.Equal(original, File.ReadAllBytes(parked));
            Assert.Equal(unrelatedBytes, File.ReadAllBytes(unrelated));
            Assert.Equal(new[] { unrelated }, workspace.TemporaryFiles());
            Assert.Equal(42, workspace.Lease.RunStoreWrite(owner, () => 42));
        }
        finally
        {
            workspace.Store.BeforeAdmittedSaveMutationForTests = null;
            if (Directory.Exists(path)) Directory.Delete(path);
            File.Move(parked, path);
        }
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void ActualPreviewLateValidationAndCancellationPreserveCacheAndCleanOnlyOwnedTemporary()
    {
        using Workspace workspace = new();
        workspace.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = workspace.Lease.Admission.EnterOrdinaryWriter();
        string path = System.IO.Path.Combine(workspace.Path, "startup-preview.json");
        string parked = System.IO.Path.Combine(workspace.Path, "startup-preview-late-validation.parked");
        byte[] original = File.ReadAllBytes(path);
        string unrelated = System.IO.Path.Combine(workspace.Path, ".startup-preview-unrelated.tmp");
        byte[] unrelatedBytes = new byte[] { 0, 1, 0xFF, 42 };
        File.WriteAllBytes(unrelated, unrelatedBytes);
        using CancellationTokenSource cancel = new();
        int after = 0, beforeCalls = 0;
        workspace.Store.BeforeAdmittedSaveMutationForTests = token =>
        {
            Assert.Same(owner, token); beforeCalls++; cancel.Cancel();
        };
        workspace.Store.AfterAdmittedSaveMutationForTests = token =>
        {
            Assert.Same(owner, token); after++;
        };
        Assert.ThrowsAny<OperationCanceledException>(() => workspace.Store.SaveAdmitted(Snapshot("new"), owner, cancel.Token));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(unrelatedBytes, File.ReadAllBytes(unrelated));
        Assert.Equal(new[] { unrelated }, workspace.TemporaryFiles());
        Assert.Equal(1, beforeCalls);
        Assert.Equal(0, after);
        workspace.Store.BeforeAdmittedSaveMutationForTests = token =>
        {
            Assert.Same(owner, token);
            beforeCalls++;
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(new[] { unrelated }, workspace.TemporaryFiles());
            // The first RejectLinks has passed. Preserve the real cache, then change
            // its destination type; the second check runs only after temp write/flush.
            File.Move(path, parked);
            Directory.CreateDirectory(path);
        };
        try
        {
            Assert.Throws<InvalidDataException>(() => workspace.Store.SaveAdmitted(Snapshot("new"), owner));
            Assert.Equal(2, beforeCalls);
            Assert.True(Directory.Exists(path));
            Assert.Equal(original, File.ReadAllBytes(parked));
            Assert.Equal(unrelatedBytes, File.ReadAllBytes(unrelated));
            Assert.Equal(new[] { unrelated }, workspace.TemporaryFiles());
            Assert.Equal(0, after);
            Assert.Equal(42, workspace.Lease.RunStoreWrite(owner, () => 42));
        }
        finally
        {
            workspace.Store.BeforeAdmittedSaveMutationForTests = null;
            if (Directory.Exists(path)) Directory.Delete(path);
            if (File.Exists(parked)) File.Move(parked, path);
        }
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(parked));
        workspace.Store.BeforeAdmittedSaveMutationForTests = token =>
        {
            Assert.Same(owner, token); beforeCalls++;
        };
        workspace.Store.SaveAdmitted(Snapshot("retry"), owner);
        Assert.Equal("retry", workspace.Store.Load()!.Rows[0].Title);
        Assert.Equal(3, beforeCalls);
        Assert.Equal(1, after);
        Assert.Equal(unrelatedBytes, File.ReadAllBytes(unrelated));
        Assert.Equal(new[] { unrelated }, workspace.TemporaryFiles());
    }

    [Fact]
    public async Task AlreadyCancelledPreviewEntryDoesNotAcquireAnOwnerAndPostCommitCancellationIsNotFailure()
    {
        using Workspace workspace = new();
        string[] before = workspace.Files();
        int calls = 0;
        workspace.Store.BeforeAdmittedSaveMutationForTests = _ => calls++;
        Assert.ThrowsAny<OperationCanceledException>(() => workspace.Store.Save(Snapshot("new"), new CancellationToken(true)));
        Assert.Equal(before, workspace.Files());
        Assert.Equal(0, calls);
        workspace.Lease.Admission.CompleteStartup();
        using CancellationTokenSource cancel = new();
        workspace.Store.AfterAdmittedSaveMutationForTests = _ => cancel.Cancel();
        workspace.Store.Save(Snapshot("new"), cancel.Token);
        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal("new", workspace.Store.Load()!.Rows[0].Title);
        Assert.Empty(workspace.TemporaryFiles());
        using CancellationTokenSource deadline = new(Deadline);
        using ProfileAdmissionToken exclusive = await workspace.Lease.Admission.EnterExclusiveAsync(deadline.Token);
        Assert.Equal(ProfileAdmissionKind.Exclusive, exclusive.Kind);
    }

    private static StartupPreviewSnapshot Snapshot(string title) => new(
        new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero), 1, 0,
        new[] { new StartupPreviewRow(false, "synthetic", "test", title, 3, 2) });

    private static string[] FileState(string path) => Directory.EnumerateFileSystemEntries(path)
        .Where(entry => System.IO.Path.GetFileName(entry) is not (".meowchan-profile.lock" or ".ochan-profile.lock"))
        .OrderBy(entry => entry, StringComparer.Ordinal)
        .Select(entry => System.IO.Path.GetFileName(entry) + "=" + (Directory.Exists(entry)
            ? "directory" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)))))
        .ToArray();

    private sealed class Workspace : IDisposable
    {
        internal string Path { get; } = TemporaryWorkspacePaths.CreateDirectory("meowchan-direct-settings-");
        internal ProfileLease Lease { get; }
        internal Config Config { get; }
        internal StartupPreviewStore Store { get; }
        private string _previewTitle = "initial";

        internal Workspace(bool seed = true)
        {
            Lease = ProfileLease.Acquire(Path);
            Config = MeowChan.Config.LoadConfig(Path, Lease);
            Store = new(Path, Lease);
            if (seed) { Config.SaveConfig(); Store.Save(Snapshot(_previewTitle)); }
        }

        internal void SetNextPayload() { Config.Language = "de"; _previewTitle = "new"; }
        internal void Save(bool preview)
        {
            if (preview) Store.Save(Snapshot(_previewTitle)); else Config.SaveConfig();
        }
        internal void SaveAdmitted(bool preview, ProfileAdmissionToken token)
        {
            if (preview) Store.SaveAdmitted(Snapshot(_previewTitle), token); else Config.SaveConfigAdmitted(Path, token);
        }
        internal void SetHooks(bool preview, Action<ProfileAdmissionToken>? before, Action<ProfileAdmissionToken>? after)
        {
            if (preview) { Store.BeforeAdmittedSaveMutationForTests = before; Store.AfterAdmittedSaveMutationForTests = after; }
            else { Config.BeforeAdmittedSaveMutationForTests = before; Config.AfterAdmittedSaveMutationForTests = after; }
        }
        internal void AssertSaved(bool preview)
        {
            if (preview) Assert.Equal(_previewTitle, Store.Load()!.Rows[0].Title);
            else Assert.Equal(Config.Language, MeowChan.Config.LoadConfig(Path).Language);
            Assert.Empty(TemporaryFiles());
        }
        internal string[] Files() => FileState(Path);
        internal string[] TemporaryFiles() => Directory.GetFiles(Path, "*.tmp").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        public void Dispose() { Lease.Dispose(); Directory.Delete(Path, recursive: true); }
    }
}
