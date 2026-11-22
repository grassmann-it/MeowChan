using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Avalonia.Controls.Notifications;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using Serilog;

namespace MeowChan;

/// <summary>
/// The Danbooru page's repair route for a running build that fails with <c>sdk-mismatch</c>: review the current bundled
/// build in the shared review dialog, confirm it, restart MeowChan. The shell navigates and reads; the dialog and the
/// profile store keep every trust decision. MeowChan has no safe in-process restart of its own profile (the profile lease
/// is held until this process ends), so the confirmed state names the restart instead of offering a button for it.
/// </summary>
public partial class MainWindow
{
    private BooruBuildRepairProbe? _booruRepairProbe;
    private PluginSdkMismatchLedger? _booruRepairLedger;
    private long _booruRepairRevision;
    private bool _booruRepairMismatchSeen;

    private async Task ReviewStartupBooruBuildAsync()
    {
        if (_isClosing || _sourcesPage is not { } sources || _pluginsPage is not { } plugins ||
            !NavigateWorkspace(WorkspacePage.Sources)) return;
        await sources.ActivateAsync(SourcesPluginsFilter.AllPlugins);
        if (_isClosing || _selectedPage != WorkspacePage.Sources || !ReferenceEquals(_pluginsPage, plugins)) return;
        if (!await plugins.ReviewBundledBuildsAsync())
            ShowNotification(Texts.T("plugins.repair.review"),
                Texts.T(plugins.BundledReviewBlockReasonKey() ?? "booru.ui.repair.notReady"),
                NotificationType.Information);
    }

    /// <summary>Smokes replace the session's probe with one over a fixture store; nothing else does.</summary>
    internal BooruBuildRepairProbe? BooruRepairProbeForTests { get; set; }

    private BooruBuildRepairProbe? BooruRepairProbe
    {
        get
        {
            if (BooruRepairProbeForTests is { } fixture) return fixture;
            if (_session.Plugins is not { } plugins || plugins.Booru?.PluginId is not { } pluginId) return null;
            if (_booruRepairProbe is { } current && current.PluginId == pluginId && ReferenceEquals(current.Ledger, plugins.SdkMismatches))
                return current;
            return _booruRepairProbe = new BooruBuildRepairProbe(pluginId, plugins.ProfileStore,
                () => plugins.RunningExecutionRevision(pluginId), plugins.SdkMismatches);
        }
    }

    /// <summary>Wires the route of one page composition; called once per page from <see cref="EnsureBooruPage"/>.</summary>
    private void AttachBooruRepair(BooruPage page, BooruWorkbenchServices services)
    {
        page.View.ReviewCurrentBuild = () => RunBooruRepairRouteAsync(page, services);
        page.View.OpenPluginDetails = () => OpenBooruPluginDetailsAsync(page, services);
        page.View.ReviewUnavailableReason = () => _pluginsPage is { } plugins && _sourcesPage is not null
            ? plugins.BundledReviewBlockReasonKey() : "booru.ui.repair.notReady";
        _booruRepairMismatchSeen = false;
        page.Model.Search.Changed += OnBooruSearchChangedForRepair;
        ObservePluginsBusyForBooruRepair();
        if (BooruRepairProbe?.Ledger is { } ledger && !ReferenceEquals(ledger, _booruRepairLedger))
        {
            if (_booruRepairLedger is { } previous) previous.Changed -= OnBooruRepairLedgerChanged;
            _booruRepairLedger = ledger;
            ledger.Changed += OnBooruRepairLedgerChanged;
        }
        // The first reading follows the navigation that shows the page (NavigateWorkspace → RequestBooruRepairRefresh).
    }

    private void DetachBooruRepair(BooruPage page)
    {
        page.Model.Search.Changed -= OnBooruSearchChangedForRepair;
        ++_booruRepairRevision;
    }

    // A search result arrives on any thread; only the transition into (or out of) the mismatch failure needs a new reading.
    private void OnBooruSearchChangedForRepair() => Dispatcher.UIThread.Post(() =>
    {
        if (_isClosing || _booruPage is not { } page) return;
        bool seen = BooruText.CanReviewCurrentBuild(page.Model.Search.View);
        if (seen == _booruRepairMismatchSeen) return;
        _booruRepairMismatchSeen = seen;
        if (seen) RequestBooruRepairRefresh();
    });

    // The Plugins page is built lazily; (re)subscribe whenever the route is shown. Idempotent.
    private void ObservePluginsBusyForBooruRepair()
    {
        if (_pluginsPage is not { } pluginsPage) return;
        pluginsPage.BusyChanged -= OnPluginsBusyChangedForBooruRepair;
        pluginsPage.BusyChanged += OnPluginsBusyChangedForBooruRepair;
    }

    private void OnBooruRepairLedgerChanged() => Dispatcher.UIThread.Post(RequestBooruRepairRefresh);

    private void OnPluginsBusyChangedForBooruRepair(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_isClosing) _booruPage?.View.RefreshRepairPresentation();
    });

    /// <summary>Re-reads the route for the current page (activation, a new observation, a closed review). Never writes.</summary>
    private void RequestBooruRepairRefresh()
    {
        if (_isClosing || _booruPage is not { } page) return;
        ObservePluginsBusyForBooruRepair();
        OpenActions.Start(() => RefreshBooruRepairAsync(page), "booru.repair.refresh");
    }

    private async Task RefreshBooruRepairAsync(BooruPage page)
    {
        if (_isClosing || !ReferenceEquals(_booruPage, page)) return;
        long revision = ++_booruRepairRevision;
        bool known = BooruText.CanReviewCurrentBuild(page.Model.Search.View);
        BooruBuildRepair state;
        if (BooruRepairProbe is not { } probe) state = known ? new BooruBuildRepair(true, BooruRepairStage.Unknown) : BooruBuildRepair.None;
        else
        {
            try { state = await probe.ReadAsync(known, CancellationToken.None); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Unreadable: keep offering the route; the dialog reads the store again and says what it finds.
                Log.Warning(exception, "The Danbooru repair state could not be read");
                state = new BooruBuildRepair(known || probe.MismatchObserved, BooruRepairStage.Unknown);
            }
        }
        if (_isClosing || revision != _booruRepairRevision || !ReferenceEquals(_booruPage, page)) return;
        page.View.Repair = state;
    }

    /// <summary>
    /// Booru → shared review dialog → back to Booru. Every step that cannot run says why on the page; after the dialog the
    /// page reads the plugin snapshot again, so a saved confirmation turns the error into the restart notice.
    /// </summary>
    private async Task RunBooruRepairRouteAsync(BooruPage page, BooruWorkbenchServices services)
    {
        // A detached/old composition cannot open a review for a replacement session.
        if (_isClosing || !CanOpenBooru || !ReferenceEquals(_booruPage, page) || !ReferenceEquals(_session.BooruServices, services)
            || _selectedPage != WorkspacePage.Booru) return;
        if (_sourcesPage is not { } sources || _pluginsPage is not { } plugins)
        { page.View.ShowRepairNotice("booru.ui.repair.notReady"); return; }
        if (plugins.BundledReviewBlockReasonKey() is { } blocked) { page.View.ShowRepairNotice(blocked); return; }
        if (!NavigateWorkspace(WorkspacePage.Sources)) { page.View.ShowRepairNotice("booru.ui.repair.busy"); return; }
        bool shown = false;
        try
        {
            await sources.ActivateAsync(SourcesPluginsFilter.AllPlugins);
            if (!_isClosing && _selectedPage == WorkspacePage.Sources && ReferenceEquals(_session.BooruServices, services)
                && ReferenceEquals(_pluginsPage, plugins))
                shown = await plugins.ReviewBundledBuildsAsync();
        }
        finally
        {
            // Back where the route started, whatever the dialog did; the user never lands on another page silently.
            if (!_isClosing && _selectedPage == WorkspacePage.Sources && ReferenceEquals(_booruPage, page) && CanOpenBooru)
                NavigateWorkspace(WorkspacePage.Booru);
        }
        if (_isClosing || !ReferenceEquals(_booruPage, page)) return;
        await RefreshBooruRepairAsync(page);
        if (!shown && ReferenceEquals(_booruPage, page))
            page.View.ShowRepairNotice(plugins.BundledReviewBlockReasonKey() ?? "booru.ui.repair.notOpened",
                MeowChan.Desktop.Localization.Texts.T("plugins.repair.review"));
    }

    /// <summary>The blocked route's next step: the Danbooru plugin's details, where its state and pin are shown.</summary>
    private async Task OpenBooruPluginDetailsAsync(BooruPage page, BooruWorkbenchServices services)
    {
        if (_isClosing || !ReferenceEquals(_booruPage, page) || !ReferenceEquals(_session.BooruServices, services)) return;
        if (_sourcesPage is not { } sources) { page.View.ShowRepairNotice("booru.ui.repair.notReady"); return; }
        if (!NavigateWorkspace(WorkspacePage.Sources)) { page.View.ShowRepairNotice("booru.ui.repair.busy"); return; }
        await sources.ShowPlugin(DanbooruPostReference.ProviderId);
    }
}
