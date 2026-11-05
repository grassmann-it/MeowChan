using System.Reflection;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Rules;

namespace MeowChan.DesktopTests;

// Real Host -> lazy RuleTemplateStore -> AtomicJsonFile. No substitute save tasks.
[Collection("Delivered plugin hosts")]
public sealed class RuleTemplateHostAdmissionIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static RuleTemplate Value(string name) => new(Guid.NewGuid(), name, MediaTypeSelection.All);
    private static string Primary(Fixture f) => Path.Combine(f.Directory.Path, "rule_templates.json");
    private static void AssertPublished(Fixture f, string name)
    {
        RuleTemplateSet current = f.Store.Current;
        Assert.Equal(name, Assert.Single(current.Templates).Name);
        using JsonDocument disk = JsonDocument.Parse(File.ReadAllBytes(Primary(f)));
        Assert.Equal(current.Revision, disk.RootElement.GetProperty("revision").GetString());
        Assert.Equal(name, Assert.Single(disk.RootElement.GetProperty("templates").EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty(System.IO.Directory.EnumerateFiles(f.Directory.Path, ".rule_templates.json.*.tmp"));
    }

    [Fact]
    public async Task AcceptedHostTemplateSaveBorrowsItsCommandUnderPendingExclusive()
    {
        await using Fixture f = await Fixture.StartAsync();
        using Barrier before = new(), after = new(), projection = new();
        ProfileAdmissionToken? first = null, last = null;
        f.Host.BeforeRuleTemplateAdmittedSaveMutationForTests = token => { first = token; before.Hold(); };
        f.Host.AfterRuleTemplateAdmittedSaveMutationForTests = token => { last = token; after.Hold(); };
        SemaphoreSlim commands = Field<SemaphoreSlim>(f.Host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline)); bool held = true;
        Task save = Task.CompletedTask, owner = Task.CompletedTask, capture = Task.CompletedTask;
        TaskCompletionSource granted = Signal(), resume = Signal();
        using CancellationTokenSource cancel = new();
        long version = f.Host.Projection.Version;
        MediaRuleSet activeRules = f.Host.MediaRules;
        string rulesPath = Path.Combine(f.Directory.Path, "media_rules.json");
        byte[]? rulesBytes = File.Exists(rulesPath) ? File.ReadAllBytes(rulesPath) : null;
        List<RuleTemplate> input = [Value("captured")];
        try
        {
            save = f.Host.SaveRuleTemplatesAsync(input, f.Store.Current.Revision);
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            input.Clear(); input.Add(Value("too late"));
            owner = f.Host.RunWithProfileProducerExclusiveAsync(async token =>
            {
                Assert.Equal(ProfileAdmissionKind.Exclusive, token.Kind);
                granted.TrySetResult(); await resume.Task.WaitAsync(Deadline);
            }, cancel.Token);
            Assert.Throws<InvalidOperationException>(() => { using var denied = f.Lease.Admission.EnterOrdinaryWriter(); });
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.LoadRuleTemplatesAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => new RuleTemplateStore(f.Directory.Path, f.Lease).SaveAsync([], "default"));
            capture = Task.Run(() => { lock (Field<object>(f.Host, "_viewCapture")) projection.Hold(); });
            await projection.Entered.Task.WaitAsync(Deadline);
            commands.Release(); held = false;
            await before.Entered.Task.WaitAsync(Deadline);
            Assert.NotNull(first); Assert.Equal(ProfileAdmissionKind.Ordinary, first.Kind);
            Assert.False(File.Exists(Primary(f))); Assert.Empty(f.Store.Current.Templates);
            Assert.False(save.IsCompleted); Assert.False(granted.Task.IsCompleted);
            before.Release();
            await after.Entered.Task.WaitAsync(Deadline);
            Assert.Same(first, last); AssertPublished(f, "captured");
            Assert.False(save.IsCompleted); Assert.False(granted.Task.IsCompleted);
            Task<bool> coreReturned = commands.WaitAsync(Deadline);
            after.Release();
            Assert.True(await coreReturned); held = true;
            AssertPublished(f, "captured");
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            Assert.False(save.IsCompleted); Assert.False(granted.Task.IsCompleted);
            Assert.True(f.Lease.Admission.RunWithToken(first!, () => true, ProfileAdmissionKind.Ordinary));
            projection.Release(); commands.Release(); held = false;
            await capture.WaitAsync(Deadline); await save.WaitAsync(Deadline);
            await granted.Task.WaitAsync(Deadline);
            Assert.True(f.Host.Projection.Version > version);
            Assert.Equal(0, f.Host.AcceptedCommandCount);
            AssertPublished(f, "captured");
            resume.TrySetResult(); await owner.WaitAsync(Deadline);
            Assert.Same(activeRules, f.Host.MediaRules);
            if (rulesBytes == null) Assert.False(File.Exists(rulesPath));
            else Assert.Equal(rulesBytes, File.ReadAllBytes(rulesPath));
        }
        finally
        {
            before.Release(); after.Release(); projection.Release(); resume.TrySetResult(); cancel.Cancel();
            if (held) commands.Release();
            await Observe(save); await Observe(owner); await Observe(capture);
            f.ClearHooks();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownWaitsForPinnedTemplateIoCleanupAndRealReturn(bool stopAfterCommit)
    {
        await using Fixture f = await Fixture.StartAsync();
        // Produce a real previous primary so replacement must also create a backup.
        await f.Host.SaveRuleTemplatesAsync([Value("previous")], f.Store.Current.Revision).WaitAsync(Deadline);
        byte[] previous = File.ReadAllBytes(Primary(f));
        using Barrier before = new(), after = new();
        ProfileAdmissionToken? first = null, last = null;
        f.Host.BeforeRuleTemplateAdmittedSaveMutationForTests = token => { first = token; before.Hold(); };
        f.Host.AfterRuleTemplateAdmittedSaveMutationForTests = token => { last = token; after.Hold(); };
        Task save = f.Host.SaveRuleTemplatesAsync([Value("committed")], f.Store.Current.Revision);
        Task stop = Task.CompletedTask;
        try
        {
            await before.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(previous, File.ReadAllBytes(Primary(f)));
            Assert.Equal("previous", Assert.Single(f.Store.Current.Templates).Name);
            if (stopAfterCommit) { before.Release(); await after.Entered.Task.WaitAsync(Deadline); }
            stop = f.Host.StopAsync();
            Assert.Equal(DesktopSessionState.Stopping, f.Host.State);
            using CancellationTokenSource cancelObserver = new();
            Task observer = f.Lease.Admission.DrainAsync(cancelObserver.Token);
            cancelObserver.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer);
            Assert.False(stop.IsCompleted); Assert.False(f.Host.IsProfileReleased);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.SaveRuleTemplatesAsync([], f.Store.Current.Revision));
            before.Release(); await after.Entered.Task.WaitAsync(Deadline);
            Assert.Same(first, last); AssertPublished(f, "committed");
            Assert.Equal(previous, File.ReadAllBytes(Primary(f) + ".bak"));
            Assert.False(save.IsCompleted); Assert.False(stop.IsCompleted);
            Assert.False(f.Host.IsProfileReleased);
            after.Release(); await save.WaitAsync(Deadline); await stop.WaitAsync(Deadline);
            AssertPublished(f, "committed");
            Assert.Equal(ProfileAdmissionPhase.Stopped, f.Lease.Admission.Phase);
            Assert.Equal(0, f.Host.AcceptedCommandCount);
        }
        finally { before.Release(); after.Release(); await Observe(save); await Observe(stop); f.ClearHooks(); }
    }

    [Fact]
    public async Task AcceptedButQueuedTemplateCommandCannotInventSaveAfterShutdown()
    {
        await using Fixture f = await Fixture.StartAsync();
        SemaphoreSlim commands = Field<SemaphoreSlim>(f.Host, "_commands");
        Assert.True(await commands.WaitAsync(Deadline)); bool held = true;
        int mutations = 0;
        f.Host.BeforeRuleTemplateAdmittedSaveMutationForTests = _ => Interlocked.Increment(ref mutations);
        Task save = f.Host.SaveRuleTemplatesAsync([Value("rejected")], f.Store.Current.Revision);
        Task stop = Task.CompletedTask;
        try
        {
            Assert.Equal(1, f.Host.AcceptedCommandCount);
            stop = f.Host.StopAsync();
            Assert.Equal(DesktopSessionState.Stopping, f.Host.State);
            Assert.False(stop.IsCompleted);
            commands.Release(); held = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => save.WaitAsync(Deadline));
            await stop.WaitAsync(Deadline);
            Assert.Equal(0, mutations); Assert.False(File.Exists(Primary(f)));
            Assert.Empty(f.Store.Current.Templates); Assert.Equal(0, f.Host.AcceptedCommandCount);
        }
        finally { if (held) commands.Release(); await Observe(save); await Observe(stop); f.ClearHooks(); }
    }

    [Fact]
    public async Task TemplateOnlyCommitPublishesCurrentWithoutReplacingActiveRulesOrProducerEpoch()
    {
        await using Fixture f = await Fixture.StartAsync();
        MediaRuleSet rules = f.Host.MediaRules;
        ProfileProducerEpoch epoch = Field<ProfileProducerEpoch>(f.Host, "_finalWriteEpoch");
        int before = 0, after = 0;
        f.Host.BeforeRuleTemplateAdmittedSaveMutationForTests = _ => before++;
        f.Host.AfterRuleTemplateAdmittedSaveMutationForTests = _ =>
        {
            after++;
            AssertPublished(f, "inactive catalog only");
            Assert.Same(rules, f.Host.MediaRules);
            Assert.Same(epoch, Field<ProfileProducerEpoch>(f.Host, "_finalWriteEpoch"));
        };
        try
        {
            await f.Host.SaveRuleTemplatesAsync([Value("inactive catalog only")], f.Store.Current.Revision).WaitAsync(Deadline);
            Assert.Equal(1, before); Assert.Equal(1, after);
            AssertPublished(f, "inactive catalog only");
            Assert.Same(rules, f.Host.MediaRules);
            Assert.Same(epoch, Field<ProfileProducerEpoch>(f.Host, "_finalWriteEpoch"));
            Assert.Empty(f.Host.GetView().Threads); Assert.Empty(f.Host.GetView().Boards);
        }
        finally { f.ClearHooks(); }
    }

    private static async Task Observe(Task task) { try { await task.WaitAsync(Deadline); } catch { /* Preserve the primary assertion on unwind only. */ } }
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
        internal RuleTemplateStore Store => Field<RuleTemplateStore>(Host, "_ruleTemplates");
        private Fixture()
        {
            new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadsPaused = true,
                DownloadPath = Path.Combine(Directory.Path, "absent-media") }.SaveConfig(Directory.Path);
            Host = new(Directory.Path, Array.Empty<IImageBoard>());
        }
        internal static async Task<Fixture> StartAsync()
        {
            Fixture f = new();
            try
            {
                await f.Host.StartAsync().WaitAsync(Deadline);
                Assert.Equal(DesktopSessionState.Ready, f.Host.State);
                await f.Host.LoadRuleTemplatesAsync().WaitAsync(Deadline);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal void ClearHooks()
        {
            Host.BeforeRuleTemplateAdmittedSaveMutationForTests = null;
            Host.AfterRuleTemplateAdmittedSaveMutationForTests = null;
        }
        public async ValueTask DisposeAsync()
        {
            ClearHooks();
            await Host.DisposeAsync().AsTask().WaitAsync(Deadline);
            if (Host.IsProfileReleased) Directory.Dispose();
        }
    }
}
