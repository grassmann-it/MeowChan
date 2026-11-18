using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopSmoke;

// work-views-booru: the real lazy shell page, not a second window or a synthetic registration tree.
// All ports, including previews, queue state, links, clock and watcher/catalog reads, are in-memory.
internal sealed class WorkViewBooruChecks : IAsyncDisposable
{
    private const string Query = "cat blue_eyes";
    private static readonly string[] PostIds = ["101", "102"];
    private static readonly string[] WatcherIds = ["wv-active", "wv-paused", "wv-blocked"];
    // The override setter only swaps ports; unlike a real binding event it does not notify the shell.
    // Deliver that same reconciliation explicitly, also on teardown (navigation alone retains pages).
    private static readonly MethodInfo Reconcile = typeof(MainWindow).GetMethod("OnBooruChanged", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The Booru shell composition callback is missing.");
    private static readonly FieldInfo ServicesOverride = typeof(DesktopSessionHost).GetField("_booruServicesOverride", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The Booru service override seam is missing.");
    private readonly MainWindow _window;
    private readonly BooruUiHarness _h = new();
    private readonly BooruWorkbenchServices? _servicesBefore, _overrideBefore;
    private readonly bool _showBefore;
    private readonly MainWindow.WorkspacePage _pageBefore;
    private readonly StatusSegment _segmentBefore;
    private BooruPage? _page;
    private bool _installed, _searched;

    internal WorkViewBooruChecks(MainWindow window, bool empty = false)
    {
        Dispatcher.UIThread.VerifyAccess();
        _window = window;
        _servicesBefore = window.SessionHost.BooruServices;
        _overrideBefore = (BooruWorkbenchServices?)ServicesOverride.GetValue(window.SessionHost);
        _showBefore = window.SessionHost.Config.ShowBooruTools;
        _pageBefore = window.SelectedPage;
        _segmentBefore = window.StatusSegmentSelected;
        if (empty) return; // Review fixture zero-state: retain the same offline ports, with no rows/watchers.
        _h.Serve(pages: 1, perPage: PostIds.Length);
        _h.Watchers.Seed(
            BooruUiFixtures.Watcher(WatcherIds[0], Query, clock: _h.Clock, hits: 6, queued: 5),
            BooruUiFixtures.Watcher(WatcherIds[1], "landscape", BooruWatcherStatus.Paused, _h.Clock),
            BooruUiFixtures.Watcher(WatcherIds[2], "scenery", BooruWatcherStatus.Error, _h.Clock,
                errorCode: BooruCodes.ContinuationBlocked)); // actually creates the conditional Restart action
        _h.Watchers.CatalogUsage = new BooruCatalogUsageView(80, 2, 100); // exercise disclosure, not an absent warning
    }

    internal async Task InitializeAsync()
    {
        _installed = true; // the caller disposes even if saving the preference or reconciliation fails
        _window.SessionHost.BooruServices = _h.Services;
        await _window.SessionHost.SaveViewPreferencesAsync(config => config.ShowBooruTools = true);
        Reconcile.Invoke(_window, null);
        Require(_window.SessionHost.Config.ShowBooruTools && ReferenceEquals(_window.SessionHost.BooruServices, _h.Services),
            "The fixture did not install its complete service bundle and presentation preference.");
    }

    internal static async Task EnterAsync(MainWindow window)
    {
        // F1: the pages are not in the ⋯ menu; Booru is hidden in the footer by default, so the route is the More menu (or the footer
        // button once the person shows it). The fixture's Reconcile (OnBooruChanged) has refreshed the footer.
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        Require(SmokeNavigation.Route(window, MainWindow.WorkspacePage.Booru) is "footer" or "more"
            && SmokeNavigation.IsEnabled(window, MainWindow.WorkspacePage.Booru), "The real shell Booru footer/More route is unavailable.");
        await SmokeNavigation.GoToAsync(window, MainWindow.WorkspacePage.Booru);
        await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Booru
            && window.BooruPageForTests is { IsEffectivelyVisible: true }
            && window.FindControl<ContentControl>("BooruPageHost")!.IsVisible,
            () => "Booru did not enter through the shell navigation route.");
    }

    private BooruPage Page => _window.BooruPageForTests is { } page && ReferenceEquals(page.Services, _h.Services)
        && ReferenceEquals(_window.FindControl<ContentControl>("BooruPageHost")!.Content, page)
        && ReferenceEquals(TopLevel.GetTopLevel(page.View), _window)
        && _window.SelectedPage == MainWindow.WorkspacePage.Booru
        ? page : throw new InvalidOperationException("Booru is not hosted by the shell over the fixture's services.");

    internal async Task MaterializeAsync()
    {
        BooruPage page = Page;
        if (_page != null) Require(ReferenceEquals(_page, page), "Presentation/navigation replaced the same-service page.");
        _page = page;
        await page.ActivateAsync(); // idempotent local-list initialization, also awaited by the fixture
        await SelectTabAsync(page, 0);
        if (!_searched)
        {
            Require(_h.Search.Requests == 0 && _h.Queue.Loads.Count == 0 && _h.Watchers.Checks == 0
                && page.Model.Search.View.Rows.IsEmpty, "Entering the page submitted work before the explicit search.");
            Find<TextBox>(page, BooruWorkbenchView.InputName).Text = Query;
            await Program.WaitUntilAsync(() => page.Model.Search.View.Input == Query);
            Button submit = Find<Button>(page, BooruWorkbenchView.SubmitName);
            Require(submit.IsEffectivelyVisible && submit.IsEffectivelyEnabled, "The real Search button is not usable.");
            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            _searched = true;
        }
        await Program.WaitUntilAsync(() => Cards(page, "BooruResult:").Length == PostIds.Length
            && page.GetVisualDescendants().OfType<Image>().Count(image => image.Source is Bitmap) == PostIds.Length
            && _h.Queue.StateReads > 0,
            () => "The actual result cards/previews were not materialized.");
        await SelectTabAsync(page, 1);
        await Program.WaitUntilAsync(() => Cards(page, "BooruWatcher:").Length == WatcherIds.Length,
            () => "The actual watcher cards were not materialized.");
        await SelectTabAsync(page, 0);
        RequireNoSideEffects();
    }

    // Both TabItem.Content roots belong to this specific hosted page even while one is not presented.
    // Never accept unrelated weak-registry entries or a prior page's detached cards as coverage.
    private static Control[] Controls(BooruPage page)
    {
        TabControl tabs = Find<TabControl>(page, BooruWorkbenchView.TabsName);
        Control[] roots = [page.View.Header, page.View.Footer, .. tabs.Items.OfType<TabItem>().Select(tab => (Control)tab.Content!)];
        return roots.SelectMany(root => root.GetLogicalDescendants().OfType<Control>().Prepend(root))
            .Distinct().Where(control => WorkViewElement.GetId(control) is { } id
                && WorkViewPolicy.Get(id).Page == WorkViewPage.Booru).ToArray();
    }

    internal static void CheckRegistrations(MainWindow window)
    {
        BooruPage page = window.BooruPageForTests ?? throw new InvalidOperationException("Booru has not been visited.");
        Control[] controls = Controls(page);
        IReadOnlyList<Control> registry = WorkViewElement.For(WorkViewPage.Booru);
        Require(controls.All(control => registry.Contains(control)), "A real page control is absent from the work-view registry.");
        HashSet<string> registered = controls.Select(control => WorkViewElement.GetId(control)!).ToHashSet(StringComparer.Ordinal);
        string[] missing = WorkViewPolicy.Entries.Where(entry => entry.Page == WorkViewPage.Booru
            && entry.Kind is WorkViewKind.Action or WorkViewKind.Element or WorkViewKind.Section
            && !registered.Contains(entry.Id)).Select(entry => entry.Id).ToArray();
        Require(missing.Length == 0, "Policy rows without controls on the visited page: " + string.Join(", ", missing));
        Console.WriteLine($"SMOKE work-views registrations: booru: {registered.Count} ids on the actual page, no orphaned policy row.");
    }

    internal async Task CheckAsync(string viewName)
    {
        BooruPage page = Page;
        WorkView view = WorkViewSetting.Parse(viewName);
        Require(page.View.CurrentWorkView == view, $"{viewName}: the page applied {page.View.CurrentWorkView}.");
        CheckRegistrations(_window);
        foreach (Control control in Controls(page))
        {
            string id = WorkViewElement.GetId(control)!;
            // This fixture is idle after one successful search: only Cancel and the transient notice are state-hidden.
            // Do not use the product's StateVisible as its own oracle: losing an action's state must fail too.
            bool stateVisible = id is not ("booru.search.cancel" or "booru.notice");
            Require(WorkViewElement.GetStateVisible(control) == stateVisible, $"{viewName}: {control.Name ?? id} lost its fixture state.");
            bool expected = stateVisible && WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve(id, view));
            Require(control.IsVisible == expected, $"{viewName}: {control.Name ?? id} visibility disagrees with {id}.");
        }
        await SelectTabAsync(page, 0);
        Require(Cards(page, "BooruResult:").Select(card => card.Name).SequenceEqual(PostIds.Select(id => "BooruResult:" + id)),
            "The visible search tab lost or replaced its exact result cards.");
        foreach (string id in PostIds)
        {
            Require(Find<Grid>(page, BooruWorkbenchView.DetailsPrefix + id).IsEffectivelyVisible, "Result facts are not on the search surface.");
            Require(Find<Button>(page, BooruWorkbenchView.LoadPrefix + id).IsEffectivelyVisible, "Load original is not on the search surface.");
            foreach ((string prefix, string policyId) in new[] { (BooruWorkbenchView.OpenPostPrefix, "booru.result.open"),
                (BooruWorkbenchView.CopyLinkPrefix, "booru.result.copy-link") })
                Require(WorkViewElement.GetId(Find<Button>(page, prefix + id)) == policyId, "A result lost its registered link action.");
        }
        Require(Find<TextBox>(page, BooruWorkbenchView.InputName).Text == Query && page.Model.Search.View.ExecutedQuery == Query,
            "Changing presentation altered the displayed/executed query.");
        await SelectTabAsync(page, 1);
        Require(Cards(page, "BooruWatcher:").Select(card => card.Name).SequenceEqual(WatcherIds.Select(id => "BooruWatcher:" + id)),
            "The visible watcher tab lost or replaced its exact watcher cards.");
        foreach (string id in WatcherIds)
            foreach (string prefix in new[] { BooruWorkbenchView.CheckPrefix, BooruWorkbenchView.RemovePrefix })
                Require(Find<Button>(page, prefix + id).IsEffectivelyVisible, "A primary watcher action is not on the watcher surface.");
        Require(Find<Button>(page, BooruWorkbenchView.PausePrefix + WatcherIds[0]).IsEffectivelyVisible
            && Find<Button>(page, BooruWorkbenchView.ResumePrefix + WatcherIds[1]).IsEffectivelyVisible,
            "The seeded active/paused states did not create their real Pause/Resume controls.");
        Require(page.Model.Watchers.View.Storage is { NearLimit: true }, "The catalog disclosure has no real near-limit state.");
        int expectedMore = Controls(page).Count(control => WorkViewElement.GetStateVisible(control)
            && WorkViewPolicy.Resolve(WorkViewElement.GetId(control)!, view) == Presence.Overflow);
        Require(expectedMore == (view switch { WorkView.Barebones => 6, WorkView.Simple => 2, _ => 0 }),
            $"{viewName}: the complete fixture did not supply the expected reduced actions.");
        Button more = Find<Button>(page, "BooruMore");
        Require(more.IsVisible == (expectedMore > 0)
            && AutomationProperties.GetName(more) == Texts.Count("workview.moreCount", expectedMore),
            $"{viewName}: More does not advertise all {expectedMore} reduced result/watcher/catalog entries.");
        await SelectTabAsync(page, 0);
        RequireNoSideEffects();
        Console.WriteLine($"SMOKE work-views booru/{viewName}: exact result and watcher cards, all policy ids and placements; More={expectedMore}; fake ports only.");
    }

    internal async Task CaptureWatchersAsync(string file, string output)
    {
        BooruPage page = Page;
        try
        {
            await SelectTabAsync(page, 1);
            Require(Cards(page, "BooruWatcher:").Length == WatcherIds.Length, "The watcher capture would be empty.");
            Program.CaptureWindow(_window, file, output);
        }
        finally { await SelectTabAsync(page, 0); }
    }

    private void RequireNoSideEffects()
    {
        Require(_h.Search.Searches.SequenceEqual(new[] { (Query, 1) }) && _h.Search.PostReads.Count == 0,
            "Presentation changed the exact query/page or sent another search/post request.");
        Require(_h.Previews.Requests == PostIds.Length && _h.Queue.StateReads > 0 && _h.Queue.Loads.Count == 0,
            "Previews/local states did not use the fake ports, or presentation loaded an original.");
        Require(_h.Watchers.Calls.Count > 0 && _h.Watchers.Calls.All(call => call == "list") && _h.Watchers.Checks == 0
            && _h.Links.Opened.Count == 0 && _h.Links.Copied.Count == 0,
            "Presentation mutated a watcher, checked a provider, or opened/copied a link.");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_installed) return;
        _installed = false;
        List<Exception> failures = new();
        async Task RestoreAsync(Func<Task> restore)
        {
            try { await restore(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        // Leave before service reconciliation: do not activate a replacement while restoring the fixture.
        await RestoreAsync(async () =>
        {
            _window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => _window.SelectedPage == MainWindow.WorkspacePage.Downloads);
        });
        await RestoreAsync(() =>
        {
            BooruPage? owned = _window.BooruPageForTests is { } current && ReferenceEquals(current.Services, _h.Services) ? current : _page;
            try
            {
                // Restore the raw override, not just the getter: null must continue following composition changes.
                _window.SessionHost.BooruServices = _overrideBefore;
                Reconcile.Invoke(_window, null); // shell removes its retained host content and disposes its model
            }
            finally { owned?.EndLifetime(); } // also retires callbacks/timer if shell reconciliation itself failed
            Require((owned == null || !ReferenceEquals(_window.BooruPageForTests, owned))
                && ReferenceEquals(_window.SessionHost.BooruServices, _servicesBefore)
                && ReferenceEquals(ServicesOverride.GetValue(_window.SessionHost), _overrideBefore),
                "The service override or owned page survived restoration.");
            return Task.CompletedTask;
        });
        await RestoreAsync(() => _window.SessionHost.SaveViewPreferencesAsync(config => config.ShowBooruTools = _showBefore));
        await RestoreAsync(async () =>
        {
            await SmokeNavigation.GoToAsync(_window, _pageBefore);
            await Program.WaitUntilAsync(() => _window.SelectedPage == _pageBefore);
            if (_pageBefore == MainWindow.WorkspacePage.Status)
                Require(_window.ShowStatus(_segmentBefore), "The original Status segment could not be restored.");
        });
        if (failures.Count > 0) throw new AggregateException("Booru work-view fixture restoration failed.", failures);
    }

    // Also the UI catalog run's tab switch (Search 0, Watchers 1).
    internal async Task SelectTabAsync(int index) => await SelectTabAsync(Page, index);

    private static async Task SelectTabAsync(BooruPage page, int index)
    {
        TabControl tabs = Find<TabControl>(page, BooruWorkbenchView.TabsName);
        tabs.SelectedIndex = index;
        for (int pass = 0; pass < 2; pass++)
            await Dispatcher.UIThread.InvokeAsync(page.UpdateLayout, DispatcherPriority.Background);
        Require(tabs.SelectedIndex == index, "The actual tab selection did not settle.");
    }

    private static T Find<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static Border[] Cards(Control root, string prefix) => root.GetVisualDescendants().OfType<Border>()
        .Where(card => card.Name?.StartsWith(prefix, StringComparison.Ordinal) == true).ToArray();
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("booru: " + message);
    }
}
