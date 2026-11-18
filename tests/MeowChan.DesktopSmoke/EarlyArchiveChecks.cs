using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

// UX103 A/C: with startup held after the catalog became readable (plugins and registry restore still
// pending) the real shell lets the user open the archive and search the local catalog. Work filters are
// refused locally instead of reading as zero results, mutation gates stay closed, and Ready keeps the
// chosen page, the typed draft and the focus. In-process routed input, not OS input.
internal static class EarlyArchiveChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    internal static void Seed(string profile)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        MeowChan.Archive.ArchiveCatalog catalog = MeowChan.Archive.ArchiveCatalog.OpenAsync(profile, lease).GetAwaiter().GetResult();
        try
        {
            foreach ((string thread, string title) in new[] { ("1", "alpha early archive fixture"), ("2", "beta early archive fixture") })
                catalog.RegisterThreadAsync(new ArchiveThreadRegistration(new ResourceId("early", "b", thread),
                    "https://offline.invalid/early/" + thread, title, Path.Combine(profile, "Downloads"), true)).GetAwaiter().GetResult();
        }
        finally { catalog.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    internal static async Task RunHeldAsync(MainWindow window, StartupPreviewBarrier readable, Func<int> sessionLoads)
    {
        Dispatcher.UIThread.VerifyAccess();
        await readable.Entered.Task.WaitAsync(Deadline);
        Button archive = C<Button>(window, "ArchiveNavigationButton");
        await Program.WaitUntilAsync(() => archive.IsEnabled);
        Require(window.SessionHost.State == DesktopSessionState.Starting && sessionLoads() == 0 && window.SessionHost.CatalogReadable,
            "The readable hold is not a pending startup.");
        Require(!C<Button>(window, "AddUrlButton").IsEnabled && !C<TextBox>(window, "UrlInput").IsEnabled && !C<Button>(window, "PauseButton").IsEnabled,
            "Early page choice enabled a session command.");
        Require(!C<Button>(window, "RulesNavigationButton").IsEnabled
            && !C<Button>(window, "SourcesNavigationButton").IsEnabled, "A page that needs plugins or the registry was offered early.");
        Require(window.FindControl<Button>("PluginsNavigationButton") == null && window.FindControl<ContentControl>("PluginsPageHost") == null,
            "The removed Plugins workspace was recreated during startup.");
        Require(C<Border>(window, "StartupPreviewPanel").IsVisible && window.HistoricThreads.Count == 1, "The historical rows are not on the Downloads page before navigation.");

        archive.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ArchivePage page = window.ArchivePage;
        await Program.WaitUntilAsync(() => C<ContentControl>(window, "ArchivePageHost").IsVisible && !page.IsBusy && Rows(page) == 2);
        Require(!C<Grid>(window, "WorkspaceBody").IsVisible && C<ContentControl>(window, "ArchivePageHost").IsVisible,
            "The Downloads table covered the archive page.");
        Require(window.SessionHost.State == DesktopSessionState.Starting && sessionLoads() == 0, "Reading the archive advanced startup.");

        TextBox search = P<TextBox>(page, "ArchiveSearchText");
        search.Text = "alpha";
        await page.ApplySearchAsync();
        await Program.WaitUntilAsync(() => !page.IsBusy);
        Require(Rows(page) == 1 && page.AppliedSearchSpec?.Text == "alpha", "A catalog text search did not run during startup.");

        // Work status does not exist yet: the filter is refused in place, not answered with "0 results".
        ComboBox work = P<ComboBox>(page, "ArchiveWorkFilter");
        work.SelectedIndex = 1;
        await page.ApplySearchAsync();
        await Program.WaitUntilAsync(() => !page.IsBusy);
        Require(P<TextBlock>(page, "ArchiveSearchError").Text == Texts.T("archive.workStatusLoading"), "The unknown work status was not explained.");
        Require(Rows(page) == 1 && page.AppliedSearchSpec?.WorkFilter == ArchiveWorkFilter.All && work.SelectedIndex == 1,
            "The refused work filter changed the result or dropped the draft.");
        Require(!page.BuildReaderThreadMenu().Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "archive.capture-toggle")).IsEnabled
            && !P<Button>(page, "ExportArchiveButton").IsEnabled
            && !P<Button>(page, "ArchiveOpenGallery").IsEnabled, "Early reading opened a mutation, export or gallery gate.");

        work.SelectedIndex = 0;
        search.Text = "alp"; // An unapplied draft with focus: Ready must not reset either.
        search.Focus();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(search.IsFocused, "The archive search box could not take focus during startup.");

        readable.Release.TrySetResult();
        await Program.WaitUntilAsync(() => C<Button>(window, "AddUrlButton").IsEnabled);
        await Program.WaitUntilAsync(() => !page.IsBusy);
        Require(window.SessionHost.State == DesktopSessionState.Ready && sessionLoads() == 1, "Startup did not complete after the hold.");
        Require(C<ContentControl>(window, "ArchivePageHost").IsVisible && !C<Grid>(window, "WorkspaceBody").IsVisible,
            "Ready navigated away from the page the user chose.");
        Require(search.Text == "alp" && search.IsFocused, "Ready reset the typed draft or the focus.");
        Require(Rows(page) == 1 && page.AppliedSearchSpec?.Text == "alpha", "Ready replaced the applied result with the draft.");

        // With the runtime present the same filter is a real answer (no watch exists in this fixture).
        search.Text = "alpha"; work.SelectedIndex = 1;
        await page.ApplySearchAsync();
        await Program.WaitUntilAsync(() => !page.IsBusy);
        Require(P<TextBlock>(page, "ArchiveSearchError").Text is null or "" && Rows(page) == 0
            && page.AppliedSearchSpec?.WorkFilter != ArchiveWorkFilter.All, "The work filter stayed blocked after Ready.");

        C<Button>(window, "DownloadsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => C<Grid>(window, "WorkspaceBody").IsVisible || C<Border>(window, "StartupPreviewPanel").IsVisible);
        Console.WriteLine("SMOKE early-archive: PASS (navigation, catalog search, work filter refusal, Ready keeps page/draft/focus)");
    }

    private static int Rows(ArchivePage page) =>
        P<ListBox>(page, "ArchiveThreads").ItemsSource?.Cast<object>().Count() ?? 0;
    private static T C<T>(MainWindow window, string name) where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException("Missing " + name);
    private static T P<T>(ArchivePage page, string name) where T : Control =>
        page.FindControl<T>(name) ?? throw new InvalidOperationException("Missing " + name);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
