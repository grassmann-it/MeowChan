using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Deep links into the Status page (SM5): the Sources page opens Status > Diagnostics with its source filter preset, and the
// recovery route (a session that failed to restore) still reaches the Diagnostics segment while Statistics is unavailable.
// Managed routed input, no network.
internal static class StatusDeepLinkChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        await SourcesLeadToTheFilteredDiagnosticsAsync(window);
        await RecoveryRouteReachesDiagnosticsAsync(window);
        Console.WriteLine("SMOKE status deep links: Sources > Status > Diagnostics with the source filter preset and clearable; recovery route reaches the Diagnostics segment, Statistics falls back to it and the filter deep link works.");
    }

    private static async Task SourcesLeadToTheFilteredDiagnosticsAsync(MainWindow window)
    {
        window.Width = 1000;
        window.Height = 700;
        window.FindControl<MenuItem>("SourcesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Sources, () => "page " + window.SelectedPage);
        SourcesPage sources = (SourcesPage)window.FindControl<ContentControl>("SourcesPageHost")!.Content!;
        Button diagnostics = sources.FindControl<Button>("SourceDiagnosticsButton")!;
        await Program.WaitUntilAsync(() => diagnostics.IsEffectivelyVisible && diagnostics.IsEnabled,
            () => "The Diagnostics button of the Sources page is not usable.");
        string identity = sources.FindControl<TextBlock>("SourceIdentity")!.Text ?? string.Empty;
        diagnostics.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => window.DiagnosticsSegmentShown, () => "segment " + window.StatusSegmentSelected + " page " + window.SelectedPage);
        DiagnosticsPage page = (DiagnosticsPage)window.DiagnosticsPageHost.Content!;
        string? source = page.SourceFilter;
        Require(source != null && identity.Contains(source, StringComparison.Ordinal),
            "The Sources page did not preset the Diagnostics source filter to the source it showed: filter=" + source + " identity=" + identity);
        Require(page.FindControl<ComboBox>("DiagnosticSourceFilter")!.SelectedItem?.ToString() == source,
            "The source box does not show the preset source.");
        // The preset is an ordinary filter: the clear button resets it.
        page.FindControl<Button>("DiagnosticClearFilters")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(page.SourceFilter == null, "The preset source filter cannot be cleared.");
    }

    private static async Task RecoveryRouteReachesDiagnosticsAsync(MainWindow owner)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("status-recovery-route-");
        DesktopSessionHost? host = null;
        MainWindow? shell = null;
        try
        {
            host = new DesktopSessionHost(profile, Array.Empty<IImageBoard>(), (_, _) => new FailedStore());
            shell = new MainWindow(host);
            shell.Show(owner);
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(host.State == DesktopSessionState.RecoveryFailed, "The fixture session did not fail to restore: " + host.State);
            Button status = shell.FindControl<Button>("StatusNavigationButton")!;
            await Program.WaitUntilAsync(() => status.IsEnabled, () => "The Status button stays disabled after a failed recovery.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            // The statistics request falls back to the diagnostics segment without a restored session.
            Require(shell.ShowStatus(StatusSegment.Statistics), "The Status page cannot be opened after a failed recovery.");
            await Program.WaitUntilAsync(() => shell.DiagnosticsSegmentShown, () => "segment " + shell.StatusSegmentSelected + " page " + shell.SelectedPage);
            Require(!shell.StatisticsSegmentShown, "Statistics was shown without a restored session.");
            Require(!shell.StatusPage.StatisticsTab.IsEnabled, "The statistics segment is offered without a restored session.");
            // The deep link with a source works on this route too.
            Require(shell.ShowStatus(StatusSegment.Diagnostics, "4chan"), "The filtered diagnostics link failed after a failed recovery.");
            Require(((DiagnosticsPage)shell.DiagnosticsPageHost.Content!).SourceFilter == "4chan",
                "The recovery route did not apply the source filter.");
        }
        finally
        {
            if (shell != null) await CloseAsync(shell);
            if (host != null) await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            owner.Activate();
        }
    }

    private sealed class FailedStore : ISessionStore
    {
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromException<SessionLoadResult>(new IOException("fixture failed restore"));
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static async Task CloseAsync(MainWindow shell)
    {
        var close = typeof(MainWindow).GetMethod("CloseApplicationAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)close.Invoke(shell, null)!).WaitAsync(TimeSpan.FromSeconds(15));
        await Program.WaitUntilAsync(() => !shell.IsVisible);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
