using Avalonia.Controls;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopSmoke;

// B10: steps of the existing booru-workbench scene, not another acceptance scene.
// Real hosted controls -> models -> counted ports. The remote viewer has no UI opener:
// its step below deliberately calls the owned model and is NOT viewer UI acceptance.
internal static partial class BooruWorkbenchChecks
{
    private static async Task CheckBrowseContractsAsync(MainWindow owner, BooruPage page, BooruUiHarness h)
    {
        Texts.SetLanguage("en");
        TextBox input = Find<TextBox>(page, BooruWorkbenchView.InputName);
        BooruSearchModel search = page.Model.Search;
        int loads = h.Queue.Loads.Count;
        int requests = h.Search.Requests;
        int oldLimit = owner.SessionHost.Config.BooruCatalogLimitMiB;
        int oldCooldown = owner.SessionHost.Config.BooruRetryAfterCooldownHours;
        try
        {
            // Save through the session's actual persistence owner, then read the profile anew.
            // Config copy/export boundaries remain unit contracts, not UI settings acceptance.
            await owner.SessionHost.SaveViewPreferencesAsync(c =>
            {
                c.BooruCatalogLimitMiB = 8;
                c.BooruRetryAfterCooldownHours = 1;
            });
            Config reloaded = Config.LoadConfig(Config.GetConfigDirectory());
            Require(reloaded.LoadWarning is null && reloaded.ShowBooruTools
                && reloaded.BooruCatalogLimitMiB == 8 && reloaded.BooruRetryAfterCooldownHours == 1,
                "B5: the saved browse preferences did not reload.");
            Require(h.Search.Requests == requests && h.Queue.Loads.Count == loads,
                "B5: saving/reloading preferences replayed work.");
        }
        finally
        {
            await owner.SessionHost.SaveViewPreferencesAsync(c =>
            {
                c.BooruCatalogLimitMiB = oldLimit;
                c.BooruRetryAfterCooldownHours = oldCooldown;
            });
        }
        Console.WriteLine("SMOKE booru B5: session preference save/profile reload, no request replay (not a settings-editor or process-restart acceptance).");

        h.ServeFullPages(8);
        input.Text = "browse_contract";
        Click(Find<Button>(page, BooruWorkbenchView.SubmitName));
        await Program.WaitUntilAsync(() => Cards(page, "BooruResult:").Count == 20 && Previews(page) == 20,
            () => "B10 full page did not render");
        Require(Find<TextBlock>(page, "BooruResultCount").Text == Texts.T("booru.ui.page.atLeast", 20),
            "B8: the result lower bound is not stated as at least 20.");

        // B6/B7 model integration over the same rendered result and ports, no pretend UI opener.
        await page.Model.OpenViewerAsync("9220");
        await page.Model.OpenViewerAsync("9218");
        input.Text = "unsent_edit";
        await page.Model.Viewer.DownloadAsync("9220"); // stale action
        Require(h.Queue.Loads.Count == loads, "B6: a stale viewer action queued an original.");
        await page.Model.Viewer.DownloadAsync("9218");
        await page.Model.Viewer.DownloadAsync("9218");
        Require(h.Queue.Loads.Skip(loads).SequenceEqual(new[] { ("9218", BooruLoadOrigin.Search) })
            && h.Queue.LoadQueries.Last() == "browse_contract"
            && h.Samples.Loaded.TakeLast(2).SequenceEqual(new[] { "9220", "9218" })
            && search.Selected.Count == 0, "B6: viewer binding/provenance or explicit-only admission failed.");
        page.Model.Viewer.Close();
        loads++;
        Console.WriteLine("SMOKE booru B6: rendered-result -> owned viewer model -> sample and bound original handoff; UI opener remains OPEN.");

        NumericUpDown target = Find<NumericUpDown>(page, "BooruPageTarget");
        async Task Jump(int number)
        {
            target.Value = number;
            await Program.WaitUntilAsync(() => Find<Button>(page, "BooruJumpPage").IsEnabled,
                () => "page jump not enabled for " + number);
            Click(Find<Button>(page, "BooruJumpPage"));
        }
        await Jump(5);
        await Program.WaitUntilAsync(() => search.View.Page == 5 && search.View.PageLimitReached
            && Text(page).Contains(Texts.T("booru.ui.page.atLeast", 100)), () => "B8: host limit/lower bound missing");
        Require(!Find<Button>(page, BooruWorkbenchView.NextName).IsEnabled
            && h.Search.Searches.Last() == ("browse_contract", 5), "B8: jump used the edit or exceeded the host limit.");

        // A failed page retains backwards navigation but cooldown gates it and explicit retry.
        h.Search.OnSearch = (_, _, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Of(
            BooruCallStatus.RateLimited, "http-429", h.Clock.GetUtcNow().AddMinutes(2), 429));
        await Jump(4);
        await Program.WaitUntilAsync(() => search.IsPaused && Cards(page, "BooruResult:").Count == 0,
            () => "B8: failed page was not cleared");
        Require(!Find<Button>(page, BooruWorkbenchView.PreviousName).IsEnabled
            && !Find<TextBlock>(page, "BooruResultCount").IsVisible, "B8: failed-page cooldown/count is untruthful.");
        requests = h.Search.Requests;
        h.Clock.Advance(TimeSpan.FromMinutes(2)); search.Tick();
        await Program.WaitUntilAsync(() => Find<Button>(page, BooruWorkbenchView.RetryName).IsEffectivelyVisible
            && Find<Button>(page, BooruWorkbenchView.PreviousName).IsEnabled, () => "B8: recovery controls did not return");
        Require(h.Search.Requests == requests, "B8: cooldown expiry retried automatically.");
        h.ServeFullPages(8);
        Click(Find<Button>(page, BooruWorkbenchView.RetryName));
        await Program.WaitUntilAsync(() => search.View.Page == 4 && Cards(page, "BooruResult:").Count == 20,
            () => "B8: retry did not restore the failed page");
        Require(h.Search.Searches.Last() == ("browse_contract", 4), "B8: retry changed query/page.");
        h.Search.OnSearch = (q, p, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(q, p, true)));
        await Jump(3);
        await Program.WaitUntilAsync(() => search.View.Status == BooruSearchStatus.Empty
            && !Find<TextBlock>(page, "BooruResultCount").IsVisible, () => "B8: empty end state missing");
        Require(!Find<Button>(page, BooruWorkbenchView.NextName).IsEnabled && !search.View.PageLimitReached,
            "B8: provider end is confused with the host page limit.");
        Console.WriteLine("SMOKE booru B8: bounded jump, executed query, at-least 20/100, host limit versus empty provider end, failed page/cooldown/explicit same-page recovery.");

        // B7: cancel an in-flight page, then deliver a late successful response anyway.
        TaskCompletionSource<BooruCallResult<BooruResultPage>> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Search.OnSearch = (_, _, _) => late.Task;
        await Jump(2);
        await Program.WaitUntilAsync(() => Find<Button>(page, BooruWorkbenchView.CancelName).IsEffectivelyVisible,
            () => "B7: cancellation action missing");
        Click(Find<Button>(page, BooruWorkbenchView.CancelName));
        late.SetResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page("browse_contract", 2, true, BooruUiFixtures.Post("late"))));
        await Program.WaitUntilAsync(() => search.View.Code == BooruCodes.Canceled
            && Text(page).Contains(Texts.T(BooruCodes.Key(BooruCodes.Canceled)))
            && Find<Button>(page, BooruWorkbenchView.RetryName).IsEffectivelyVisible,
            () => "B7: aborted request not visibly recoverable");
        Require(search.View.Rows.IsEmpty, "B7: late rows were published after cancellation.");
        h.ServeFullPages(8);
        Click(Find<Button>(page, BooruWorkbenchView.RetryName));
        await Program.WaitUntilAsync(() => search.View.Page == 2 && Cards(page, "BooruResult:").Count == 20,
            () => "B7: cancelled page retry failed");
        Require(h.Search.Searches.Last() == ("browse_contract", 2) && h.Queue.Loads.Count == loads,
            "B7: metadata recovery replayed originals or changed request.");

        // A thumbnail abort is recoverable without metadata or original replay.
        h.Previews.OnPreview = _ => throw new OperationCanceledException();
        await Jump(1);
        await Program.WaitUntilAsync(() => Exists(page, "BooruRetryPreview:9220"), () => "B7: thumbnail retry missing");
        int previews = h.Previews.Requests;
        requests = h.Search.Requests;
        h.Previews.OnPreview = null;
        Click(Find<Button>(page, "BooruRetryPreview:9220"));
        await Program.WaitUntilAsync(() => search.View.Rows[0].Preview == BooruPreviewState.Ready,
            () => "B7: thumbnail did not recover");
        Require(h.Previews.Requests == previews + 1 && h.Search.Requests == requests && h.Queue.Loads.Count == loads,
            "B7: thumbnail retry fetched more than its own preview.");
        Console.WriteLine("SMOKE booru B7: visible abort, late response rejected, explicit same-page retry; aborted thumbnail retries only itself.");

        // B9: activate actual explicit checkbox controls, including a rejected eleventh.
        void Select(string id, bool selected)
        {
            CheckBox box = Find<CheckBox>(page, "BooruSelect:" + id);
            box.IsChecked = selected; // routed Click is raised after ToggleButton's state change
            Click(box);
        }
        for (int i = 0; i < 11; i++)
        {
            Select((9220 - i).ToString(), true);
            await SettleAsync(owner);
        }
        Require(search.Selected.Count == 10 && Find<CheckBox>(page, "BooruSelect:9210").IsChecked == false
            && Text(page).Contains(Texts.T("booru.ui.selection.exceeded", 10)), "B9: overflow was not visibly rejected.");
        TaskCompletionSource<BooruLoadResult> handoff = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Queue.OnLoad = (post, _) => post.PostId == "9219" ? handoff.Task
            : Task.FromResult(new BooruLoadResult(BooruLoadStatus.Queued));
        try
        {
            Click(Find<Button>(page, "BooruLoadSelected"));
            await Program.WaitUntilAsync(() => h.Queue.Loads.Count == loads + 2 && search.Batch.Running
                && Find<Button>(page, "BooruCancelBatch").IsEffectivelyVisible,
                () => "B9: batch did not reach its second handoff");
            Click(Find<Button>(page, "BooruCancelBatch"));
            Require(search.Batch.Running && search.Batch.CancelRequested && !search.CanSubmit,
                "B9: stop pretended to cancel an outstanding atomic handoff.");
            handoff.SetResult(new BooruLoadResult(BooruLoadStatus.Failed, BooruCodes.NotAdmitted));
            await Program.WaitUntilAsync(() => !search.Batch.Running
                && Text(page).Contains(Texts.T("booru.ui.selection.summary", 1, 1, 8)), () => "B9: partial counts missing");
            Require(h.Queue.Loads.Count == loads + 2 && h.Queue.Cancellations.Count == 0,
                "B9: stop started more handoffs or cancelled an accepted job.");
            Select("9220", false);
            await SettleAsync(owner);
            Select("9210", true); // not part of recovery snapshot
            h.Queue.OnLoad = null;
            Click(Find<Button>(page, "BooruRetryBatch"));
            await Program.WaitUntilAsync(() => !search.Batch.Running
                && Text(page).Contains(Texts.T("booru.ui.selection.summary", 9, 0, 0)), () => "B9: recovery counts missing");
            Require(h.Queue.Retries.SequenceEqual(new[] { "9219" })
                && h.Queue.Loads.Skip(loads).Select(x => x.PostId).SequenceEqual(Enumerable.Range(0, 10).Select(i => (9220 - i).ToString()))
                && h.Queue.LoadQueries.Skip(loads).All(q => q == "browse_contract"), "B9: recovery widened or replayed the successful subset.");
            await Jump(2);
            await Program.WaitUntilAsync(() => search.View.Page == 2 && search.Selected.Count == 0 && !search.Batch.Started,
                () => "B9: page change kept selection/recovery");
        }
        finally { handoff.TrySetResult(new BooruLoadResult(BooruLoadStatus.Failed)); h.Queue.OnLoad = null; h.Previews.OnPreview = null; }
        Console.WriteLine("SMOKE booru B9: explicit cap 10/visible overflow, atomic stop, 1 succeeded/1 failed/8 unprocessed, only 9 recovered despite later selection, page-scoped reset; session-only recovery.");
    }
}
