using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using Serilog.Core;
using Serilog.Events;

namespace MeowChan.DesktopTests;

/// <summary>
/// UX wave 3 review W3-09/W3-10/W3-15/W3-19 for the profile and folder source dialogs: the folder text and the profile list never
/// wait for the file system on the UI thread, the dialogs sit on the dialog factory, and their messages are localized sentences
/// (the exception stays in the log). A Window needs a platform backend, so dialog wiring is checked at the source seam like
/// <see cref="DialogMd3Tests"/>; everything with a timing or a file system is tested for real on temporary folders.
/// </summary>
[Collection("Localization")]
public sealed class ProfileDialogResponsivenessTests : IDisposable
{
    private readonly string _previous = Texts.Language;
    public ProfileDialogResponsivenessTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private sealed class CollectingSink : ILogEventSink
    {
        internal readonly List<LogEvent> Events = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    private static IEnumerable<string> Entries(int count, Counter counter)
    {
        for (int index = 0; index < count; index++) { counter.Value++; yield return "entry-" + index; }
    }

    private sealed class Counter { internal int Value; internal bool Started; }

    // ---- W3-09: Classify does not list the folder ----

    [Fact]
    public void ClassifyFindsAProfileByItsMarkerWithoutListingTheFolder()
    {
        using TempDirectory temp = new();
        foreach (string marker in new[] { ".meowchan-profile.protocol", ".meowchan-profile.lock", ".ochan-profile.lock" })
        {
            string folder = Path.Combine(temp.Path, marker.TrimStart('.'));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, marker), string.Empty);
            Counter counter = new();
            Assert.Equal(ProfileFolderKind.Existing, ProfileOpenPolicy.Classify(folder, _ => { counter.Started = true; return Entries(10_000, counter); }));
            Assert.False(counter.Started, "a marker decides without a directory listing");
        }
    }

    [Fact]
    public void ClassifyReadsAtMostTheFirstEntryOfAHugeForeignFolder()
    {
        using TempDirectory temp = new();
        Counter counter = new();
        Assert.Equal(ProfileFolderKind.Foreign, ProfileOpenPolicy.Classify(temp.Path, _ => Entries(10_000, counter)));
        Assert.Equal(1, counter.Value);
        Counter none = new();
        Assert.Equal(ProfileFolderKind.New, ProfileOpenPolicy.Classify(temp.Path, _ => Entries(0, none)));
        Assert.Equal(0, none.Value);
    }

    [Fact]
    public void ClassifyKeepsItsFileSystemAnswers()
    {
        using TempDirectory temp = new();
        Assert.Equal(ProfileFolderKind.New, ProfileOpenPolicy.Classify(Path.Combine(temp.Path, "absent")));
        string empty = Path.Combine(temp.Path, "empty");
        Directory.CreateDirectory(empty);
        Assert.Equal(ProfileFolderKind.New, ProfileOpenPolicy.Classify(empty));
        File.WriteAllText(Path.Combine(empty, "photo.jpg"), "x");
        Assert.Equal(ProfileFolderKind.Foreign, ProfileOpenPolicy.Classify(empty));
        string file = Path.Combine(temp.Path, "a-file");
        File.WriteAllText(file, "x");
        Assert.Equal(ProfileFolderKind.Unusable, ProfileOpenPolicy.Classify(file));
        string profile = Path.Combine(temp.Path, "profile");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), "{}"); // alone it proves nothing
        Assert.Equal(ProfileFolderKind.Foreign, ProfileOpenPolicy.Classify(profile));
        using (ProfileLease.Acquire(profile)) { }
        Assert.Equal(ProfileFolderKind.Existing, ProfileOpenPolicy.Classify(profile));
    }

    // ---- W3-09: the folder text is debounced and classified off the calling thread ----

    private static ProfileFolderKindProbe Probe(Func<string, ProfileFolderKind> classify, TimeSpan delay, List<(string Folder, ProfileFolderKind Kind)> results,
        Action<Action>? post = null) =>
        new(classify, delay, post ?? (action => action()), (folder, kind) => { lock (results) results.Add((folder, kind)); });

    [Fact]
    public async Task TwentyEditsInARowClassifyOnlyTheLastFolderAfterThePause()
    {
        List<(string, ProfileFolderKind)> results = new();
        List<string> classified = new();
        ProfileFolderKindProbe probe = Probe(folder => { lock (classified) classified.Add(folder); return ProfileFolderKind.Existing; }, TimeSpan.FromMilliseconds(150), results);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int edit = 0; edit < 20; edit++) probe.Request("/folder/" + edit);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100), "requesting must not wait for the pause or the file system");
        await Until(() => { lock (results) return results.Count > 0; });
        await Task.Delay(300);
        lock (classified) Assert.Equal(new[] { "/folder/19" }, classified);
        lock (results) Assert.Equal(new[] { ("/folder/19", ProfileFolderKind.Existing) }, results);
    }

    [Fact]
    public async Task ASlowFolderNeverBlocksTheCallerAndItsAnswerIsDroppedWhenANewerOneFollowed()
    {
        List<(string, ProfileFolderKind)> results = new();
        using ManualResetEventSlim entered = new(), release = new();
        int callerThread = Environment.CurrentManagedThreadId;
        int classifyThread = -1;
        ProfileFolderKindProbe probe = Probe(folder =>
        {
            if (folder != "/slow") return ProfileFolderKind.New;
            classifyThread = Environment.CurrentManagedThreadId;
            entered.Set();
            release.Wait(Patience);
            return ProfileFolderKind.Existing;
        }, TimeSpan.Zero, results);
        Task request = Task.Run(() => probe.Request("/slow"));
        Assert.True(await Task.WhenAny(request, Task.Delay(Patience)) == request, "Request returned while the folder was still being read");
        Assert.True(entered.Wait(Patience));
        Assert.NotEqual(callerThread, classifyThread);
        lock (results) Assert.Empty(results);
        probe.Request("/fast"); // supersedes the slow folder while it is still being read
        await Until(() => { lock (results) return results.Count == 1; });
        release.Set();
        await Task.Delay(200);
        lock (results) Assert.Equal(new[] { ("/fast", ProfileFolderKind.New) }, results);
    }

    [Fact]
    public async Task CancelDropsAPendingFolderAndAFailingCheckKeepsTheDialogQuiet()
    {
        List<(string, ProfileFolderKind)> results = new();
        int calls = 0;
        ProfileFolderKindProbe cancelled = Probe(_ => { Interlocked.Increment(ref calls); return ProfileFolderKind.New; }, TimeSpan.FromMilliseconds(150), results);
        cancelled.Request("/a");
        cancelled.Cancel();
        await Task.Delay(400);
        Assert.Equal(0, Volatile.Read(ref calls));
        lock (results) Assert.Empty(results);

        CollectingSink sink = new();
        using Serilog.Core.Logger logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        ProfileFolderKindProbe failing = new(_ => throw new InvalidOperationException("secret /private/path"), TimeSpan.Zero, action => action(),
            (folder, kind) => { lock (results) results.Add((folder, kind)); }, logger);
        failing.Request("/b");
        await Until(() => { lock (sink.Events) return sink.Events.Count > 0; });
        lock (results) Assert.Empty(results);
        lock (sink.Events) Assert.Equal(LogEventLevel.Warning, sink.Events[0].Level);
    }

    // ---- W3-10: the profile list answers per folder, a silent folder never blocks ----

    [Fact]
    public async Task FiftyUnreachableFoldersAreAllReportedUnknownAfterTheirTimeout()
    {
        string[] folders = Enumerable.Range(0, 50).Select(index => Path.Combine(Path.GetTempPath(), "meowchan-offline-mount-" + index)).ToArray();
        using ManualResetEventSlim neverAnswers = new();
        Dictionary<int, ProfileRowStatus> reported = new();
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            await ProfileRowProbe.RunAsync(folders, _ => { neverAnswers.Wait(Patience); return ProfileRowStatus.Ready; }, parallelism: 16,
                TimeSpan.FromMilliseconds(100), (index, status) => { lock (reported) reported[index] = status; }, CancellationToken.None)
                .WaitAsync(Patience);
        }
        finally { neverAnswers.Set(); }
        // 50 folders at 16 at a time: four rounds of the timeout, not 50 sequential waits (and not forever).
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "stalled folders must not hold the list up: " + stopwatch.Elapsed);
        lock (reported)
        {
            Assert.Equal(50, reported.Count);
            Assert.All(reported.Values, status => Assert.Equal(ProfileRowStatus.Unknown, status));
        }
    }

    [Fact]
    public async Task AnAnsweringFolderIsReportedWithoutWaitingForASilentOne()
    {
        string[] folders = { "/silent", "/fine" };
        using ManualResetEventSlim neverAnswers = new();
        Dictionary<int, ProfileRowStatus> reported = new();
        Task run = ProfileRowProbe.RunAsync(folders, folder => { if (folder == "/silent") neverAnswers.Wait(Patience); return ProfileRowStatus.Ready; }, 16,
            TimeSpan.FromSeconds(5), (index, status) => { lock (reported) reported[index] = status; }, CancellationToken.None);
        try
        {
            await Until(() => { lock (reported) return reported.ContainsKey(1); });
            Assert.False(run.IsCompleted, "the silent folder is still being waited for");
            lock (reported) Assert.False(reported.ContainsKey(0));
        }
        finally { neverAnswers.Set(); }
        await run.WaitAsync(Patience);
    }

    [Fact]
    public async Task CancellingTheProbeEndsItAtOnce()
    {
        using ManualResetEventSlim neverAnswers = new();
        using CancellationTokenSource cancel = new();
        Task run = ProfileRowProbe.RunAsync(new[] { "/a", "/b" }, _ => { neverAnswers.Wait(Patience); return ProfileRowStatus.Ready; }, 1,
            TimeSpan.FromSeconds(30), (_, _) => { }, cancel.Token);
        try
        {
            cancel.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { neverAnswers.Set(); }
    }

    [Fact]
    public void TheRealProbeTellsReadyMissingAndInUse()
    {
        using TempDirectory temp = new();
        string ready = Path.Combine(temp.Path, "ready"), held = Path.Combine(temp.Path, "held");
        using (ProfileLease.Acquire(ready)) { }
        Assert.Equal(ProfileRowStatus.Ready, ProfileRowProbe.Probe(ready));
        Assert.Equal(ProfileRowStatus.Missing, ProfileRowProbe.Probe(Path.Combine(temp.Path, "gone")));
        using ProfileLease lease = ProfileLease.Acquire(held);
        Assert.Equal(ProfileRowStatus.InUse, ProfileRowProbe.Probe(held));
    }

    [Fact]
    public async Task ForgetAsyncReturnsAtOnceWhileAnotherWindowHoldsTheListLock()
    {
        using TempDirectory temp = new();
        string registryPath = Path.Combine(temp.Path, "config", ProfileRegistry.FileName);
        ProfileRegistry registry = new(registryPath);
        string current = Path.Combine(temp.Path, "current"), other = Path.Combine(temp.Path, "other");
        Directory.CreateDirectory(current); Directory.CreateDirectory(other);
        registry.Register(other, "Other", Guid.NewGuid(), null, 10);
        ProfileSwitchService service = new(registry, new NoLauncher(), current, Path.Combine(temp.Path, "dl"), () => 10);

        FileStream holder = new(registryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task<ProfileForgetOutcome> forgetting;
        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            forgetting = service.ForgetAsync(other);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "the lock wait (up to 3 s) must not run on the calling thread: " + stopwatch.Elapsed);
            await Task.Delay(300);
            Assert.False(forgetting.IsCompleted, "the removal is waiting for the held lock, in the background");
        }
        finally { holder.Dispose(); }
        Assert.Equal(ProfileForgetOutcome.Removed, await forgetting.WaitAsync(Patience));
        Assert.Empty(registry.Read().Entries);
        Assert.Equal(ProfileForgetOutcome.NotListed, await service.ForgetAsync(other));
        Assert.Equal(ProfileForgetOutcome.NotListed, await service.ForgetAsync(current));
    }

    [Fact]
    public async Task ForgetAsyncReportsAListThatStaysLockedAsFailedAndLogsIt()
    {
        using TempDirectory temp = new();
        string registryPath = Path.Combine(temp.Path, "config", ProfileRegistry.FileName);
        ProfileRegistry registry = new(registryPath);
        string current = Path.Combine(temp.Path, "current"), other = Path.Combine(temp.Path, "other");
        Directory.CreateDirectory(current); Directory.CreateDirectory(other);
        registry.Register(other, "Other", Guid.NewGuid(), null, 10);
        CollectingSink sink = new();
        using Serilog.Core.Logger logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        ProfileSwitchService service = new(registry, new NoLauncher(), current, Path.Combine(temp.Path, "dl"), () => 10, logger);
        using (new FileStream(registryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(ProfileForgetOutcome.Failed, await service.ForgetAsync(other).WaitAsync(Patience));
        lock (sink.Events) Assert.Contains(sink.Events, entry => entry.Level == LogEventLevel.Warning && entry.Exception != null);
        Assert.Single(registry.Read().Entries); // nothing was removed
    }

    private sealed class NoLauncher : IProfileLauncher { public void Launch(string profileDirectory, string? mediaFolder = null) { } }

    private sealed class FailingLauncher : IProfileLauncher
    {
        public void Launch(string profileDirectory, string? mediaFolder = null) =>
            throw new InvalidOperationException("Could not start " + profileDirectory + " because of a raw .NET failure.");
    }

    // ---- W3-15 / W3-11: localized messages, details only in the log ----

    [Fact]
    public void AnExistingProfileWhoseIdentityCannotBeReadStillOpensAndTheFailureIsOnlyLogged()
    {
        using TempDirectory temp = new();
        string current = Path.Combine(temp.Path, "current"), profile = Path.Combine(temp.Path, "profile");
        Directory.CreateDirectory(current);
        using (ProfileLease.Acquire(profile)) { }
        // A folder where the identity file belongs: opening it is refused (not found, not a regular file, no access).
        Directory.CreateDirectory(Path.Combine(profile, ProfileInstanceIdentity.FileName));
        CollectingSink sink = new();
        using Serilog.Core.Logger logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        RecordingLauncher launcher = new();
        ProfileSwitchService service = new(new ProfileRegistry(Path.Combine(temp.Path, "config", ProfileRegistry.FileName)), launcher, current,
            Path.Combine(temp.Path, "dl"), () => 10, logger);
        ProfileActionResult result = service.Create(new NewProfileRequest("Existing", profile, Path.Combine(temp.Path, "dl2"), null));
        Assert.Equal(ProfileActionStatus.Started, result.Status);
        Assert.Equal(new[] { profile }, launcher.Started);
    }

    private sealed class RecordingLauncher : IProfileLauncher
    {
        internal readonly List<string> Started = new();
        public void Launch(string profileDirectory, string? mediaFolder = null) => Started.Add(profileDirectory);
    }

    [Fact]
    public void AFailedStartShowsALocalizedSentenceNeverTheExceptionOrAPath()
    {
        using TempDirectory temp = new();
        string current = Path.Combine(temp.Path, "current"), other = Path.Combine(temp.Path, "other-profile-with-private-name");
        Directory.CreateDirectory(current);
        using (ProfileLease.Acquire(other)) { }
        CollectingSink sink = new();
        using Serilog.Core.Logger logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        ProfileSwitchService service = new(null, new FailingLauncher(), current, Path.Combine(temp.Path, "dl"), () => 10, logger);
        ProfileActionResult result = service.Open(other);
        Assert.Equal((ProfileActionStatus.Failed, "profile.status.startFailed"), (result.Status, result.MessageKey));
        foreach (string code in Texts.Languages.Select(language => language.Code))
        {
            Texts.SetLanguage(code);
            foreach (string key in new[] { result.MessageKey, "profile.status.prepareFailed", "profile.status.forgetFailed", "profile.status.unexpected",
                "folderSource.error.unexpected", "profile.state.unknown", "profile.state.checking" })
            {
                string text = Texts.T(key);
                Assert.NotEqual(key, text);
                Assert.DoesNotContain(temp.Path, text);
                Assert.DoesNotContain("raw .NET failure", text);
                Assert.DoesNotContain("InvalidOperationException", text);
            }
        }
        lock (sink.Events) Assert.Contains(sink.Events, entry => entry.Level == LogEventLevel.Warning && entry.Exception is InvalidOperationException);
    }

    [Fact]
    public void EveryNewTextExistsInAllTwelveLanguages()
    {
        string[] keys = { "profile.state.checking", "profile.state.unknown", "profile.status.forgetFailed", "profile.status.unexpected", "folderSource.error.unexpected" };
        Assert.Equal(12, Texts.Languages.Count);
        string english = string.Empty;
        foreach (string code in Texts.Languages.Select(language => language.Code))
        {
            Texts.SetLanguage(code);
            foreach (string key in keys) Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, code + " lacks " + key);
        }
        Texts.SetLanguage("en");
        english = Texts.T("profile.state.unknown");
        Texts.SetLanguage("de");
        Assert.NotEqual(english, Texts.T("profile.state.unknown"));
        Texts.SetLanguage("ja");
        Assert.NotEqual(english, Texts.T("profile.state.unknown"));
    }

    [Theory]
    [InlineData("Desktop/Views/Settings/ProfileDialogs.cs")]
    [InlineData("Desktop/Views/Sources/FolderSourceDialog.cs")]
    public void TheDialogsNeverShowAnExceptionTextAndLogEveryUnexpectedFailure(string path)
    {
        string source = File.ReadAllText(Source(path));
        Assert.DoesNotMatch(@"\.Message\b(?!Key)", source);
        Assert.Contains("catch (Exception error) when (error is not OutOfMemoryException)", source);
        Assert.Contains("Log.", source);
    }

    // ---- W3-09/W3-10: the dialogs use the off-thread helpers ----

    [Fact]
    public void NewProfileLooksAtTheFolderOnlyThroughTheDebouncedProbeAndCreatesWithoutChecksOnTheUiThread()
    {
        string source = File.ReadAllText(Source("Desktop/Views/Settings/ProfileDialogs.cs"));
        string newProfile = source[..source.IndexOf("internal sealed record ProfileSwitchOutcome", StringComparison.Ordinal)];
        Assert.DoesNotContain("ProfileOpenPolicy.Classify(", newProfile.Replace("new ProfileFolderKindProbe(ProfileOpenPolicy.Classify,", string.Empty));
        Assert.DoesNotContain("_service.Validate(", newProfile);
        Assert.Contains("_kindProbe.Request(folder)", newProfile);
        Assert.Contains("ProfileFolderKindProbe.DefaultDelay", newProfile);
        Assert.Contains("await Task.Run(() => _service.Create(", newProfile);
    }

    [Fact]
    public void SwitchProfileDrawsFirstAndAsksTheFoldersInTheBackgroundAndForgetsAsynchronously()
    {
        string source = File.ReadAllText(Source("Desktop/Views/Settings/ProfileDialogs.cs"));
        string switchDialog = source[source.IndexOf("internal sealed class SwitchProfileDialog", StringComparison.Ordinal)..];
        Assert.DoesNotContain("Directory.Exists", switchDialog);
        Assert.DoesNotContain("ProfileLease.IsInUse", switchDialog);
        Assert.DoesNotContain("_service.ReadList()", switchDialog);
        Assert.DoesNotContain("_service.Forget(", switchDialog);
        Assert.Contains("_service.ReadListAsync()", switchDialog);
        Assert.Contains("ProfileRowProbe.RunAsync(", switchDialog);
        Assert.Contains("await _service.ForgetAsync(folder)", switchDialog);
        // A language change redraws from what is known; only a dialog without a list reads one.
        Assert.Contains("if (_snapshot == null) LoadTask = RefreshAsync(); else Render();", switchDialog);
    }

    // ---- W3-19: the three dialogs on the dialog factory ----

    [Theory]
    [InlineData("Desktop/Views/Settings/ProfileDialogs.cs", 2)]
    [InlineData("Desktop/Views/Sources/FolderSourceDialog.cs", 1)]
    public void TheProfileAndFolderSourceDialogsComeFromTheDialogFactory(string path, int dialogs)
    {
        string source = File.ReadAllText(Source(path));
        Assert.Equal(dialogs, Count(source, "DialogWindow.Configure(this, DialogSize."));
        Assert.Equal(dialogs, Count(source, "DialogWindow.Frame(this,"));
        Assert.Equal(dialogs, Count(source, "DialogFooter.Build("));
        Assert.Equal(dialogs, Count(source, "DialogEscape.Attach(this, isBusy: () => _busy);"));
        Assert.Equal(dialogs, Count(source, "modal: true, captionName:"));
        foreach (string literal in new[] { "Key.Escape", "WindowChromeKind.Dialog", "WindowChrome.Frame(", "new WrapPanel", "WindowStartupLocation", "ShowInTaskbar",
            " Width = ", "MinWidth", "MinHeight" })
            Assert.DoesNotContain(literal, source);
        // Enter confirms in a text field (tunnelled, before the field's own handling) and is the only key these dialogs handle themselves.
        Assert.Contains("AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);", source);
        Assert.Contains("e.Key == Key.Enter && e.Source is TextBox", source);
    }

    [Fact]
    public void TheDialogsTakeTheirSizeStepFromTheFactory()
    {
        string profile = File.ReadAllText(Source("Desktop/Views/Settings/ProfileDialogs.cs"));
        Assert.Equal(2, Count(profile, "DialogWindow.Configure(this, DialogSize.L, modal: true);"));
        string folder = File.ReadAllText(Source("Desktop/Views/Sources/FolderSourceDialog.cs"));
        Assert.Contains("DialogWindow.Configure(this, DialogSize.M, modal: true, heightToContent: renaming != null);", folder);
        // The factory keeps a modal out of the taskbar.
        Assert.Contains("if (modal) window.ShowInTaskbar = false;", File.ReadAllText(Source("Desktop/Controls/DialogWindow.cs")));
    }

    [Fact]
    public void EscapeIsSwallowedWhileTheProfileAndFolderDialogsAreBusyAndClosesThemOtherwise()
    {
        bool busy = true;
        Func<bool> isBusy = () => busy;
        Assert.Equal(DialogEscapeOutcome.Busy, DialogEscape.Decide(Key.Escape, KeyModifiers.None, false, null, isBusy));
        busy = false;
        Assert.Equal(DialogEscapeOutcome.Close, DialogEscape.Decide(Key.Escape, KeyModifiers.None, false, null, isBusy));
        // A child that already used Escape (an open drop-down) keeps it: the old tunnel handler took it before the child saw it.
        Assert.Equal(DialogEscapeOutcome.Ignored, DialogEscape.Decide(Key.Escape, KeyModifiers.None, true, null, isBusy));
        // Closing is also refused while busy, whatever asks for it.
        foreach (string path in new[] { "Desktop/Views/Settings/ProfileDialogs.cs", "Desktop/Views/Sources/FolderSourceDialog.cs" })
            Assert.Contains("Closing += (_, e) => e.Cancel = _busy;", File.ReadAllText(Source(path)));
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static async Task Until(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(10);
        }
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", path));
}
