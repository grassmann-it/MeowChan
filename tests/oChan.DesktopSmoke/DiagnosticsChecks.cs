using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using oChan.Desktop;
using oChan.Desktop.Localization;
using oChan.Http;

namespace oChan.DesktopSmoke;

internal static class DiagnosticsChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        Config initial = Config.LoadConfig();
        SettingsEditor settings = window.SettingsEditor;
        double width = window.Width;
        double height = window.Height;
        ContentControl host = window.FindControl<ContentControl>("DiagnosticsPageHost")!;
        object? previous = host.Content;
        // The live owner may refresh its own page while these passive fixture snapshots are rendered.
        // Keep the isolated fixture as a retained page without introducing another native window.
        DiagnosticsPage page = new DiagnosticsPage(_ => false, _ => Task.CompletedTask);
        host.Content = page;
        DesktopThreadEntry thread = SnapshotFixtures.Thread(9123, "123", "Offline diagnostic fixture") with
        {
            Snapshot = SnapshotFixtures.Thread(9123, "123", "Offline diagnostic fixture").Snapshot with
            {
                LastCheckFailure = new FailureInfo(FailureKind.Http, "Fixture rate limit", HttpStatusCode.TooManyRequests),
                RequestProgress = new RequestPolicyProgress(RequestPolicyPhase.Cooldown, TimeSpan.FromSeconds(30), 2),
                MediaFailures = new Dictionary<string, FailureInfo> { ["777"] = new FailureInfo(FailureKind.Storage, "Fixture storage failure") }
            }
        };
        DesktopSessionView view = new DesktopSessionView(new[] { thread }, Array.Empty<DesktopBoardEntry>(),
            new[] { "Fixture save warning" }, DesktopSessionState.Ready, null);
        try
        {
            Click(window.FindControl<Button>("SettingsNavigationButton")!);
            settings.DownloadPath = "retained diagnosis draft";
            foreach (string mode in new[] { "Simple", "Barebones", "Advanced" })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                window.FindControl<MenuItem>("DiagnosticsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                window.Width = mode == "Barebones" ? 640 : 760;
                window.Height = mode == "Barebones" ? 480 : 500;
                foreach (string language in new[] { "en", "de" })
                {
                    Texts.SetLanguage(language);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
                    page.Refresh(view, true);
                    DataGrid grid = page.FindControl<DataGrid>("DiagnosticsGrid")!;
                    grid.SelectedItem = grid.ItemsSource!.Cast<DiagnosticEntry>().Single(entry => entry.Details.Contains("HTTP 429"));
                    await Program.WaitUntilAsync(() => Fits(window, page.FindControl<Button>("CopyDiagnosticButton")!)
                        && Fits(window, page.FindControl<Button>("OpenDiagnosticWatchButton")!) && grid.Bounds.Height > 40
                        && page.FindControl<TextBox>("DiagnosticDetails")!.Text?.Contains("HTTP 429") == true);
                    Require(window.SelectedPage == MainWindow.WorkspacePage.Diagnostics && settings.DownloadPath == "retained diagnosis draft",
                        "Diagnostics navigation lost the selected page or settings draft.");
                    Require(page.FindControl<TextBox>("DiagnosticDetails")!.Text!.Contains("HTTP 429"), "The complete diagnostic was not rendered.");
                    Program.CaptureWindow(window, $"smoke-diagnostics-{mode.ToLowerInvariant()}-{language}.png");
                    // A stale lifetime must never select a different watch, even if its URL matches.
                    Click(page.FindControl<Button>("OpenDiagnosticWatchButton")!);
                    Require(window.SelectedPage == MainWindow.WorkspacePage.Diagnostics
                        && !string.IsNullOrEmpty(page.FindControl<TextBlock>("DiagnosticsActionError")!.Text), "A stale diagnostic action changed pages.");
                    page.Refresh(view with { Threads = Array.Empty<DesktopThreadEntry>() }, true);
                    Require(!page.FindControl<Button>("CopyDiagnosticButton")!.IsEnabled
                        && !page.FindControl<Button>("OpenDiagnosticWatchButton")!.IsEnabled, "Removed diagnostics left actions enabled.");
                }
            }
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.L,
                KeyModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control });
            Require(window.SelectedPage == MainWindow.WorkspacePage.Downloads && window.FindControl<TextBox>("UrlInput")!.IsFocused,
                "The URL shortcut did not return from Diagnostics to Downloads.");
        }
        finally
        {
            page.EndLifetime();
            host.Content = previous;
            settings.CancelChanges();
            Texts.SetLanguage(initial.Language);
            await PresentationChecks.SwitchModeAsync(window, initial.WorkspaceMode);
            window.Width = width;
            window.Height = height;
            Click(window.FindControl<Button>("DownloadsNavigationButton")!);
        }
        Console.WriteLine("SMOKE diagnostics: current HTTP/storage/cooldown projection, retained drafts, stale watch protection and compact EN/DE layouts passed.");
    }

    private static bool Fits(Window window, Control control)
    {
        Avalonia.Point? point = control.TranslatePoint(new Avalonia.Point(0, 0), window);
        return control.IsEffectivelyVisible && control.Bounds.Width > 0 && point.HasValue && point.Value.X >= 0 && point.Value.Y >= 0
            && point.Value.X + control.Bounds.Width <= window.Bounds.Width + 1 && point.Value.Y + control.Bounds.Height <= window.Bounds.Height + 1;
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
