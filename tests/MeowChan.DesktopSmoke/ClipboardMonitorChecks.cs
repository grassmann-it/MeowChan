using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Boards.Base;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Updates;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Real native Avalonia windows + managed routed input, NOT physical/OS keyboard input.
// Root hook, after the owner's Ready/empty barrier: MEOWCHAN_CLIPBOARD_MONITOR_ONLY=1.
// Only the timer cadence is extended; production Poll/Settings/admission/shutdown are used.
internal static class ClipboardMonitorChecks
{
    private const string Host = "clipboard-monitor.invalid";
    private const string First = "https://" + Host + "/b/thread/101";
    private const string Second = "https://" + Host + "/b/thread/102";
    private const string Newer = "https://" + Host + "/b/thread/103";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task RunAsync(MainWindow owner)
    {
        // Matches ClipboardChecks' existing X11 isolation seam. Fail closed rather than
        // silently reporting a PASS/skip on a platform whose clipboard we cannot isolate.
        Require(OperatingSystem.IsLinux(), "Clipboard monitor fixture requires Linux/X11; no native clipboard was read.");
        Dispatcher.UIThread.VerifyAccess();
        string language = Texts.Language;
        var theme = Application.Current!.RequestedThemeVariant;
        string root = TemporaryWorkspacePaths.CreateDirectory("clipboard-monitor-smoke-");
        List<Exception> failures = new();
        try
        {
            // Separate journeys keep a red keyboard contract from hiding lifecycle evidence.
            await CaseAsync("settings-off-save-discard", false, SettingsAsync);
            await CaseAsync("passive-dismiss-repeat", true, PassiveAsync);
            await CaseAsync("frozen-button-enter", true, FrozenAcceptAsync);
            await CaseAsync("dismiss-late-read", true, DismissLateReadAsync);
            await CaseAsync("disable-late-read", true, DisableLateReadAsync);
            await CaseAsync("modal-and-close-late-read", true, CloseLateReadAsync);
            await CaseAsync("textbox-enter-is-not-confirmation", true, TextboxEnterAsync);
        }
        finally
        {
            Texts.SetLanguage(language);
            Application.Current.RequestedThemeVariant = theme;
            owner.Activate();
            // Deliberately retain private fixture directories; no artifact cleanup.
        }
        if (failures.Count != 0) throw new AggregateException("Clipboard monitor integration failed.", failures);
        Console.WriteLine("SMOKE clipboard monitor PASS: private host, settings Save/Discard, passive banner, frozen local-button Enter admission, dismissal/repeat, opt-out/modal/close lifetime. Native windows; managed routed input only; terminal fake HTTP, no live internet/media/profile.");

        async Task CaseAsync(string name, bool enabled, Func<Fixture, Task> check)
        {
            try
            {
                await using Fixture fixture = await Fixture.OpenAsync(owner, Path.Combine(root, name), enabled);
                await check(fixture);
                Console.WriteLine("SMOKE clipboard monitor case PASS: " + name);
            }
            catch (Exception error)
            {
                Console.WriteLine("SMOKE clipboard monitor case FAIL: " + name + ": " + error.Message);
                failures.Add(new InvalidOperationException(name, error));
            }
        }
    }

    private static async Task SettingsAsync(Fixture f)
    {
        f.Clipboard.Text = First;
        await f.PollAsync();
        await f.PollAsync();
        Require(f.Clipboard.Reads == 0 && !f.Panel.IsVisible, "Default OFF read the clipboard or offered URLs.");
        // Even a stale/programmatic Click must not bypass opt-out. Real handler, no direct admission.
        f.Accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await DrainAsync();
        f.RequirePassive("OFF acceptance guard");
        await f.OpenSettingsAsync();
        CheckBox toggle = C<CheckBox>(f.Shell.SettingsEditor, "MonitorClipboardUrlsInput");
        Require(toggle.IsEffectivelyVisible && toggle.IsChecked == false, "Visible setting did not start OFF.");
        toggle.IsChecked = true;
        await DrainAsync();
        Require(f.Shell.SettingsEditor.MonitorClipboardUrls && !f.Session.Config.MonitorClipboardUrls,
            "Unsaved checkbox mutated live config or failed its two-way binding.");
        await f.PollAsync();
        Require(f.Clipboard.Reads == 0 && !Config.LoadConfig(f.Profile).MonitorClipboardUrls,
            "Unsaved opt-in read clipboard or persisted itself.");
        Click(C<Button>(f.Shell.SettingsEditor, "CancelSettingsButton"));
        await DrainAsync();
        Require(toggle.IsChecked == false && !f.Shell.SettingsEditor.MonitorClipboardUrls,
            "Actual Discard retained the opt-in draft.");
        await f.SetEnabledAsync(true);
        await f.PollAsync();
        await f.RequireBannerAsync(First);
        f.RequirePassive("Saved ON, before confirmation");
        await f.OpenSettingsAsync();
        toggle.IsChecked = false;
        await DrainAsync();
        Require(!f.Shell.SettingsEditor.MonitorClipboardUrls && f.Session.Config.MonitorClipboardUrls,
            "Unsaved opt-out mutated live config.");
        Click(C<Button>(f.Shell.SettingsEditor, "CancelSettingsButton"));
        await DrainAsync();
        Require(toggle.IsChecked == true && f.Shell.SettingsEditor.MonitorClipboardUrls
            && Config.LoadConfig(f.Profile).MonitorClipboardUrls, "Actual Discard lost saved opt-in.");
        await f.SetEnabledAsync(false);
        int reads = f.Clipboard.Reads;
        await f.PollAsync();
        f.Accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await DrainAsync();
        Require(f.Clipboard.Reads == reads && !f.Panel.IsVisible && string.IsNullOrEmpty(f.Offered.Text),
            "Saved OFF retained actionable URLs or performed another read.");
        f.RequirePassive("Saved OFF");
    }

    private static async Task PassiveAsync(Fixture f)
    {
        f.Clipboard.Text = First + "," + First + "\n" + Second
            + "\nhttps://foreign.invalid/b/thread/999";
        int reads = f.Clipboard.Reads;
        await f.PollAsync();
        await f.RequireBannerAsync(First, Second);
        Require(f.Clipboard.Reads == reads + 1, "A poll did not perform exactly one isolated read.");
        Require(C<TextBlock>(f.Shell, "ClipboardSuggestionTitle").Text == Texts.T("clipboard.monitor.found", 2),
            "Banner count disagrees with frozen, deduplicated supported URLs.");
        f.RequirePassive("Detection");
        for (int i = 0; i < 3; i++) await f.PollAsync();
        await f.RequireBannerAsync(First, Second);
        f.RequirePassive("Repeated polls");
        Click(C<Button>(f.Shell, "ClipboardSuggestionDismiss"));
        Require(!f.Panel.IsVisible && string.IsNullOrEmpty(f.Offered.Text), "Dismiss did not clear suggestion.");
        await f.PollAsync();
        f.Clipboard.Text = "Copied again: " + First + "\n" + Second;
        await f.PollAsync();
        Require(!f.Panel.IsVisible, "Dismissed URL batch nagged after unchanged/reformatted clipboard.");
        f.RequirePassive("Dismiss and repeated poll");
        f.Clipboard.Text = Newer;
        await f.PollAsync();
        await f.RequireBannerAsync(Newer);
        f.RequirePassive("New batch after dismiss");
    }

    private static async Task FrozenAcceptAsync(Fixture f)
    {
        f.Clipboard.Text = First + "\n" + Second;
        await f.PollAsync();
        await f.RequireBannerAsync(First, Second);
        f.RequirePassive("Frozen confirmation precondition");
        int reads = f.Clipboard.Reads;
        f.Clipboard.Text = Newer; // No subsequent poll: confirmation must use the displayed batch.
        f.Accept.Focus();
        await Program.WaitUntilAsync(() => f.Accept.IsFocused);
        KeyPair(f.Accept, Key.Enter); // Framework Button.OnKeyDown -> Click, never a fabricated Click fallback.
        await Program.WaitUntilAsync(() => f.Shell.UrlList.Count == 2 && C<Button>(f.Shell, "AddUrlButton").IsEnabled);
        await Program.WaitUntilAsync(() => f.Transport.Requests.Count == 2);
        Require(f.Shell.UrlList.Select(row => row.Url).Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { First, Second }, StringComparer.Ordinal), "Enter did not admit exactly the displayed frozen list.");
        Require(f.Transport.Requests.Order(StringComparer.Ordinal).SequenceEqual(new[] { First, Second }, StringComparer.Ordinal)
            && f.Clipboard.Reads == reads && !f.Panel.IsVisible && f.Session.GetThreadWorkSnapshots().Count == 2,
            "Confirmation reread clipboard, started wrong work or retained banner.");
        await f.PollAsync();
        await f.RequireBannerAsync(Newer);
        f.Clipboard.Text = First + "\n" + Second;
        await f.PollAsync();
        Require(!f.Panel.IsVisible && f.Shell.UrlList.Count == 2, "Already watched/accepted URLs were offered again.");
    }

    private static async Task DismissLateReadAsync(Fixture f)
    {
        f.Clipboard.Text = First;
        await f.PollAsync();
        await f.RequireBannerAsync(First);
        TaskCompletionSource<IAsyncDataTransfer?> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Clipboard.Read = () => pending.Task;
        int beforeRead = f.Clipboard.Reads;
        Task poll = f.Shell.PollClipboardMonitorAsync();
        try
        {
            Require(f.Clipboard.Reads == beforeRead + 1 && !poll.IsCompleted,
                "Dismiss-late-read fixture never reached its pending platform read.");
            Click(C<Button>(f.Shell, "ClipboardSuggestionDismiss"));
            pending.SetResult(TextData(Second));
            await poll.WaitAsync(Deadline);
            Require(!f.Panel.IsVisible && string.IsNullOrEmpty(f.Offered.Text), "Dismiss resurrected a late in-flight read.");
            f.RequirePassive("Dismiss during read");
            f.Clipboard.Read = null;
            f.Clipboard.Text = Second;
            await f.PollAsync();
            await f.RequireBannerAsync(Second);
        }
        finally { pending.TrySetResult(null); await poll.WaitAsync(Deadline); }
    }

    private static async Task DisableLateReadAsync(Fixture f)
    {
        TaskCompletionSource<IAsyncDataTransfer?> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Clipboard.Read = () => pending.Task;
        int beforeRead = f.Clipboard.Reads;
        Task poll = f.Shell.PollClipboardMonitorAsync();
        try
        {
            Require(f.Clipboard.Reads == beforeRead + 1 && !poll.IsCompleted,
                "Opt-out-late-read fixture never reached its pending platform read.");
            int reads = f.Clipboard.Reads;
            await f.PollAsync();
            Require(f.Clipboard.Reads == reads && reads > 0, "Overlapping poll issued a second clipboard read.");
            await f.SetEnabledAsync(false); // Real Settings save while platform read is unresolved.
            await f.SetEnabledAsync(true);  // Re-enable must not revive the previous generation.
            pending.SetResult(TextData(First));
            await poll.WaitAsync(Deadline);
            Require(!f.Panel.IsVisible && string.IsNullOrEmpty(f.Offered.Text), "Late read survived saved OFF/ON generation change.");
            f.RequirePassive("Late read after real opt-out/opt-in");
            f.Clipboard.Read = null;
            f.Clipboard.Text = Second;
            await f.PollAsync();
            await f.RequireBannerAsync(Second);
            f.RequirePassive("Recovered after stale read");
        }
        finally { pending.TrySetResult(null); await poll.WaitAsync(Deadline); }
    }

    private static async Task CloseLateReadAsync(Fixture f)
    {
        // Real ShowDialog lifetime, not a substituted canScan predicate.
        Window modal = new() { Title = "Clipboard smoke blocker", Width = 260, Height = 120 };
        Task dialog = modal.ShowDialog(f.Shell);
        try
        {
            await Program.WaitUntilAsync(() => modal.IsVisible);
            int reads = f.Clipboard.Reads;
            f.Clipboard.Text = First;
            await f.PollAsync();
            Require(f.Clipboard.Reads == reads && !f.Panel.IsVisible, "Modal owner was scanned/offered URLs.");
            f.RequirePassive("Visible shell modal");
        }
        finally { modal.Close(); await dialog.WaitAsync(Deadline); }
        TaskCompletionSource<IAsyncDataTransfer?> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Clipboard.Read = () => pending.Task;
        int beforeCloseRead = f.Clipboard.Reads;
        Task poll = f.Shell.PollClipboardMonitorAsync();
        try
        {
            Require(f.Clipboard.Reads == beforeCloseRead + 1 && !poll.IsCompleted,
                "Close-late-read fixture never reached its pending platform read.");
            await f.CloseAsync();
            pending.SetResult(TextData(First));
            await poll.WaitAsync(Deadline);
            int reads = f.Clipboard.Reads;
            await f.PollAsync();
            Require(f.Clipboard.Reads == reads && !f.Panel.IsVisible && TimerField.GetValue(f.Shell) == null,
                "Closed shell read again, displayed a late suggestion or retained its timer.");
            f.RequirePassive("Late read after shutdown");
        }
        finally { pending.TrySetResult(null); await poll.WaitAsync(Deadline); }
    }

    private static async Task TextboxEnterAsync(Fixture f)
    {
        f.Clipboard.Text = First;
        await f.PollAsync();
        await f.RequireBannerAsync(First);
        // UrlInput intentionally has its OWN explicit Add contract; do not confuse
        // that with accepting a clipboard suggestion. Use an unrelated search field.
        TextBox search = C<TextBox>(f.Shell, "DownloadSearchInput");
        f.Shell.FocusDownloadSearch();
        await Program.WaitUntilAsync(() => search.IsFocused);
        KeyPair(search, Key.Enter);
        await DrainAsync();
        f.RequirePassive("Enter in unrelated search TextBox");
        await f.RequireBannerAsync(First);
        // Strict user contract: even the preview is text, not the confirmation button.
        // A reachable preview-Enter acceptance branch is a regression here; do not weaken.
        f.Offered.Focus();
        await Program.WaitUntilAsync(() => f.Offered.IsFocused);
        KeyPair(f.Offered, Key.Enter);
        await DrainAsync();
        await Program.WaitUntilAsync(() => C<Button>(f.Shell, "AddUrlButton").IsEnabled);
        f.RequirePassive("Enter in URL preview TextBox must not accept (button-only contract)");
        await f.RequireBannerAsync(First);
    }

    private static void KeyPair(Control source, Key key)
    {
        source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.None });
        source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = KeyModifiers.None });
    }
    private static async Task DrainAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static void Click(Button button)
    {
        Require(button.IsEffectivelyVisible && button.IsEffectivelyEnabled, "Action not available: " + button.Name);
        SmokeActionClick.Execute(button);
    }
    private static T C<T>(Control root, string name) where T : Control => root.FindControl<T>(name)
        ?? throw new InvalidOperationException("Missing clipboard smoke control: " + name);
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static DataTransfer TextData(string text)
    {
        DataTransfer data = new();
        data.Add(DataTransferItem.Create(DataFormat.Text, text));
        return data;
    }
    private static readonly FieldInfo TimerField = typeof(MainWindow).GetField("_clipboardUrlTimer", PrivateInstance)
        ?? throw new InvalidOperationException("Clipboard monitor timer seam changed.");

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly IDisposable _scope;
        private readonly HttpClient _client;
        private bool _closed;
        private string _lastPollBefore = "not polled";
        private string _lastPollAfter = "not polled";
        internal string Profile { get; }
        internal FakeClipboard Clipboard { get; } = new();
        internal TerminalHttp Transport { get; } = new();
        internal DesktopSessionHost Session { get; private set; } = null!;
        internal MainWindow Shell { get; private set; } = null!;
        internal Border Panel => C<Border>(Shell, "ClipboardSuggestionPanel");
        internal TextBox Offered => C<TextBox>(Shell, "ClipboardSuggestionUrls");
        internal Button Accept => C<Button>(Shell, "ClipboardSuggestionAccept");

        private Fixture(string profile)
        {
            Profile = profile;
            _scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<IClipboard>().ToConstant(Clipboard);
            _client = new HttpClient(Transport);
        }
        internal static async Task<Fixture> OpenAsync(MainWindow owner, string profile, bool enabled)
        {
            Fixture f = new(profile);
            try
            {
                string media = Path.Combine(profile, "empty-output");
                new Config { DownloadPath = media, MonitorClipboardUrls = enabled, FirstRunSetupVersion = 1,
                    AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadsPaused = false,
                    MinimumFreeSpaceGiB = 0, CloseToTray = false, MinimizeToTray = false,
                    WorkspaceMode = "Advanced", Language = Texts.Language, RecheckTimer = 3600 }.SaveConfig(profile);
                Directory.CreateDirectory(media);
                f.Session = new DesktopSessionHost(profile, new IImageBoard[] { new FakeProvider(f._client) },
                    updateSessionFactory: (path, config) => new UpdateSession(path, config, f._client, disableEnvironment: () => "1"));
                f.Shell = new MainWindow(f.Session);
                // Assert before Show/start/poll; never read or write the real desktop clipboard.
                Require(ReferenceEquals(f.Shell.Clipboard, f.Clipboard), "X11 clipboard isolation failed; refusing to show/poll fixture.");
                ((DispatcherTimer)TimerField.GetValue(f.Shell)!).Interval = TimeSpan.FromHours(1);
                f.Shell.Show(owner);
                await f.Session.StartAsync().WaitAsync(Deadline);
                await Program.WaitUntilAsync(() => f.Shell.IsVisible && C<Button>(f.Shell, "AddUrlButton").IsEnabled);
                await DrainAsync();
                Require(f.Shell.TryGetPlatformHandle()?.Handle is nint handle && handle != 0,
                    "Fixture has no genuine native window handle.");
                Require(f.Session.GetProviderDescriptors().Any(p => p.SupportedHosts.Contains(Host)),
                    "Synthetic host was not published through the real provider projection.");
                f.RequirePassive("Private startup");
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal async Task PollAsync()
        {
            // Read-only observations, never another poll/read or a readiness override.
            // Retain only the latest pair so diagnostics stay bounded across journeys.
            _lastPollBefore = DescribeMonitorState();
            _lastPollAfter = "poll did not complete";
            await Shell.PollClipboardMonitorAsync().WaitAsync(Deadline);
            _lastPollAfter = DescribeMonitorState();
        }
        private string DescribeMonitorState()
        {
            object? Property(object source, string name) => source.GetType()
                .GetProperty(name, PrivateInstance | BindingFlags.Public)?.GetValue(source);
            object? Field(string name) => typeof(MainWindow).GetField(name, PrivateInstance)?.GetValue(Shell);
            object? monitor = Field("_clipboardUrlMonitor");
            var applied = Field("_appliedSessionProjection") as DesktopSessionProjection;
            DispatcherTimer? timer = TimerField.GetValue(Shell) as DispatcherTimer;
            int? pendingCount = monitor != null && Property(monitor, "Pending") is IReadOnlyList<string> pending
                ? pending.Count : null;
            // Synthetic payload values are deliberately omitted; counts suffice to
            // separate no-read, no-offer, hidden, unarranged and disabled-action failures.
            return $"config={Session.Config.MonitorClipboardUrls}, draft={Shell.SettingsEditor.MonitorClipboardUrls}, "
                + $"state={Session.State}, page={Shell.SelectedPage}, visible={Shell.IsVisible}, enabled={Shell.IsEnabled}, window={Shell.WindowState}, "
                + $"canScan={Property(Shell, "CanScanClipboard")}, canShow={Property(Shell, "CanShowClipboardSuggestion")}, "
                + $"opened={Field("_hasOpened")}, restored={Field("_restoreCompleted")}, closing={Field("_isClosing")}, importing={Field("_importing")}, "
                + $"applyingPreferences={Field("_applyingPreferences")}, editorBusy={Shell.SettingsEditor.IsBusy}, editorAvailable={Shell.SettingsEditor.IsAvailable}, "
                + $"rowsApplying={Shell.SessionRowsApplying}, rowBusy={Property(Shell, "RowCommandBusy")}, "
                + $"projection={Session.Projection.Version}, appliedProjection={applied?.Version}, "
                + $"visibleOwnedWindows={Shell.OwnedWindows.Count(window => window.IsVisible)}, "
                + $"blockingDecision={typeof(MainWindow).GetMethod("HasBlockingPageDecision", PrivateInstance)?.Invoke(Shell, null)}, "
                + $"largeReader={Field("_largeReaderOpen")}, galleryPresent={Field("_gallery") != null}, "
                + $"reads={Clipboard.Reads}, writes={Clipboard.Writes}, clipboardIsPrivate={ReferenceEquals(Shell.Clipboard, Clipboard)}, "
                + $"pendingCount={pendingCount}, "
                + $"panelVisible={Panel.IsVisible}, panelEffective={Panel.IsEffectivelyVisible}, arranged={Panel.IsArrangeValid}, bounds={Panel.Bounds}, "
                + $"acceptVisible={Accept.IsEffectivelyVisible}, acceptEnabled={Accept.IsEffectivelyEnabled}, offeredLength={Offered.Text?.Length ?? 0}, "
                + $"timerEnabled={timer?.IsEnabled}, timerInterval={timer?.Interval}";
        }
        internal async Task OpenSettingsAsync()
        {
            Click(C<Button>(Shell, "SettingsNavigationButton"));
            await Program.WaitUntilAsync(() => Shell.SettingsPageVisible && Shell.SettingsEditor.IsEffectivelyVisible);
            await SettingsSectionChecks.OpenAsync(Shell.SettingsEditor, "settings.behavior");
        }
        internal async Task SetEnabledAsync(bool enabled)
        {
            await OpenSettingsAsync();
            SettingsEditor editor = Shell.SettingsEditor;
            C<CheckBox>(editor, "MonitorClipboardUrlsInput").IsChecked = enabled;
            await DrainAsync();
            Require(editor.MonitorClipboardUrls == enabled, "Monitor checkbox binding did not update Settings draft.");
            int saves = 0;
            void Saved(object? sender, EventArgs args) => saves++;
            editor.Saved += Saved;
            try
            {
                Click(C<Button>(editor, "SaveSettingsButton"));
                await Program.WaitUntilAsync(() => !editor.IsBusy && (saves == 1 || editor.HasError));
                Require(saves == 1 && !editor.HasError && Session.Config.MonitorClipboardUrls == enabled
                    && Config.LoadConfig(Profile).MonitorClipboardUrls == enabled, "Real Settings save did not commit monitor setting to live config and private disk.");
            }
            finally { editor.Saved -= Saved; }
            Click(C<Button>(Shell, "DownloadsNavigationButton"));
            await Program.WaitUntilAsync(() => !Shell.SettingsPageVisible && C<TextBox>(Shell, "UrlInput").IsEffectivelyVisible);
        }
        internal async Task RequireBannerAsync(params string[] urls)
        {
            string beforeWait = DescribeMonitorState();
            try
            {
                await Program.WaitUntilAsync(() => Panel.IsEffectivelyVisible && Panel.IsArrangeValid
                    && Panel.Bounds.Width > 0 && Panel.Bounds.Height > 0 && Accept.IsEffectivelyVisible && Accept.IsEffectivelyEnabled);
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"Clipboard banner readiness failed (expected URLs={urls.Length}). "
                    + $"Last poll BEFORE: {_lastPollBefore}. Last poll AFTER: {_lastPollAfter}. "
                    + $"Before banner wait: {beforeWait}. At timeout: {DescribeMonitorState()}.", error);
            }
            Require((Offered.Text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .SequenceEqual(urls, StringComparer.Ordinal), "Visible banner URL snapshot mismatch.");
            Require(ReferenceEquals(TopLevel.GetTopLevel(Panel), Shell), "Banner is not in the native fixture shell.");
            foreach (Control control in new Control[] { Panel, Accept })
            {
                Point? origin = control.TranslatePoint(default, Shell);
                Require(origin is { } point && point.X >= -1 && point.Y >= -1
                    && point.X + control.Bounds.Width <= Shell.ClientSize.Width + 1
                    && point.Y + control.Bounds.Height <= Shell.ClientSize.Height + 1,
                    "Visible clipboard banner/action is outside the native client bounds.");
            }
        }
        internal void RequirePassive(string stage)
        {
            // Resolve may CONSTRUCT passive provider objects; that is not activation.
            Require(Shell.UrlList.Count == 0 && Shell.BoardsList.Count == 0
                && Session.GetThreadWorkSnapshots().Count == 0 && Session.Projection.View.Boards.Count == 0
                && Transport.Requests.IsEmpty && Session.Plugins == null && Clipboard.Writes == 0
                && !Shell.OwnedWindows.OfType<ExperimentalActivationDialog>().Any(window => window.IsVisible),
                stage + ": admitted work, provider/update HTTP, plugin runtime or clipboard write before confirmation.");
        }
        internal async Task CloseAsync()
        {
            if (_closed || Shell == null) return;
            MethodInfo close = typeof(MainWindow).GetMethod("CloseApplicationAsync", PrivateInstance)
                ?? throw new InvalidOperationException("Shell close seam changed.");
            await ((Task)close.Invoke(Shell, null)!).WaitAsync(Deadline);
            await Program.WaitUntilAsync(() => !Shell.IsVisible);
            _closed = true;
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                try { await CloseAsync(); }
                finally { if (Session != null) await Session.DisposeAsync().AsTask().WaitAsync(Deadline); }
            }
            finally { _client.Dispose(); _scope.Dispose(); }
        }
    }

    // No delegation to an OS clipboard, including writes. Deferred reads model a
    // platform permission/owner delay while the genuine UI lifetime changes.
    private sealed class FakeClipboard : IClipboard
    {
        internal string? Text;
        internal Func<Task<IAsyncDataTransfer?>>? Read;
        internal int Reads, Writes;
        public Task<IAsyncDataTransfer?> TryGetDataAsync()
        {
            Reads++;
            return Read?.Invoke() ?? Task.FromResult<IAsyncDataTransfer?>(Text == null ? null : TextData(Text));
        }
        public Task SetDataAsync(IAsyncDataTransfer? dataTransfer)
        { Writes++; dataTransfer?.Dispose(); throw new InvalidOperationException("Monitor unexpectedly wrote clipboard."); }
        public Task ClearAsync() { Writes++; throw new InvalidOperationException("Unexpected clipboard clear."); }
        public Task FlushAsync() { Writes++; throw new InvalidOperationException("Unexpected clipboard flush."); }
        public Task<IAsyncDataTransfer?> TryGetInProcessDataAsync()
        { Reads++; throw new InvalidOperationException("Unexpected in-process clipboard API."); }
    }

    // Terminal transport with no inner handler, socket, DNS, redirects, payload or media.
    // Positive requests after acceptance prove the passive-zero oracle is wired.
    private sealed class TerminalHttp : HttpMessageHandler
    {
        internal ConcurrentQueue<string> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Enqueue(request.RequestUri!.AbsoluteUri);
            Require(request.Method == HttpMethod.Get && request.RequestUri.IdnHost == Host,
                "Unexpected fixture HTTP destination/method.");
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
        }
    }
    private sealed class FakeProvider(HttpClient client) : IImageBoard, IProviderDescriptorSource
    {
        public string Name => "clipboard-smoke";
        public string NiceName => "Private clipboard smoke source";
        public Uri BaseUri => new("https://" + Host + "/");
        public IReadOnlyList<string> SupportedHosts => new[] { Host };
        public ProviderCapabilities Capabilities => ProviderCapabilities.ThreadWatch;
        public bool CanHandle(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IdnHost == Host;
        public bool IsThreadUri(Uri uri) => CanHandle(uri) && uri.AbsolutePath.StartsWith("/b/thread/", StringComparison.Ordinal)
            && long.TryParse(uri.AbsolutePath[10..], out _);
        public bool IsBoardUri(Uri uri) => CanHandle(uri) && uri.AbsolutePath == "/b/";
        public IBoard GetBoard(Uri uri) => new FakeBoard(this);
        public IThread GetThread(Uri uri) => new FakeThread(new FakeBoard(this), uri);
        public HttpClient GetHttpClient() => client;
    }
    private sealed class FakeBoard(IImageBoard provider) : BaseBoard
    {
        public override IImageBoard ImageBoard => provider;
        public override string BoardCode => "b";
        public override string Name { get; protected set; } = "Clipboard fixture";
        public override string NiceName => Name;
        public override Uri BoardUri => new(provider.BaseUri, "b/");
        public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Thread-only fixture unexpectedly requested a catalog.");
    }
    private sealed class FakeThread(IBoard board, Uri uri) : BaseThread
    {
        public override IBoard Board => board;
        public override string ThreadId => uri.Segments[^1];
        public override string NiceName => "Clipboard fixture " + ThreadId;
        public override Uri ThreadUri => uri;
        public override async Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
        {
            using HttpResponseMessage response = await Board.ImageBoard.GetHttpClient().GetAsync(ThreadUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            Activity = ThreadActivity.NoMedia;
        }
    }
}
