using MeowChan.PluginHost.Configuration;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
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
