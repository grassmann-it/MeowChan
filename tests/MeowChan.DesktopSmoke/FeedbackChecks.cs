using MeowChan.PluginHost.Configuration;
using System.Reflection;
using System.Collections;
using System.Windows.Input;
using Avalonia.Automation;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;

namespace MeowChan.DesktopSmoke;

internal static class FeedbackChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        Config config = window.SessionHost.Config;
        bool information = config.ShowInformationNotifications, warning = config.ShowWarningNotifications, error = config.ShowErrorNotifications;
        SessionFeedbackStore store = (SessionFeedbackStore)typeof(MainWindow).GetField("_feedback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        MethodInfo publish = typeof(MainWindow).GetMethod("ShowNotification", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ContentControl host = window.FindControl<ContentControl>("DiagnosticsPageHost")!;
        object? previous = host.Content;
        DiagnosticsPage? page = null;
        try
        {
            config.ShowInformationNotifications = config.ShowWarningNotifications = config.ShowErrorNotifications = false;
            int before = store.Entries.Count;
            foreach (string operation in new[] { "target.open", "directory.open", "clipboard.read", "clipboard.write", "preferences.appearance", "preferences.gallery", "preferences.reading-position", "session.restore", "session.shutdown", "board.admission", "selection.command", "media.retry", "archive.delete", "gallery.action" })
                publish.Invoke(window, new object?[] { "Feedback fixture", "Failure for " + operation, NotificationType.Error, "frozen-object-A", "fixture." + operation });
            Require(store.Entries.Count == before + 14, "Suppressed error families were lost from session diagnostics.");
            Require(window.FindControl<Ursa.Controls.Banner>("ShellActionErrorBanner")!.IsVisible, "Suppressed errors have no local shell banner.");
            page = new DiagnosticsPage(_ => false, _ => Task.FromException(new IOException("Clipboard fixture unavailable")));
            page.AttachFeedback(store); host.Content = page;
            window.FindControl<MenuItem>("DiagnosticsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            page.FindControl<Expander>("SessionFeedbackSection")!.IsExpanded = true;
            DataGrid grid = page.FindControl<DataGrid>("SessionFeedbackGrid")!;
            // DataGrid defers SelectionChanged after ItemsSource changes until its first arrange.
            // Exercise the materialized user selection, then observe the actual detail event.
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                return grid.IsEffectivelyVisible && grid.Bounds.Height > 0
                    && grid.GetVisualDescendants().OfType<DataGridRow>().Any(row => row.IsEffectivelyVisible);
            });
            SessionFeedback selected = store.Entries.Last();
            Require(selected.Context == "frozen-object-A", "The captured feedback event lost its original context before selection.");
            grid.SelectedItem = selected;
            await Program.WaitUntilAsync(() => (grid.SelectedItem as SessionFeedback)?.Id == selected.Id
                && page.FindControl<TextBox>("SessionFeedbackDetails")!.Text == selected.DetailText);
            Require(page.FindControl<TextBox>("SessionFeedbackDetails")!.Text?.Contains("frozen-object-A") == true, "Captured operation identity is missing from details.");
            // Replacing a repeated row or appending a different context must keep the chosen identity.
            store.Record(selected.Category, selected.Operation, selected.Context, selected.Summary, selected.Details);
            store.Record(FeedbackCategory.Error, "fixture.unrelated", "frozen-object-B", "Unrelated failure");
            SessionFeedback repeated = store.Entries.Single(entry => entry.Id == selected.Id);
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                return (grid.SelectedItem as SessionFeedback)?.Id == selected.Id
                    && page.FindControl<TextBox>("SessionFeedbackDetails")!.Text == repeated.DetailText;
            });
            Require(repeated.Count == 2 && repeated.Context == "frozen-object-A"
                && page.FindControl<TextBox>("SessionFeedbackDetails")!.Text?.Contains("frozen-object-B") == false,
                "A feedback refresh replaced the selected operation with a newer unrelated context.");
            page.FindControl<Button>("CopySessionFeedbackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => store.Entries.Any(entry => entry.Operation == "diagnostics.copy-message"));
            Require(page.FindControl<TextBlock>("DiagnosticsActionError")!.Text == Texts.T("clipboard.writeFailed"), "Copy failure is not locally visible.");
            page.EndLifetime();
            page = new DiagnosticsPage(); page.AttachFeedback(store); host.Content = page;
            Require(page.FindControl<DataGrid>("SessionFeedbackGrid")!.ItemsSource!.Cast<SessionFeedback>().Count() == store.Entries.Count, "Rebuilding Diagnostics lost session feedback.");
            page.FindControl<Button>("ClearSessionFeedbackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(store.Entries.Count == 0 && window.FindControl<Ursa.Controls.Banner>("ShellActionErrorBanner")!.IsVisible,
                "Clearing session messages cleared a local error or retained the session list.");

            ArchivePage archive = new();
            try
            {
                MethodInfo message = typeof(ArchivePage).GetMethod("OperationMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
                message.Invoke(archive, new object?[] { "content", "Query failure retained", true });
                message.Invoke(archive, new object?[] { "media", string.Empty, false });
                Require(archive.FindControl<TextBlock>("ArchiveErrorText")!.Text == "Query failure retained", "Successful media loading erased the query error.");
                message.Invoke(archive, new object?[] { "action", string.Empty, false });
                Require(archive.FindControl<Ursa.Controls.Banner>("ArchiveErrorBanner")!.IsVisible, "A status redraw dismissed an unresolved archive error.");
            }
            finally { archive.EndLifetime(); }
            await CheckPluginGenerationsAsync();
            Console.WriteLine("FEEDBACK checks: popup suppression retains all central families; clipboard failure, diagnostic rebuild/clear, independent archive errors, real plugin operation cancellation/selection/lifetime guards.");
        }
        finally
        {
            page?.EndLifetime(); host.Content = previous;
            config.ShowInformationNotifications = information; config.ShowWarningNotifications = warning; config.ShowErrorNotifications = error;
        }
    }

    // Run through the existing diagnostics-only entry point too; no second window/profile or
    // native startup is needed. Reflection is limited to the existing publish/queue boundary.
    internal static async Task CheckDismissalAsync(MainWindow window)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        Config config = window.SessionHost.Config;
        bool warning = config.ShowWarningNotifications, error = config.ShowErrorNotifications;
        string language = Texts.Language;
        SessionFeedbackStore store = (SessionFeedbackStore)typeof(MainWindow).GetField("_feedback", instance)!.GetValue(window)!;
        MethodInfo publish = typeof(MainWindow).GetMethod("ShowNotification", instance)!;
        MethodInfo render = typeof(MainWindow).GetMethod("RenderNotification", instance)!;
        FieldInfo opened = typeof(MainWindow).GetField("_hasOpened", instance)!;
        object queue = typeof(MainWindow).GetField("_startupNotifications", instance)!.GetValue(window)!;
        int Queued() => (int)queue.GetType().GetProperty("Count")!.GetValue(queue)!;
        object Dequeue() => queue.GetType().GetMethod("Dequeue")!.Invoke(queue, null)!;
        bool wasOpened = (bool)opened.GetValue(window)!;
        Require(wasOpened && Queued() == 0, "Dismissal checks require the real shell's completed startup queue.");
        ContentControl host = window.FindControl<ContentControl>("DiagnosticsPageHost")!;
        object? previous = host.Content;
        DiagnosticsPage? page = null;
        string operation = "fixture.dismiss." + Guid.NewGuid().ToString("N");
        string title = operation;
        const string message = "Synthetic persistent source failure";
        Ursa.Controls.Banner banner = window.FindControl<Ursa.Controls.Banner>("ShellActionErrorBanner")!;
        TextBlock text = window.FindControl<TextBlock>("ShellActionError")!;
        Button dismiss = window.FindControl<Button>("ShellFeedbackDismissButton")
            ?? throw new InvalidOperationException("MU-06 requires the public shell X control ShellFeedbackDismissButton.");
        void Publish(string context, NotificationType type = NotificationType.Error) =>
            publish.Invoke(window, new object?[] { title, message, type, context, operation });
        SessionFeedback Entry(string context) => store.Entries.Single(entry => entry.Operation == operation && entry.Context == context);
        NotificationCard[] Cards() => window.GetVisualDescendants().OfType<NotificationCard>()
            .Where(card => !card.IsClosing && !card.IsClosed && card.Content is FeedbackToastContent { Entry: { } entry }
                && entry.Operation == operation).ToArray();
        async Task DrainUiAsync() { await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background); }
        try
        {
            config.ShowWarningNotifications = config.ShowErrorNotifications = false;
            // Model notifications captured before Opened, then use the same production renderer
            // as OnMainWindowOpened. Do not replay the real window's startup/lifetime handler.
            opened.SetValue(window, false);
            Publish("source-A"); Publish("source-A");
            Require(Queued() > 0 && Entry("source-A").Count == 2 && banner.IsVisible,
                "The pre-Opened repeated A fixture did not retain diagnostics and queued feedback.");
            object[] lateA = ((IEnumerable)queue).Cast<object>().ToArray();
            await DrainUiAsync();
            Require(dismiss.IsEffectivelyVisible && dismiss.IsEnabled && dismiss.Focusable
                && dismiss.Bounds.Width > 0 && dismiss.Bounds.Height > 0,
                "The shell X is not a visible, keyboard-focusable action.");
            Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(dismiss)), "The shell X has no accessible name.");
            ICommand closeA = dismiss.Command ?? throw new InvalidOperationException("The shell X must capture its displayed feedback identity.");
            Require(dismiss.Focus(), "The shell X cannot receive keyboard focus.");
            SmokeActionClick.Execute(dismiss);
            Require(!banner.IsVisible && !dismiss.IsKeyboardFocusWithin && Entry("source-A").Count == 2,
                "Closing A lost diagnostics, retained focus in hidden UI or left its banner visible.");
            int pendingAfterClose = Queued();
            Publish("source-A");
            Require(!banner.IsVisible && Entry("source-A").Count == 3 && Queued() == pendingAfterClose,
                "A repeat after close was requeued, redisplayed or omitted from diagnostics.");
            // Dismissal may remove pending A eagerly or retain it for a render-time guard.
            while (Queued() > 0) render.Invoke(window, new[] { Dequeue() });
            opened.SetValue(window, true);
            config.ShowWarningNotifications = config.ShowErrorNotifications = true;
            foreach (object pending in lateA) render.Invoke(window, new[] { pending });
            Publish("source-A");
            await DrainUiAsync();
            Require(!banner.IsVisible && Cards().Length == 0 && Entry("source-A").Count == 4,
                "Popup re-enable or a late pre-Opened queue item replayed closed A.");

            page = new DiagnosticsPage(); page.AttachFeedback(store); host.Content = page;
            window.FindControl<MenuItem>("DiagnosticsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            page.FindControl<Expander>("SessionFeedbackSection")!.IsExpanded = true;
            DataGrid grid = page.FindControl<DataGrid>("SessionFeedbackGrid")!;
            Require(grid.ItemsSource!.Cast<SessionFeedback>().Single(entry => entry.Id == Entry("source-A").Id).Count == 4,
                "The dismissed failure's diagnostic counter is not publicly available.");
            foreach (string code in new[] { "de", "en" })
            {
                Texts.SetLanguage(code);
                Publish("source-A");
                await DrainUiAsync();
                Require(!banner.IsVisible && Cards().Length == 0,
                    "A locale/status redraw replayed the same dismissed identity.");
            }
            Require(Entry("source-A").Count == 6, "Hidden repeats stopped incrementing diagnostics.");
            long oldId = Entry("source-A").Id;
            page.FindControl<Button>("ClearSessionFeedbackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(store.Entries.Count == 0, "The actual diagnostics Clear action did not clear its entries.");
            page.EndLifetime();
            page = new DiagnosticsPage(); page.AttachFeedback(store); host.Content = page;
            Publish("source-A");
            foreach (object pending in lateA) render.Invoke(window, new[] { pending });
            await DrainUiAsync();
            Require(Entry("source-A").Id != oldId && Entry("source-A").Count == 1 && !banner.IsVisible && Cards().Length == 0,
                "Diagnostic Clear/rebuild or late queue replay reset the closed-A presentation identity.");

            // Deliberately identical text: a different source must remain independently visible.
            Publish("source-B");
            await Program.WaitUntilAsync(() => Cards().Any(card => card.IsEffectivelyVisible));
            Require(banner.IsVisible && text.Text == message && Entry("source-B").Count == 1,
                "A different source B was suppressed with A or has no visible feedback.");
            closeA.Execute(null); // Real command frozen while A owned the reused shell control.
            Require(banner.IsVisible && Entry("source-B").Count == 1,
                "A stale close command for A hid or changed newer B.");
            Publish("source-A");
            await DrainUiAsync();
            Require(banner.IsVisible && text.Text == message && Cards().Length == 1 && Entry("source-A").Count == 2,
                "A dismissed repeat replaced B, replayed a toast or lost its diagnostic count.");
            SmokeActionClick.Execute(dismiss);
            Require(!banner.IsVisible && Entry("source-B").Count == 1, "B's own close action did not dismiss only its presentation.");
            Publish("source-B");
            Require(!banner.IsVisible && Entry("source-B").Count == 2,
                "A suppressed A repeat stole B's close identity, so B immediately reopened.");
            // Exercise Button's real Space gesture, not only the captured ICommand: Button
            // ordinarily resolves Command on key-up, when a newer publication may own it.
            config.ShowErrorNotifications = false;
            Publish("space-A");
            Require(dismiss.Focus(), "The shell X cannot receive focus for keyboard dismissal.");
            dismiss.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
            Publish("space-B");
            dismiss.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space });
            Require(banner.IsVisible && Entry("space-A").Count == 1 && Entry("space-B").Count == 1,
                "Space pressed for A but released after B hid the newer banner or lost diagnostics.");
            Publish("space-A");
            Require(banner.IsVisible && Entry("space-A").Count == 2, "The frozen Space gesture did not preserve diagnostic repeats.");
            SmokeActionClick.Execute(dismiss);
            Publish("space-B");
            Require(!banner.IsVisible && Entry("space-B").Count == 2,
                "The Space gesture failed to dismiss A, allowing its repeat to steal B's close identity.");
            // The active Semi template has no built-in close action. Exercise our
            // actual rendered toast X, not merely the central banner command.
            config.ShowErrorNotifications = true;
            Publish("toast-A"); Publish("toast-A"); Publish("toast-B");
            await Program.WaitUntilAsync(() => Cards().Length == 3 && Cards().All(card => card.IsEffectivelyVisible));
            FeedbackToastContent toastA = Cards().Select(card => (FeedbackToastContent)card.Content!)
                .First(content => content.Entry!.Context == "toast-A");
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                return toastA.DismissButton.IsEffectivelyVisible && toastA.DismissButton.Bounds.Width >= 32
                    && toastA.DismissButton.Bounds.Height >= 32;
            });
            Console.WriteLine("SMOKE toast layout: button=" + toastA.DismissButton.Bounds
                + "; content=" + toastA.Bounds + "; attached=" + (TopLevel.GetTopLevel(toastA) != null));
            Require(toastA.DismissButton.IsEffectivelyVisible && toastA.DismissButton.Bounds.Width >= 32
                && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(toastA.DismissButton)),
                "The rendered toast has no visible accessible X.");
            Program.CaptureWindow(window, "smoke-feedback-toast-close.png");
            SmokeActionClick.Execute(toastA.DismissButton);
            await Program.WaitUntilAsync(() => Cards().Length == 1);
            Require(store.IsDismissed(Entry("toast-A")) && !store.IsDismissed(Entry("toast-B"))
                && banner.IsVisible && ((FeedbackToastContent)Cards()[0].Content!).Entry!.Context == "toast-B",
                "Toast A's X hid B or failed to remove matching visible A duplicates.");
            Publish("toast-A");
            Require(Cards().Length == 1 && Entry("toast-A").Count == 3,
                "Closing the toast failed to suppress the repeated occurrence.");
            SmokeActionClick.Execute(((FeedbackToastContent)Cards()[0].Content!).DismissButton);
            Require(!banner.IsVisible && store.IsDismissed(Entry("toast-B")),
                "Toast B's X did not acknowledge its matching banner.");
            Publish("toast-expiry");
            await Program.WaitUntilAsync(() => Cards().Length == 1 && Cards()[0].IsEffectivelyVisible);
            NotificationCard expiring = Cards()[0];
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!expiring.IsClosed && !expiring.IsClosing && DateTime.UtcNow < deadline) await Task.Delay(50);
            Require(expiring.IsClosed || expiring.IsClosing, "The normal toast expiry did not occur within its bounded check.");
            Require(!store.IsDismissed(Entry("toast-expiry")), "Passive toast expiry acknowledged the underlying occurrence.");
            Publish("toast-expiry");
            await Program.WaitUntilAsync(() => Cards().Length == 1 && Cards()[0].IsEffectivelyVisible);
            Require(Entry("toast-expiry").Count == 2, "An expired, unacknowledged failure did not remain repeatable.");
            SmokeActionClick.Execute(((FeedbackToastContent)Cards()[0].Content!).DismissButton);
            Console.WriteLine("SMOKE feedback toast: rendered X, matching duplicate dismissal, independent B, and actual expiry without acknowledgement passed.");
            Console.WriteLine("SMOKE MU-06 feedback dismissal: visible accessible X; pre-Opened A repeats/late queue; continued diagnostic counts; popup re-enable; EN/DE redraw; diagnostic clear/rebuild; equal-text source B; stale A command and Space-release race cannot hide B passed.");
        }
        finally
        {
            opened.SetValue(window, wasOpened);
            queue.GetType().GetMethod("Clear")!.Invoke(queue, null);
            foreach (NotificationCard card in Cards()) card.Close();
            page?.EndLifetime(); host.Content = previous;
            config.ShowWarningNotifications = warning; config.ShowErrorNotifications = error;
            Texts.SetLanguage(language);
        }
    }

    private static async Task CheckPluginGenerationsAsync()
    {
        string root = Path.Combine(Config.GetConfigDirectory(), "feedback-plugin-fixture-" + Guid.NewGuid().ToString("N"));
        string sources = Path.Combine(root, "sources"); Directory.CreateDirectory(sources);
        await File.WriteAllTextAsync(Path.Combine(sources, "A.cs"), "public class A {}");
        await File.WriteAllTextAsync(Path.Combine(sources, "B.cs"), "public class B {}");
        DelayedCompiler compiler = new();
        PluginManagementServices services = new(new PluginProfileStore(root), compiler, new PluginInstallationInventory(), sources,
            (_, _) => throw new InvalidOperationException("A failed compilation must not start description."),
            (_, _, _) => throw new InvalidOperationException("A failed compilation must not start a worker."),
            (_, _, _, _, _) => throw new InvalidOperationException("No probe is part of this fixture."));
        PluginsPage page = new(); page.Attach(services);
        try
        {
            await page.ActivateAsync();
            string[] keys = (await services.ReadCatalogAsync(CancellationToken.None)).Entries.Select(entry => entry.Key).ToArray();
            Require(keys.Length == 2, "Plugin feedback fixture needs two candidates.");
            page.Select(keys[0]);
            Task old = page.BuildAndTestAsync();
            Require(page.IsBusy && page.FindControl<Ursa.Controls.LoadingIcon>("PluginOperationLoading")!.IsLoading, "An actual pending build has no loading state.");
            page.Select(keys[1]);
            string? status = page.FindControl<TextBlock>("PluginTestStatus")!.Text;
            compiler.Fail(); await old;
            Require(!page.IsBusy && !page.FindControl<Ursa.Controls.Banner>("PluginErrorBanner")!.IsVisible
                && page.FindControl<TextBlock>("PluginTestStatus")!.Text == status, "Late failure of A changed B feedback.");
            Task cancelled = page.BuildAndTestAsync();
            page.FindControl<Button>("PluginCancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            compiler.Fail(); await cancelled;
            Require(!page.IsBusy && page.FindControl<TextBlock>("PluginTestStatus")!.Text == Texts.T("plugins.cancelled")
                && !page.FindControl<Ursa.Controls.Banner>("PluginErrorBanner")!.IsVisible, "Cancellation became an error or left loading active.");
            Task failed = page.BuildAndTestAsync(); compiler.Fail(); await failed;
            Require(page.FindControl<Ursa.Controls.Banner>("PluginErrorBanner")!.IsVisible, "Current compilation failure is not retained in its banner.");
            Task ended = page.BuildAndTestAsync(); page.EndLifetime();
            string? ending = page.FindControl<TextBlock>("PluginPageError")!.Text;
            compiler.Fail(); await ended;
            Require(page.FindControl<TextBlock>("PluginPageError")!.Text == ending, "An ended page accepted a late build failure.");
        }
        finally { page.EndLifetime(); Directory.Delete(root, true); }
    }

    // Deliberately ignores cancellation to exercise a backend that completes after the UI has moved on.
    private sealed class DelayedCompiler : IPluginCompilationService
    {
        private TaskCompletionSource<PluginBuildResult>? _pending;
        public Task<PluginBuildResult> BuildAsync(string sourcePath, IReadOnlyList<PluginResourceInput>? resources = null, CancellationToken cancellationToken = default)
        {
            _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _pending.Task;
        }
        public void Fail() => _pending!.SetResult(new(PluginBuildStatus.SourceError, null, false, null, null,
            new[] { new PluginBuildDiagnostic("FIXTURE", "Delayed syntax failure") }));
        public Task<PluginArtifact?> FindArtifactAsync(string executionRevision, CancellationToken cancellationToken = default) => Task.FromResult<PluginArtifact?>(null);
        public Task<PluginCacheCleanupResult> CleanupAsync(IReadOnlySet<string> retainedExecutionRevisions, IReadOnlySet<string> retainedCompilationFingerprints,
            TimeSpan minimumAge, CancellationToken cancellationToken = default) => Task.FromResult(new PluginCacheCleanupResult(0, 0, Array.Empty<PluginBuildDiagnostic>()));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
