using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Rules;

namespace MeowChan.DesktopTests;

// Real Host -> ThreadContentRuntime.Rules -> AtomicJsonFile -> active resolver.
[Collection("Delivered plugin hosts")]
public sealed class MediaRuleHostAdmissionIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static readonly ResourceId Resource = new("fixture", "b", "42");
    private static readonly MediaRuleScope Scope = MediaRuleScope.ForThread("fixture", "b", "42");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static MediaRule Rule(bool next) => new(MediaRuleScope.Global, types:
        new(MediaTypeSelectionMode.Selected, [next ? "jpg" : "png"]), sizeLimit:
        new(MediaSizeLimitMode.Limited, next ? 10 : 100));

    private static void AssertResolution(Fixture f, bool next)
    {
        string revision = f.Store.Current.Revision;
        EffectiveMediaRule host = f.Host.ResolveRules(Scope);
        EffectiveMediaRule store = f.Store.Resolve(Resource);
        Assert.Equal(revision, f.Host.MediaRules.Revision);
        foreach (EffectiveMediaRule effective in new[] { host, store, f.Store.Resolve(Scope) })
        {
            Assert.Equal(revision, effective.Revision);
            Assert.Equal(MediaTypeSelectionMode.Selected, effective.Types.Mode);
            Assert.Equal(next ? ".jpg" : ".png", Assert.Single(effective.Types.Extensions));
            Assert.Equal(MediaSizeLimitMode.Limited, effective.SizeLimit.Mode);
            Assert.Equal(next ? 10L : 100L, effective.SizeLimit.Bytes);
            Assert.Equal(MediaRuleScope.Global, effective.TypesOrigin.Scope);
            Assert.Equal(revision, effective.TypesOrigin.Revision);
            Assert.Equal(revision, effective.SizeLimitOrigin.Revision);
        }
        string allowed = next ? ".jpg" : ".png", excluded = next ? ".png" : ".jpg";
        MediaSelectionResult selected = f.Store.Evaluate(Resource, allowed, 5);
        Assert.Equal(MediaSelectionOutcome.Selected, selected.Outcome);
        Assert.Equal(MediaSelectionReason.Allowed, selected.Reason);
        Assert.Equal(revision, selected.EffectiveRule.Revision);
        MediaSelectionResult type = f.Store.Evaluate(Resource, excluded, 5);
        Assert.Equal(MediaSelectionOutcome.Excluded, type.Outcome);
        Assert.Equal(MediaSelectionReason.TypeExcluded, type.Reason);
        MediaSelectionResult size = f.Store.Evaluate(Resource, allowed, 50);
        Assert.Equal(next ? MediaSelectionOutcome.Excluded : MediaSelectionOutcome.Selected, size.Outcome);
        Assert.Equal(next ? MediaSelectionReason.SizeExceeded : MediaSelectionReason.Allowed, size.Reason);
        Assert.Equal(revision, size.EffectiveRule.Revision);
    }

    private static void AssertPublished(Fixture f, bool next)
    {
        AssertResolution(f, next);
        using JsonDocument disk = JsonDocument.Parse(File.ReadAllBytes(f.Primary));
        Assert.Equal(f.Store.Current.Revision, disk.RootElement.GetProperty("Revision").GetString());
        JsonElement rule = Assert.Single(disk.RootElement.GetProperty("Rules").EnumerateArray());
        Assert.Equal(next ? ".jpg" : ".png", Assert.Single(rule.GetProperty("Types").GetProperty("Extensions").EnumerateArray()).GetString());
        Assert.Equal(next ? 10 : 100, rule.GetProperty("SizeLimit").GetProperty("Bytes").GetInt32());
        Assert.Empty(Directory.EnumerateFiles(f.Directory.Path, ".media_rules.json.*.tmp"));
    }

    [Fact]
    public async Task AcceptedRulesCommandFreezesInputBorrowsAcrossExclusiveAndOwnsProjection()
    {
        await using Fixture f = await Fixture.StartAsync();
        using Barrier before = new(), after = new(), projection = new();
        ProfileAdmissionToken? first = null, last = null;
        f.Host.BeforeMediaRuleAdmittedSaveMutationForTests = token => { first = token; before.Hold(); };
        f.Host.AfterMediaRuleAdmittedSaveMutationForTests = token => { last = token; after.Hold(); };
        byte[] oldBytes = File.ReadAllBytes(f.Primary);
        MediaRuleSet oldRules = f.Store.Current;
        SemaphoreSlim commands = Field<SemaphoreSlim>(f.Host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline)); bool held = true;
        Task save = Task.CompletedTask, owner = Task.CompletedTask, capture = Task.CompletedTask;
        TaskCompletionSource granted = Signal(), resume = Signal();
        using CancellationTokenSource cancel = new();
        long version = f.Host.Projection.Version;
        List<MediaRule> input = [Rule(next: true)];
        try
        {
            save = f.Host.SaveRulesAsync(input, oldRules.Revision);
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            input.Clear(); input.Add(Rule(next: false));
            owner = f.Host.RunWithProfileProducerExclusiveAsync(async token =>
            {
                Assert.Equal(ProfileAdmissionKind.Exclusive, token.Kind);
                granted.TrySetResult(); await resume.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            }, cancel.Token);
            Assert.Throws<InvalidOperationException>(() => { using var denied = f.Lease.Admission.EnterOrdinaryWriter(); });
            // Different Host command: prove kernel refusal, not SaveRules action dedup.
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.LoadRuleTemplatesAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => new MediaRuleStore(f.Directory.Path, f.Lease).SaveAsync([], oldRules.Revision));
            capture = Task.Run(() => { lock (Field<object>(f.Host, "_viewCapture")) projection.Hold(); });
            await projection.Entered.Task.WaitAsync(Deadline);
            commands.Release(); held = false;
            await before.Entered.Task.WaitAsync(Deadline);
            Assert.NotNull(first); Assert.Equal(ProfileAdmissionKind.Ordinary, first.Kind);
            Assert.Same(oldRules, f.Store.Current); Assert.Equal(oldBytes, File.ReadAllBytes(f.Primary));
            AssertResolution(f, next: false); f.AssertTemplatesUnchanged();
            Assert.False(save.IsCompleted); Assert.False(granted.Task.IsCompleted);
            before.Release(); await after.Entered.Task.WaitAsync(Deadline);
            Assert.Same(first, last); AssertPublished(f, next: true);
            Assert.NotSame(oldRules, f.Store.Current);
            Assert.Equal(oldBytes, File.ReadAllBytes(f.Primary + ".bak"));
            f.AssertTemplatesUnchanged();
            Assert.False(save.IsCompleted); Assert.False(granted.Task.IsCompleted);
            Task<bool> coreReturned = commands.WaitAsync(Deadline);
            after.Release(); Assert.True(await coreReturned); held = true;
            AssertPublished(f, next: true);
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            Assert.False(save.IsCompleted); Assert.False(granted.Task.IsCompleted);
            Assert.True(f.Lease.Admission.RunWithToken(first!, () => true, ProfileAdmissionKind.Ordinary));
            projection.Release(); commands.Release(); held = false;
            await capture.WaitAsync(Deadline); await save.WaitAsync(Deadline);
            await granted.Task.WaitAsync(Deadline);
            Assert.True(f.Host.Projection.Version > version);
            Assert.Equal(0, f.Host.AcceptedCommandCount);
            AssertPublished(f, next: true); f.AssertTemplatesUnchanged();
            resume.TrySetResult(); await owner.WaitAsync(Deadline);
        }
        finally
        {
            before.Release(); after.Release(); projection.Release(); resume.TrySetResult(); cancel.Cancel();
            if (held) commands.Release();
            await Observe(save); await Observe(owner); await Observe(capture); f.ClearHooks();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownAfterInnerEntryDrainsRealSaveAndResolverPublication(bool stopAfterCommit)
    {
        await using Fixture f = await Fixture.StartAsync();
        byte[] previous = File.ReadAllBytes(f.Primary);
        using Barrier before = new(), after = new();
        ProfileAdmissionToken? first = null, last = null;
        f.Host.BeforeMediaRuleAdmittedSaveMutationForTests = token => { first = token; before.Hold(); };
        f.Host.AfterMediaRuleAdmittedSaveMutationForTests = token => { last = token; after.Hold(); };
        Task save = f.Host.SaveRulesAsync([Rule(next: true)], f.Store.Current.Revision);
        Task stop = Task.CompletedTask;
        try
        {
            await before.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(previous, File.ReadAllBytes(f.Primary)); AssertResolution(f, next: false);
            if (stopAfterCommit) { before.Release(); await after.Entered.Task.WaitAsync(Deadline); }
            stop = f.Host.StopAsync();
            Assert.Equal(DesktopSessionState.Stopping, f.Host.State);
            using CancellationTokenSource cancelObserver = new();
            Task observer = f.Lease.Admission.DrainAsync(cancelObserver.Token);
            cancelObserver.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer);
            Assert.False(stop.IsCompleted); Assert.False(f.Host.IsProfileReleased);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.SaveRulesAsync([], f.Store.Current.Revision));
            before.Release(); await after.Entered.Task.WaitAsync(Deadline);
            Assert.Same(first, last); AssertPublished(f, next: true);
            Assert.Equal(previous, File.ReadAllBytes(f.Primary + ".bak")); f.AssertTemplatesUnchanged();
            Assert.False(save.IsCompleted); Assert.False(stop.IsCompleted); Assert.False(f.Host.IsProfileReleased);
            after.Release(); await save.WaitAsync(Deadline); await stop.WaitAsync(Deadline);
            AssertPublished(f, next: true); f.AssertTemplatesUnchanged();
            Assert.Equal(ProfileAdmissionPhase.Stopped, f.Lease.Admission.Phase);
            Assert.Equal(0, f.Host.AcceptedCommandCount);
        }
        finally { before.Release(); after.Release(); await Observe(save); await Observe(stop); f.ClearHooks(); }
    }

    [Fact]
    public async Task ShutdownBeforeInnerEntryRejectsQueuedAcceptedRulesWithoutMutation()
    {
        await using Fixture f = await Fixture.StartAsync();
        SemaphoreSlim commands = Field<SemaphoreSlim>(f.Host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline)); bool held = true;
        byte[] previous = File.ReadAllBytes(f.Primary); MediaRuleSet oldRules = f.Store.Current;
        int mutations = 0;
        f.Host.BeforeMediaRuleAdmittedSaveMutationForTests = _ => Interlocked.Increment(ref mutations);
        Task save = f.Host.SaveRulesAsync([Rule(next: true)], oldRules.Revision);
        Task stop = Task.CompletedTask;
        try
        {
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            stop = f.Host.StopAsync(); Assert.Equal(DesktopSessionState.Stopping, f.Host.State);
            Assert.False(stop.IsCompleted);
            commands.Release(); held = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => save.WaitAsync(Deadline));
            await stop.WaitAsync(Deadline);
            Assert.Equal(0, mutations); Assert.Equal(previous, File.ReadAllBytes(f.Primary));
            Assert.Same(oldRules, f.Store.Current); AssertPublished(f, next: false); f.AssertTemplatesUnchanged();
            Assert.False(File.Exists(f.Primary + ".bak"));
            Assert.Equal(0, f.Host.AcceptedCommandCount);
        }
        finally { if (held) commands.Release(); await Observe(save); await Observe(stop); f.ClearHooks(); }
    }

    [Fact]
    public async Task ActiveResolverCommitDoesNotReplaceProducerEpochOrTemplateCatalog()
    {
        await using Fixture f = await Fixture.StartAsync();
        ProfileProducerEpoch epoch = Field<ProfileProducerEpoch>(f.Host, "_finalWriteEpoch");
        int before = 0, after = 0;
        f.Host.BeforeMediaRuleAdmittedSaveMutationForTests = _ =>
        { before++; AssertResolution(f, next: false); f.AssertTemplatesUnchanged(); };
        f.Host.AfterMediaRuleAdmittedSaveMutationForTests = _ =>
        {
            after++; AssertPublished(f, next: true); f.AssertTemplatesUnchanged();
            Assert.Same(epoch, Field<ProfileProducerEpoch>(f.Host, "_finalWriteEpoch"));
        };
        try
        {
            await f.Host.SaveRulesAsync([Rule(next: true)], f.Store.Current.Revision).WaitAsync(Deadline);
            Assert.Equal(1, before); Assert.Equal(1, after);
            AssertPublished(f, next: true); f.AssertTemplatesUnchanged();
            Assert.Same(epoch, Field<ProfileProducerEpoch>(f.Host, "_finalWriteEpoch"));
            Assert.Empty(f.Host.GetView().Threads); Assert.Empty(f.Host.GetView().Boards);
        }
        finally { f.ClearHooks(); }
    }

    [Fact]
    public async Task StartingHostRefusesRulesCommandThenReadyHostIssuesFreshOrdinaryAuthority()
    {
        using TempDirectory profile = new();
        SeedConfig(profile.Path);
        await using DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>());
        ProfileLease lease = Field<ProfileLease>(host, "_profileLease");
        MediaRuleStore store = Field<ThreadContentRuntime>(host, "_content").Rules;
        int before = 0, after = 0;
        ProfileAdmissionToken? commandToken = null;
        host.BeforeMediaRuleAdmittedSaveMutationForTests = token => { before++; commandToken = token; };
        host.AfterMediaRuleAdmittedSaveMutationForTests = token => { after++; Assert.Same(commandToken, token); };
        try
        {
            Assert.Equal(DesktopSessionState.Starting, host.State);
            Assert.Equal(ProfileAdmissionPhase.Starting, lease.Admission.Phase);
            string[] untouched = RuleFiles(profile.Path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.SaveRulesAsync([Rule(next: false)], "default").WaitAsync(Deadline));
            Assert.Equal(0, before); Assert.Equal(0, after); Assert.Equal(0, host.AcceptedCommandCount);
            Assert.Equal(untouched, RuleFiles(profile.Path));
            await host.StartAsync().WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Ready, host.State);
            Assert.Equal(ProfileAdmissionPhase.Ready, lease.Admission.Phase);
            Assert.Equal(untouched, RuleFiles(profile.Path)); // Startup loads rules, never saves a draft.
            await host.SaveRulesAsync([Rule(next: false)], store.Current.Revision).WaitAsync(Deadline);
            Assert.Equal(1, before); Assert.Equal(1, after); Assert.NotNull(commandToken);
            Assert.Equal(ProfileAdmissionKind.Ordinary, commandToken.Kind);
            Assert.Equal(MediaSelectionReason.TypeExcluded, store.Evaluate(Resource, ".jpg", 5).Reason);
            Assert.Equal(0, host.AcceptedCommandCount);
            Assert.Throws<InvalidOperationException>(() => lease.Admission.RunWithToken(commandToken, () => true, ProfileAdmissionKind.Ordinary));
        }
        finally
        {
            host.BeforeMediaRuleAdmittedSaveMutationForTests = null;
            host.AfterMediaRuleAdmittedSaveMutationForTests = null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRuleStartupCannotBorrowHostAuthorityOrRetryWithoutNewHost(bool futureSchema)
    {
        using TempDirectory profile = new();
        SeedConfig(profile.Path);
        string path = Path.Combine(profile.Path, "media_rules.json");
        File.WriteAllText(path, futureSchema ? "{\"SchemaVersion\":2}" : "invalid synthetic rules");
        await using DesktopSessionHost host = new(profile.Path, Array.Empty<IImageBoard>());
        ProfileLease lease = Field<ProfileLease>(host, "_profileLease");
        int mutations = 0;
        host.BeforeMediaRuleAdmittedSaveMutationForTests = _ => Interlocked.Increment(ref mutations);
        try
        {
            string[] untouched = RuleFiles(profile.Path);
            await host.StartAsync().WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.RecoveryFailed, host.State);
            Assert.Equal(ProfileAdmissionPhase.RecoveryFailed, lease.Admission.Phase);
            Assert.False(string.IsNullOrWhiteSpace(host.StartupError));
            Assert.Equal(untouched, RuleFiles(profile.Path));
            for (int attempt = 0; attempt < 2; attempt++)
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.SaveRulesAsync([Rule(next: true)], "default").WaitAsync(Deadline));
            Assert.Equal(0, mutations); Assert.Equal(0, host.AcceptedCommandCount);
            Assert.Equal(untouched, RuleFiles(profile.Path));
        }
        finally { host.BeforeMediaRuleAdmittedSaveMutationForTests = null; }
    }

    [Fact]
    public async Task RecoveredRulesRemainPassiveUntilExplicitHostSavePreservesEvidenceAndPublishesResolver()
    {
        await using Fixture f = await Fixture.StartAsync(recoveredRules: true);
        string[] recoveredFiles = RuleFiles(f.Directory.Path);
        byte[] backup = File.ReadAllBytes(f.Primary + ".bak");
        string? warning = Field<ThreadContentRuntime>(f.Host, "_content").LoadWarning;
        Assert.False(string.IsNullOrWhiteSpace(warning));
        Assert.Contains(warning!, f.Host.GetView().Warnings);
        Assert.Equal(CorruptRules, File.ReadAllText(f.Primary));
        AssertResolution(f, next: false);
        Assert.Empty(Directory.EnumerateFiles(f.Directory.Path, "media_rules.json.corrupt-*"));
        ProfileAdmissionToken? commandToken = null;
        int before = 0, after = 0;
        f.Host.BeforeMediaRuleAdmittedSaveMutationForTests = token =>
        {
            before++; commandToken = token;
            Assert.Equal(ProfileAdmissionKind.Ordinary, token.Kind);
            Assert.Equal(recoveredFiles, RuleFiles(f.Directory.Path));
            AssertResolution(f, next: false);
        };
        f.Host.AfterMediaRuleAdmittedSaveMutationForTests = token =>
        {
            after++; Assert.Same(commandToken, token); AssertPublished(f, next: true);
            Assert.Equal(backup, File.ReadAllBytes(f.Primary + ".bak"));
            Assert.Equal(CorruptRules, File.ReadAllText(Assert.Single(Directory.EnumerateFiles(f.Directory.Path, "media_rules.json.corrupt-*"))));
        };
        try
        {
            await f.Host.SaveRulesAsync([Rule(next: true)], f.Store.Current.Revision).WaitAsync(Deadline);
            Assert.Equal(1, before); Assert.Equal(1, after);
            AssertPublished(f, next: true); f.AssertTemplatesUnchanged();
            Assert.Equal(0, f.Host.AcceptedCommandCount);
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(commandToken!, () => true, ProfileAdmissionKind.Ordinary));
        }
        finally { f.ClearHooks(); }
    }

    [Fact]
    public async Task ActualReplaceFailureReleasesHostOwnerAndRetryBorrowsNewCommandThroughProjection()
    {
        await using Fixture f = await Fixture.StartAsync();
        byte[] previous = File.ReadAllBytes(f.Primary);
        MediaRuleSet oldRules = f.Store.Current;
        Exception reference = ReplaceBackupDirectoryReference(f.Directory.Path);
        ProfileAdmissionToken? failedToken = null, retryToken = null;
        int before = 0, after = 0;
        f.Host.BeforeMediaRuleAdmittedSaveMutationForTests = token =>
        {
            before++; failedToken = token;
            // The validated backup was missing. Only now install the blocker;
            // the failure must come from real Atomic temp/write/flush/Replace.
            Directory.CreateDirectory(f.Primary + ".bak");
        };
        f.Host.AfterMediaRuleAdmittedSaveMutationForTests = _ => after++;
        try
        {
            Exception? failure = await Record.ExceptionAsync(() => f.Host.SaveRulesAsync([Rule(next: true)], oldRules.Revision).WaitAsync(Deadline));
            Assert.IsType(reference.GetType(), failure); Assert.Equal(reference.HResult, failure!.HResult);
            Assert.Equal(1, before); Assert.Equal(0, after);
            Assert.Same(oldRules, f.Store.Current); Assert.Equal(previous, File.ReadAllBytes(f.Primary));
            AssertResolution(f, next: false); f.AssertTemplatesUnchanged();
            Assert.Empty(Directory.EnumerateFiles(f.Directory.Path, ".media_rules.json.*.tmp"));
            Assert.Equal(0, f.Host.AcceptedCommandCount);
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(failedToken!, () => true, ProfileAdmissionKind.Ordinary));
        }
        finally
        {
            f.ClearHooks();
            if (Directory.Exists(f.Primary + ".bak")) Directory.Delete(f.Primary + ".bak");
        }
        using Barrier afterRetry = new(), projection = new();
        SemaphoreSlim commands = Field<SemaphoreSlim>(f.Host, "_commands");
        Task save = Task.CompletedTask, capture = Task.CompletedTask;
        f.Host.BeforeMediaRuleAdmittedSaveMutationForTests = token => { retryToken = token; Assert.NotSame(failedToken, token); };
        f.Host.AfterMediaRuleAdmittedSaveMutationForTests = token =>
        {
            Assert.Same(retryToken, token); AssertPublished(f, next: true); afterRetry.Hold();
        };
        try
        {
            capture = Task.Run(() => { lock (Field<object>(f.Host, "_viewCapture")) projection.Hold(); });
            await projection.Entered.Task.WaitAsync(Deadline);
            save = f.Host.SaveRulesAsync([Rule(next: true)], oldRules.Revision); // Same draft/revision, new command authority.
            await afterRetry.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(previous, File.ReadAllBytes(f.Primary + ".bak"));
            Assert.False(save.IsCompleted); Assert.Equal(1, f.Host.AcceptedCommandCount);
            Task<bool> coreReturned = commands.WaitAsync(Deadline);
            afterRetry.Release();
            Assert.True(await coreReturned);
            commands.Release();
            Assert.True(f.Lease.Admission.RunWithToken(retryToken!, () => true, ProfileAdmissionKind.Ordinary));
            Assert.False(save.IsCompleted); // Actual projection still blocked after store return.
            projection.Release(); await capture.WaitAsync(Deadline); await save.WaitAsync(Deadline);
            AssertPublished(f, next: true); f.AssertTemplatesUnchanged();
            Assert.Equal(0, f.Host.AcceptedCommandCount);
            Assert.Throws<InvalidOperationException>(() => f.Lease.Admission.RunWithToken(retryToken!, () => true, ProfileAdmissionKind.Ordinary));
        }
        finally { afterRetry.Release(); projection.Release(); await Observe(save); await Observe(capture); f.ClearHooks(); }
    }

    private const string CorruptRules = "broken synthetic media rules";
    // Deliberately valid current-format backup; no real profile or recovery API fabrication.
    private const string RecoveryBackup = """
        {"SchemaVersion":1,"Revision":"synthetic-recovery","Rules":[{"Scope":{"Kind":0},"Enabled":true,"Types":{"Mode":2,"Extensions":[".png"]},"SizeLimit":{"Mode":2,"Bytes":100}}]}
        """;
    private static void SeedConfig(string path) => new Config
    {
        AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadsPaused = true,
        DownloadPath = Path.Combine(path, "absent-media")
    }.SaveConfig(path);
    private static string[] RuleFiles(string path) => Directory.EnumerateFiles(path)
        .Where(file => Path.GetFileName(file).StartsWith("media_rules.json", StringComparison.Ordinal)
            || Path.GetFileName(file).StartsWith(".media_rules.json.", StringComparison.Ordinal))
        .OrderBy(file => file, StringComparer.Ordinal)
        .Select(file => Path.GetFileName(file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).ToArray();
    private static Exception ReplaceBackupDirectoryReference(string profile)
    {
        string path = Path.Combine(profile, "replace-reference"); Directory.CreateDirectory(path);
        string source = Path.Combine(path, "source.json"), destination = Path.Combine(path, "destination.json"), backup = Path.Combine(path, "backup.json");
        File.WriteAllText(source, "source"); File.WriteAllText(destination, "destination"); Directory.CreateDirectory(backup);
        Exception? failure = Record.Exception(() => File.Replace(source, destination, backup));
        Assert.NotNull(failure);
        Assert.Equal("source", File.ReadAllText(source)); Assert.Equal("destination", File.ReadAllText(destination));
        return failure!;
    }

    private static async Task Observe(Task task) { try { await task.WaitAsync(Deadline); } catch { /* Unwind only; positive awaits above never tolerate failures. */ } }
    private sealed class Barrier : IDisposable
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        private readonly ManualResetEventSlim _release = new();
        internal void Hold() { Entered.TrySetResult(); Assert.True(_release.Wait(Deadline), "Inner barrier was not released."); }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal TempDirectory Directory { get; } = new();
        internal DesktopSessionHost Host { get; }
        internal ProfileLease Lease => Field<ProfileLease>(Host, "_profileLease");
        internal MediaRuleStore Store => Field<ThreadContentRuntime>(Host, "_content").Rules;
        internal string Primary => Path.Combine(Directory.Path, "media_rules.json");
        private RuleTemplateSet _templates = null!;
        private byte[] _templateBytes = null!;
        private Fixture(bool recoveredRules = false)
        {
            new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadsPaused = true,
                DownloadPath = Path.Combine(Directory.Path, "absent-media") }.SaveConfig(Directory.Path);
            if (recoveredRules)
            {
                File.WriteAllText(Primary, CorruptRules);
                File.WriteAllText(Primary + ".bak", RecoveryBackup);
            }
            Host = new(Directory.Path, Array.Empty<IImageBoard>());
        }
        internal static async Task<Fixture> StartAsync(bool recoveredRules = false)
        {
            Fixture f = new(recoveredRules);
            try
            {
                await f.Host.StartAsync().WaitAsync(Deadline);
                Assert.Equal(DesktopSessionState.Ready, f.Host.State);
                await f.Host.LoadRuleTemplatesAsync().WaitAsync(Deadline);
                RuleTemplateStore templates = Field<RuleTemplateStore>(f.Host, "_ruleTemplates");
                f._templates = await f.Host.SaveRuleTemplatesAsync([new RuleTemplate(Guid.NewGuid(), "unrelated template", MediaTypeSelection.All)], templates.Current.Revision).WaitAsync(Deadline);
                f._templateBytes = File.ReadAllBytes(Path.Combine(f.Directory.Path, "rule_templates.json"));
                if (!recoveredRules)
                {
                    await f.Host.SaveRulesAsync([Rule(next: false)], f.Store.Current.Revision).WaitAsync(Deadline);
                    AssertPublished(f, next: false);
                }
                else AssertResolution(f, next: false);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal void AssertTemplatesUnchanged()
        {
            Assert.Same(_templates, Field<RuleTemplateStore>(Host, "_ruleTemplates").Current);
            Assert.Equal(_templateBytes, File.ReadAllBytes(Path.Combine(Directory.Path, "rule_templates.json")));
            Assert.False(File.Exists(Path.Combine(Directory.Path, "rule_templates.json.bak")));
        }
        internal void ClearHooks()
        {
            Host.BeforeMediaRuleAdmittedSaveMutationForTests = null;
            Host.AfterMediaRuleAdmittedSaveMutationForTests = null;
        }
        public async ValueTask DisposeAsync()
        {
            ClearHooks(); await Host.DisposeAsync().AsTask().WaitAsync(Deadline);
            if (Host.IsProfileReleased) Directory.Dispose();
        }
    }
}
