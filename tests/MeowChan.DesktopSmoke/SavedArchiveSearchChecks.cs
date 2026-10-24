using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopSmoke;

/// <summary>Real saved-search UI, two independent process phases, and a bounded fixture walkthrough.
/// Inputs are in-process control events, not OS input or a human preference study.</summary>
internal static partial class SavedArchiveSearchChecks
{
    internal const string ImplementationBasisDigest = "3b69fe4e5796db28b33ee0ca668a8fdcb86a14cd8ca79e2d293186f9d7935269";
    private const string Source = "savedfixture";
    private static readonly ArchiveSearchSpec Board = new("boardneedle", [Source], [new(Source, "b")], sort: ArchiveThreadSort.Title);
    private static readonly ArchiveSearchSpec Work = new("workneedle", [Source], [new(Source, "w")], workFilter: ArchiveWorkFilter.Watching);
    private static readonly ArchiveSearchSpec Dates = new("dateneedle", [Source], [new(Source, "d")],
        dateRange: new(new(2026, 10, 24), new(2026, 10, 26), "Europe/Berlin"));
    private static readonly string[] Names = ["Board + text", "Current watching", "Absolute DST dates"];
    private static string RuntimeSourceDigest
    {
        get
        {
            string digest = Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCH_SOURCE_DIGEST")
                ?? throw new InvalidOperationException("Saved-search smoke requires the actual immutable runtime snapshot digest in MEOWCHAN_SAVED_SEARCH_SOURCE_DIGEST.");
            Require(digest.Length == 64 && digest.All(Uri.IsHexDigit), "Runtime source digest must be a full SHA256 hex value.");
            return digest.ToLowerInvariant();
        }
    }
    private static string Stage => Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCH_STAGE") ?? "save";
    private static string Spec(ArchiveSearchSpec spec) => ArchiveSearchSpecJson.Serialize(spec);
    private static string SearchFile(string profile) => Path.Combine(profile, "archive-searches.json");
    private static string Key(ResourceId id) => id.Source + "/" + id.Board + "/" + id.Thread;
    private static string[] BoardIds(bool t1) => Enumerable.Range(t1 ? 1 : 0, 65).Select(i => Key(Id("b", i))).ToArray();
    private static ResourceId Id(string board, int number) => new(Source, board, number.ToString("D2"));
    private static string Url(ResourceId id) => "https://fixture.invalid/" + id.Board + "/" + id.Thread;
    private static string FixturePlan => JsonSerializer.Serialize(new
    {
        definitions = new[] { Spec(Board), Spec(Work), Spec(Dates) },
        boardT0 = BoardIds(false), boardT1 = BoardIds(true), workT0 = new[] { Key(Id("w", 0)) }, workT1 = new[] { Key(Id("w", 1)) },
        dates = new[] { Key(Id("d", 1)), Key(Id("d", 2)), Key(Id("d", 3)) },
        firstSeenUtc = "2026-10-23..27T12:00:00Z", removed = Key(Id("b", 0)), added = Key(Id("b", 65))
    });
    private static string FixtureDigest => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FixturePlan))).ToLowerInvariant();

    internal static async Task PrepareAsync(string profile)
    {
        _ = RuntimeSourceDigest;
        Require(Stage is "save" or "reopen" or "presentation" or "contracts", "MEOWCHAN_SAVED_SEARCH_STAGE must be save, reopen, presentation or contracts.");
        if (Stage == "presentation")
        {
            Require(File.Exists(Path.Combine(profile, "saved-search-reopen-complete.json")), "Presentation requires a completed save/reopen test profile.");
            Require(!File.Exists(Path.Combine(profile, "saved-search-presentation-evidence.jsonl")), "Presentation evidence exists; copy the completed functional profile to a fresh test path.");
            using JsonDocument completed = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, "saved-search-reopen-complete.json")));
            Require(completed.RootElement.GetProperty("fixtureDigest").GetString() == FixtureDigest
                && completed.RootElement.GetProperty("savedFileHash").GetString() == Hash(File.ReadAllBytes(SearchFile(profile))), "Presentation profile definitions/fixture differ from the completed functional pair.");
            return; // Read-only validation: do not seed or mutate archive/work/persisted definitions.
        }
        string planPath = Path.Combine(profile, "saved-search-fixture.json");
        if (Stage is "save" or "contracts")
        {
            Require(!File.Exists(SearchFile(profile)) && !File.Exists(planPath), "Save phase requires a fresh marked test profile.");
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            for (int i = 0; i < 65; i++) await CaptureAsync(catalog, profile, Id("b", i), "boardneedle", new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
            for (int i = 0; i < 2; i++) await CaptureAsync(catalog, profile, Id("w", i), "workneedle", new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
            for (int i = 0; i < 5; i++) await CaptureAsync(catalog, profile, Id("d", i), "dateneedle", new(2026, 10, 23 + i, 12, 0, 0, TimeSpan.Zero));
            File.WriteAllText(planPath, FixturePlan);
        }
        else
        {
            Require(File.Exists(Path.Combine(profile, "saved-search-save-complete.json")), "Reopen phase requires a completed save process.");
            Require(File.ReadAllText(planPath) == FixturePlan, "Restart fixture definition changed.");
            byte[] savedBefore = File.ReadAllBytes(SearchFile(profile));
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            await catalog.DeleteThreadsAsync([Id("b", 0)]);
            await CaptureAsync(catalog, profile, Id("b", 65), "boardneedle", new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
            Require(savedBefore.SequenceEqual(File.ReadAllBytes(SearchFile(profile))), "Preparing T1 altered saved definitions.");
        }
    }
    private static Task CaptureAsync(ArchiveCatalog catalog, string profile, ResourceId id, string needle, DateTimeOffset captured) =>
        catalog.CaptureAsync(new(id, Url(id), needle + " " + id.Thread, ThreadSourceState.Active, captured,
            Path.Combine(profile, "fixture-media", id.Board, id.Thread!), [new(id.Thread!, 0, PostFieldPresence.Html, Html: needle)]));

    internal static async Task RunAsync(MainWindow owner, string profile)
    {
        if (double.TryParse(Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCH_WIDTH"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double width)) owner.Width = width;
        if (double.TryParse(Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCH_HEIGHT"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double height)) owner.Height = height;
        owner.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        using var log = new Evidence(profile, owner);
        log.Note("start", "No automatic saved-search startup; Downloads remains selected", new { stage = Stage });
        Require(owner.SelectedPage == MainWindow.WorkspacePage.Downloads, "Saved search automatically changed the startup workspace.");
        ArchivePage page = owner.ArchivePage;
        bool t1 = Stage is "reopen" or "presentation";
        page.GetWorkSnapshots = () => Enumerable.Range(0, 2).Select(i => new ThreadWorkSnapshot(Url(Id("w", i)),
            i == (t1 ? 1 : 0) ? WorkIntent.Watching : WorkIntent.Completed, ThreadSourceStateValue.Active, null, false, [])).ToArray();
        log.Note("fixture preparation", "Work membership is a passive fixture projection; no provider/watch is created", new { watching = Key(Id("w", t1 ? 1 : 0)) });
        Button navigation = owner.FindControl<Button>("ArchiveNavigationButton")!;
        if (navigation.IsEffectivelyVisible) await ClickAsync(navigation, log, "Open local archive");
        else
        {
            var menu = owner.FindControl<MenuItem>("WorkspaceMenuItem")!;
            log.Action("open workspace menu", "Reveal the actual Archive menu entry", new { control = menu.Name });
            menu.Focus(NavigationMethod.Tab); menu.IsSubMenuOpen = true;
            await Program.WaitUntilAsync(() => owner.FindControl<MenuItem>("ArchiveMenuItem")!.IsEffectivelyVisible);
            await ClickAsync(owner.FindControl<MenuItem>("ArchiveMenuItem")!, log, "Open local archive from workspace menu");
            menu.IsSubMenuOpen = false;
        }
        await Program.WaitUntilAsync(() => !page.IsBusy && page.AppliedSearchSpec != null);
        if (Stage == "contracts") await ContractsAsync(owner, page, profile, log);
        else if (Stage == "presentation") await PresentationAsync(owner, page, profile, log);
        else if (t1) await ReopenAsync(owner, page, profile, log);
        else await SaveAsync(owner, page, profile, log);
        Require(owner.SessionHost.GetView().Threads.Count == 0 && owner.SessionHost.GetView().Boards.Count == 0
            && owner.SessionHost.ReceivedMediaBytes == 0, "Saved-search fixture created watches or received media.");
        log.Note("complete", "No human preference/native AT/IME claim; source-built in-process controls only", new { stage = Stage });
        Console.WriteLine(Stage == "contracts" ? "SMOKE saved searches contracts: overlapping selection, failure retention, frozen retry and dialog cancellation passed."
            : Stage == "presentation" ? "SMOKE saved searches presentation: 12 real dialog cells completed; functional pair not repeated."
            : "SMOKE saved searches " + Stage + ": real save/apply/dirty/replace/restart/DST/export assertions completed.");
    }

    private static async Task SaveAsync(MainWindow owner, ArchivePage page, string profile, Evidence log)
    {
        await SetupAppliedAsync(page, Board, log);
        log.Note("fixture API preparation", "Prepare page two (offset 50), not a UI pagination action", new { page = 2 });
        Require(await page.NavigateResultsPage(2), "Fixture could not prepare page two.");
        Edit(page.FindControl<TextBox>("ArchiveSearchText")!, "UNSEARCHED B", log, "Draft B must not become the saved definition");
        SavedArchiveSearchesDialog manager = await OpenAsync(owner, page, save: true, log);
        await SaveNameAsync(manager, Names[0], owner, log);
        Require(Saved(profile).Searches.Single().Definition!.Equals(Board), "SaveCurrent persisted draft B or lost applied A.");
        Require(Offset(page) == 50 && page.FindControl<TextBox>("ArchiveSearchText")!.Text == "UNSEARCHED B", "SaveCurrent applied the draft or changed the results page.");
        await CloseAsync(manager, owner, log);
        await SetupAppliedAsync(page, Work, log); AssertRows(page, [Key(Id("w", 0))]);
        manager = await OpenAsync(owner, page, true, log); await SaveNameAsync(manager, Names[1], owner, log); await CloseAsync(manager, owner, log);
        await SetupAppliedAsync(page, Dates, log); AssertRows(page, [Key(Id("d", 1)), Key(Id("d", 2)), Key(Id("d", 3))]);
        manager = await OpenAsync(owner, page, true, log); await SaveNameAsync(manager, Names[2], owner, log); await CloseAsync(manager, owner, log);
        AssertDateDefinition(Saved(profile).Searches.Single(x => x.Name == Names[2]).Definition!);

        var dateRows = page.FindControl<ListBox>("ArchiveThreads")!;
        log.Action("select result", "Select one Date result; applying saved Board must not restore this selection", new { control = dateRows.Name, id = Key(Id("d", 1)) });
        dateRows.SelectedItem = dateRows.Items.OfType<ArchiveThreadRow>().Single(row => row.Summary.Id == Id("d", 1));
        await Program.WaitUntilAsync(() => !page.IsBusy);
        byte[] beforeDirty = File.ReadAllBytes(SearchFile(profile));
        Edit(page.FindControl<TextBox>("ArchiveSearchText")!, "UNSEARCHED DIRTY", log, "Cancel preserves draft and current date result");
        manager = await OpenAsync(owner, page, false, log); Select(manager, Names[0], log);
        await ClickAsync(Control<Button>(manager, "SavedSearchApply"), log, "Selection alone never applies; Apply asks before overwriting dirty draft");
        await Program.WaitUntilAsync(() => Control<StackPanel>(manager, "SavedSearchEditor").IsVisible);
        Require(Control<TextBlock>(manager, "SavedSearchPrompt").Text == Texts.T("savedSearch.prompt.Apply", Names[0]), "Dirty apply lacked explicit confirmation.");
        await ClickAsync(Control<Button>(manager, "SavedSearchCancelEdit"), log, "Abort dirty apply");
        Require(page.FindControl<TextBox>("ArchiveSearchText")!.Text == "UNSEARCHED DIRTY" && page.AppliedSearchSpec!.Equals(Dates), "Aborting saved apply lost draft or old result.");
        Require(beforeDirty.SequenceEqual(File.ReadAllBytes(SearchFile(profile))), "Canceled apply changed persisted definitions.");
        await ClickAsync(Control<Button>(manager, "SavedSearchApply"), log, "Repeat explicit dirty apply");
        await ClickAsync(Control<Button>(manager, "SavedSearchConfirm"), log, "Confirm replacing dirty draft and applying Board A");
        await WaitClosedAsync(manager, owner, page); Require(page.AppliedSearchSpec!.Equals(Board) && Offset(page) == 0, "Confirmed apply did not return A on page one.");
        AssertRows(page, BoardIds(false).Take(50).ToArray());
        Require(page.FindControl<ListBox>("ArchiveThreads")!.SelectedItems!.Count == 0, "Saved apply restored a selection.");
        Require(beforeDirty.SequenceEqual(File.ReadAllBytes(SearchFile(profile))), "Applying changed the saved file.");

        // A scratch entry makes replacement observable without altering the three restart definitions.
        manager = await OpenAsync(owner, page, true, log); await SaveNameAsync(manager, "Scratch replacement", owner, log); await CloseAsync(manager, owner, log);
        Guid target = Saved(profile).Searches.Single(x => x.Name == "Scratch replacement").Id;
        await SetupAppliedAsync(page, Work, log);
        Edit(page.FindControl<TextBox>("ArchiveSearchText")!, "UNSEARCHED REPLACE", log, "Replacement must use applied Work, not this text draft");
        manager = await OpenAsync(owner, page, false, log); Select(manager, "Scratch replacement", log);
        await ClickAsync(Control<Button>(manager, "SavedSearchReplace"), log, "Preview replacement target and applied definition");
        Require(Control<TextBlock>(manager, "SavedSearchPrompt").Text == Texts.T("savedSearch.prompt.Replace", "Scratch replacement")
            && Control<TextBlock>(manager, "SavedSearchDefinition").Text!.Contains("workneedle", StringComparison.Ordinal)
            && !Control<TextBlock>(manager, "SavedSearchDefinition").Text!.Contains("UNSEARCHED REPLACE", StringComparison.Ordinal), "Replace preview used a draft or wrong target.");
        await ClickAsync(Control<Button>(manager, "SavedSearchConfirm"), log, "Commit replacement");
        await Program.WaitUntilAsync(() => Control<TextBlock>(manager, "SavedSearchStatus").Text == Texts.T("savedSearch.saved"));
        var replaced = Saved(profile).Searches.Single(x => x.Id == target); Require(replaced.Definition!.Equals(Work), "Replace changed identity or stored unapplied fields.");
        await CloseAsync(manager, owner, log);
        await CheckNameDiscardAsync(owner, page, profile, log);
        await CheckFrozenExportAsync(owner, page, log);
        await SavedSearchVariantWalkthrough.RunAsync(owner, profile, FixtureDigest, BoardIds(false), BoardIds(true), log.Action);
        var saved = Saved(profile);
        File.WriteAllText(Path.Combine(profile, "saved-search-save-complete.json"), JsonSerializer.Serialize(new
        { sourceDigest = RuntimeSourceDigest, implementationBasisDigest = ImplementationBasisDigest, fixtureDigest = FixtureDigest, processId = Environment.ProcessId, savedFileHash = Hash(File.ReadAllBytes(SearchFile(profile))), identities = saved.Searches.Select(x => new { x.Id, x.Name, definition = Spec(x.Definition!) }) }));
        Program.CaptureWindow(owner, "smoke-saved-searches-save-complete.png");
    }

    private static async Task ReopenAsync(MainWindow owner, ArchivePage page, string profile, Evidence log)
    {
        using JsonDocument prior = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, "saved-search-save-complete.json")));
        byte[] original = File.ReadAllBytes(SearchFile(profile));
        Require(prior.RootElement.GetProperty("processId").GetInt32() != Environment.ProcessId, "Reopen must use a new OS process.");
        log.Note("process relaunch", "New PID, same marked test profile, identical saved bytes, controlled T1 archive/work", new
        { previousPid = prior.RootElement.GetProperty("processId").GetInt32(), currentPid = Environment.ProcessId, profile });
        Require(prior.RootElement.GetProperty("fixtureDigest").GetString() == FixtureDigest
            && prior.RootElement.GetProperty("savedFileHash").GetString() == Hash(original), "Definitions changed between processes/T1 seeding.");
        var snapshot = Saved(profile); AssertDateDefinition(snapshot.Searches.Single(x => x.Name == Names[2]).Definition!);
        foreach (var task in new[] { (Names[0], Board, BoardIds(true).Take(50).ToArray()),
            (Names[1], Work, new[] { Key(Id("w", 1)) }), (Names[2], Dates, new[] { Key(Id("d", 1)), Key(Id("d", 2)), Key(Id("d", 3)) }) })
        {
            Edit(page.FindControl<TextBox>("ArchiveSearchText")!, "restart dirty draft", log, "Apply must explicitly replace this draft with " + task.Item1);
            var manager = await OpenAsync(owner, page, false, log); Select(manager, task.Item1, log);
            Require(Control<TextBlock>(manager, "SavedSearchStatus").Text != Texts.T("savedSearch.working"), "Manager did not finish reading.");
            await ClickAsync(Control<Button>(manager, "SavedSearchApply"), log, "Run selected saved definition against current T1 archive/work");
            await ClickAsync(Control<Button>(manager, "SavedSearchConfirm"), log, "Confirm replacing restart draft");
            await WaitClosedAsync(manager, owner, page); Require(page.AppliedSearchSpec!.Equals(task.Item2) && Offset(page) == 0, "Reopen applied wrong definition/page.");
            AssertRows(page, task.Item3); Require(original.SequenceEqual(File.ReadAllBytes(SearchFile(profile))), "Applying T1 rewrote definitions.");
            log.Note("observed T1", "Current local IDs must equal predicted IDs", new { name = task.Item1, ids = Rows(page), storedDefinition = Spec(task.Item2) });
        }
        Edit(page.FindControl<TextBox>("ArchiveSearchText")!, "edited without save", log, "Editing alone never updates saved definitions");
        Require(original.SequenceEqual(File.ReadAllBytes(SearchFile(profile))), "Post-apply editing modified a saved definition.");
        AssertDateDefinition(Saved(profile).Searches.Single(x => x.Name == Names[2]).Definition!);
        Program.CaptureWindow(owner, "smoke-saved-searches-reopen-T1.png");
        File.WriteAllText(Path.Combine(profile, "saved-search-reopen-complete.json"), JsonSerializer.Serialize(new { sourceDigest = RuntimeSourceDigest, implementationBasisDigest = ImplementationBasisDigest, fixtureDigest = FixtureDigest, savedFileHash = Hash(original), result = "passed assertions" }));
    }
    private static async Task CheckNameDiscardAsync(MainWindow owner, ArchivePage page, string profile, Evidence log)
    {
        byte[] original = File.ReadAllBytes(SearchFile(profile));
        var dialog = await OpenAsync(owner, page, false, log); Select(dialog, "Scratch replacement", log);
        await ClickAsync(Control<Button>(dialog, "SavedSearchRename"), log, "Rename starts an editor without writing");
        TextBox input = Control<TextBox>(dialog, "SavedSearchName");
        Edit(input, "UNSAVED name 🐾", log, "Escape must ask before discarding this raw name");
        log.Action("key Escape", "Show discard confirmation without writing", new { control = input.Name });
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
        Require(Control<TextBlock>(dialog, "SavedSearchPrompt").Text == Texts.T("savedSearch.prompt.Discard", "Scratch replacement"), "Escape silently lost the raw name.");
        await ClickAsync(Control<Button>(dialog, "SavedSearchCancelEdit"), log, "Keep editing preserves name and restores its focus");
        Require(input.Text == "UNSAVED name 🐾" && input.IsKeyboardFocusWithin, "Keep editing lost raw name/focus.");
        await ClickAsync(Control<Button>(dialog, "SavedSearchCancelEdit"), log, "Ask before discarding the name again");
        await ClickAsync(Control<Button>(dialog, "SavedSearchConfirm"), log, "Explicitly discard unsaved rename");
        Require(original.SequenceEqual(File.ReadAllBytes(SearchFile(profile))), "Canceled rename wrote to the store.");
        await CloseAsync(dialog, owner, log);
    }

    private static async Task CheckFrozenExportAsync(MainWindow owner, ArchivePage page, Evidence log)
    {
        await SetupAppliedAsync(page, Board, log);
        Edit(page.FindControl<TextBox>("ArchiveSearchText")!, "UNSEARCHED EXPORT B", log, "Export captures applied Board A across all pages");
        Button export = page.FindControl<Button>("ArchiveResultsExport")!;
        await ClickAsync(export, log, "Open explicit all-results export choice");
        Require(export.ContextMenu?.IsOpen == true, "Export scope menu did not open.");
        var item = export.ContextMenu!.Items.OfType<MenuItem>().Single();
        await ClickAsync(item, log, "Freeze 65 matching IDs from applied Board A");
        export.ContextMenu.Close();
        await Program.WaitUntilAsync(() => page.FindControl<Border>("ArchiveExportDialog")?.IsVisible == true
            && page.FindControl<Button>("ArchiveExportStart")?.IsEnabled == true);
        string summary = page.FindControl<TextBlock>("ArchiveExportSummary")!.Text!;
        var frozenRequest = (ArchiveSearchRequest)typeof(ArchivePage).GetField("_appliedSearch", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        string[] frozen = ExportIds(page); Require(frozen.SequenceEqual(BoardIds(false)), "Export captured draft/page membership instead of all applied IDs.");
        // The overlay intentionally blocks underlying UI. This external change is fixture injection,
        // never reported as a user's ability to interact through a modal overlay.
        await SetupAppliedAsync(page, Work, log);
        Require(frozenRequest.Spec.Equals(Board) && page.AppliedSearchSpec!.Equals(Work)
            && ExportIds(page).SequenceEqual(frozen) && page.FindControl<TextBlock>("ArchiveExportSummary")!.Text == summary,
            "An external later applied search rewrote the open export's scope.");
        log.Note("observed frozen export", "The open export retains its query-derived IDs after a later search", new { frozenIds = frozen, currentIds = Rows(page), summary });
        Program.CaptureWindow(owner, "smoke-saved-searches-frozen-export.png");
        await ClickAsync(page.FindControl<Button>("ArchiveExportClose")!, log, "Close frozen export without creating output");
    }

    private sealed record SavedEntry(Guid Id, string Name, ArchiveSearchSpec Definition);
    private sealed record SavedSnapshot(long Revision, SavedEntry[] Searches);
    private static SavedSnapshot Saved(string profile)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(SearchFile(profile)));
        var root = json.RootElement;
        return new(root.GetProperty("revision").GetInt64(), root.GetProperty("searches").EnumerateArray().Select(entry =>
            new SavedEntry(entry.GetProperty("id").GetGuid(), entry.GetProperty("name").GetString()!,
                ArchiveSearchSpecJson.Deserialize(entry.GetProperty("definition").GetRawText()))).ToArray());
    }
    private static T Control<T>(Control root, string name) where T : Control => root.FindControl<T>(name)
        ?? throw new InvalidOperationException("Missing harness target " + name);
    private static int Offset(ArchivePage page) => (int)typeof(ArchivePage).GetField("_listOffset", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
    private static string[] ExportIds(ArchivePage page) => ((ResourceId[]?)typeof(ArchivePage).GetField("_exportMembers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)
        ?? throw new InvalidOperationException("No frozen export members.")).Select(Key).ToArray();
    private static string[] Rows(ArchivePage page) => page.FindControl<ListBox>("ArchiveThreads")!.Items
        .OfType<ArchiveThreadRow>().Select(row => Key(row.Summary.Id)).ToArray();
    private static void AssertRows(ArchivePage page, string[] expected) => Require(Rows(page).OrderBy(x => x, StringComparer.Ordinal)
        .SequenceEqual(expected.OrderBy(x => x, StringComparer.Ordinal)), "Displayed IDs differ: " + string.Join(",", Rows(page)));
    private static void AssertDateDefinition(ArchiveSearchSpec spec)
    {
        Require(spec.Equals(Dates), "Persisted absolute date definition changed.");
        ResolvedDateWindow window = ArchiveSearchRequest.Create(spec).DateWindow!;
        Require(window.StartInclusive == new DateTimeOffset(2026, 10, 23, 22, 0, 0, TimeSpan.Zero)
            && window.EndExclusive == new DateTimeOffset(2026, 10, 26, 23, 0, 0, TimeSpan.Zero)
            && window.EndExclusive - window.StartInclusive == TimeSpan.FromHours(73), "DST window is not the expected 73-hour absolute range.");
        using JsonDocument json = JsonDocument.Parse(Spec(spec));
        Require(json.RootElement.GetProperty("dateRange").EnumerateObject().Select(x => x.Name)
            .OrderBy(x => x).SequenceEqual(new[] { "basis", "endDate", "startDate", "timeZoneId" }), "Derived UTC/relative values leaked into v1 date definition.");
    }
    private static async Task SetupAppliedAsync(ArchivePage page, ArchiveSearchSpec spec, Evidence log)
    {
        log.Note("fixture API preparation", "Apply immutable fixture definition; this is not a UI action", new { definition = Spec(spec) });
        Require(await page.ApplySearchSpecAsync(spec), "Could not prepare the applied fixture definition.");
        await Program.WaitUntilAsync(() => !page.IsBusy);
    }
    private static void Edit(TextBox input, string value, Evidence log, string prediction)
    {
        Require(input.IsEffectivelyVisible && input.IsEnabled, "Cannot edit hidden/disabled " + input.Name);
        log.Action("edit", prediction, new { control = input.Name, rawBefore = input.Text, rawAfter = value });
        Require(input.Focus(NavigationMethod.Tab), "Could not focus " + input.Name);
        input.Text = value;
        log.Note("observed edit", prediction, new { control = input.Name, raw = input.Text });
    }
    private static async Task ClickAsync(Control control, Evidence log, string prediction)
    {
        if (!control.IsEffectivelyVisible)
        {
            var toolbar = log.Owner.ArchivePage.FindControl<ActionToolBar>("ArchiveResultActions")!;
            if (toolbar.Items.Contains(control) && toolbar.More is { IsEffectivelyVisible: true } more)
            {
                log.Action("toggle overflow", "Reveal original toolbar action " + control.Name, new { control = more.Name });
                more.Focus(NavigationMethod.Tab); more.IsChecked = true;
                await Program.WaitUntilAsync(() => control.IsEffectivelyVisible);
            }
        }
        Require(control.IsEffectivelyVisible && control.IsEnabled, "Cannot activate hidden/disabled " + control.Name);
        log.Action("click", prediction, new { control = control.Name ?? control.GetType().Name });
        control.Focus(NavigationMethod.Tab);
        SmokeActionClick.Execute(control);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        log.Note("observed control event", prediction, new { control = control.Name ?? control.GetType().Name });
    }
    private static async Task<SavedArchiveSearchesDialog> OpenAsync(MainWindow owner, ArchivePage page, bool save, Evidence log)
    {
        await ClickAsync(page.FindControl<Button>(save ? "ArchiveSaveSearch" : "ArchiveSavedSearches")!, log,
            save ? "Save freezes applied A and focuses the name editor" : "Manager loads definitions without applying any selection");
        await Program.WaitUntilAsync(() => owner.OwnedWindows.OfType<SavedArchiveSearchesDialog>().Any(x => x.IsVisible));
        var dialog = owner.OwnedWindows.OfType<SavedArchiveSearchesDialog>().Single(x => x.IsVisible);
        await Program.WaitUntilAsync(() => save ? Control<TextBox>(dialog, "SavedSearchName").IsVisible && Control<Button>(dialog, "SavedSearchConfirm").IsEnabled
            : Control<Button>(dialog, "SavedSearchCreate").IsEnabled);
        await AssertFocusAsync(dialog, save ? Control<TextBox>(dialog, "SavedSearchName") : Control<ListBox>(dialog, "SavedSearchList"), log, "initial dialog focus");
        Require(save ? Control<TextBox>(dialog, "SavedSearchName").IsKeyboardFocusWithin : Control<ListBox>(dialog, "SavedSearchList").IsKeyboardFocusWithin,
            "Saved-search dialog has wrong initial focus.");
        await NativeFeatureCheckpoint.HoldAsync(save ? "saved-search-name" : "saved-search-manager", dialog, save ? Control<TextBox>(dialog, "SavedSearchName") : Control<ListBox>(dialog, "SavedSearchList"), save ? Control<TextBox>(dialog, "SavedSearchName") : Control<ListBox>(dialog, "SavedSearchList"), Control<Button>(dialog, save ? "SavedSearchConfirm" : "SavedSearchClose"));
        log.Note("observed manager ready", "Initial focus is name for Save, list for manager", new { save, focus = FocusLabel(dialog) });
        return dialog;
    }
    private static async Task SaveNameAsync(SavedArchiveSearchesDialog dialog, string name, MainWindow owner, Evidence log)
    {
        Edit(Control<TextBox>(dialog, "SavedSearchName"), name, log, "Only Confirm writes the applied definition under this name");
        await ClickAsync(Control<Button>(dialog, "SavedSearchConfirm"), log, "Save named applied definition");
        await Program.WaitUntilAsync(() => !Control<StackPanel>(dialog, "SavedSearchEditor").IsVisible
            && Control<TextBlock>(dialog, "SavedSearchStatus").Text == Texts.T("savedSearch.saved"));
        await AssertFocusAsync(dialog, Control<ListBox>(dialog, "SavedSearchList"), log, "post-write list focus");
        log.Note("observed saved", "Definition and persisted bytes are available for independent assertions", new { name });
    }
    private static string? FocusLabel(Window window) => window.FocusManager?.GetFocusedElement() is Control control
        ? control.Name ?? control.GetType().Name : window.FocusManager?.GetFocusedElement()?.ToString();
    private static async Task AssertFocusAsync(Window dialog, Control target, Evidence log, string context)
    {
        try { await Program.WaitUntilAsync(() => target.IsKeyboardFocusWithin); }
        catch (TimeoutException)
        {
            log.Note("focus failure", "Real focus must reach " + target.Name + "; harness never sets it", new
            { context, expected = target.Name, actual = FocusLabel(dialog), target.Focusable, target.IsEnabled, target.IsEffectivelyVisible });
            throw;
        }
        log.Note("observed focus", "Real focus reached " + target.Name + " without harness injection", new
        { context, expected = target.Name, actual = FocusLabel(dialog), target.Focusable, target.IsEnabled, target.IsEffectivelyVisible });
    }
    private static void Select(SavedArchiveSearchesDialog dialog, string name, Evidence log)
    {
        var list = Control<ListBox>(dialog, "SavedSearchList");
        string definition = Spec(log.Owner.ArchivePage.AppliedSearchSpec!); string[] ids = Rows(log.Owner.ArchivePage);
        string? rawBefore = log.Owner.ArchivePage.FindControl<TextBox>("ArchiveSearchText")!.Text;
        log.Action("select", "Selecting " + name + " alone preserves results and draft", new { control = list.Name, name });
        list.Focus(NavigationMethod.Tab);
        list.SelectedItem = list.ItemsSource!.Cast<SavedArchiveSearch>().Single(x => x.Name == name);
        Require(Spec(log.Owner.ArchivePage.AppliedSearchSpec!) == definition && Rows(log.Owner.ArchivePage).SequenceEqual(ids)
            && log.Owner.ArchivePage.FindControl<TextBox>("ArchiveSearchText")!.Text == rawBefore, "Selection alone applied a search or lost raw draft.");
        log.Note("observed selection", "Only list selection changed", new { name });
    }
    private static async Task CloseAsync(SavedArchiveSearchesDialog dialog, MainWindow owner, Evidence log)
    {
        await ClickAsync(Control<Button>(dialog, "SavedSearchClose"), log, "Close manager without changing definition or draft");
        await WaitClosedAsync(dialog, owner, owner.ArchivePage);
    }
    private static async Task WaitClosedAsync(SavedArchiveSearchesDialog dialog, MainWindow owner, ArchivePage page) =>
        await Program.WaitUntilAsync(() => !dialog.IsVisible && !owner.OwnedWindows.Contains(dialog) && !page.IsBusy
            && page.FindControl<Button>("ArchiveSavedSearches")!.IsEnabled);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Evidence : IDisposable
    {
        private readonly string _profile;
        private readonly StreamWriter _writer;
        private readonly Config _actualConfig;
        private int _sequence;
        internal MainWindow Owner { get; }
        internal Evidence(string profile, MainWindow owner)
        {
            _profile = profile; Owner = owner; _actualConfig = Config.LoadConfig(profile);
            _writer = new StreamWriter(Path.Combine(profile, "saved-search-" + Stage + "-evidence.jsonl"), append: false) { AutoFlush = true };
            Note("metadata", "Bounded harness observation; preference/native AT/IME remain open", new
            {
                package = "URSA-18", sourceDigest = RuntimeSourceDigest, implementationBasisDigest = ImplementationBasisDigest,
                harnessDigest = Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCH_HARNESS_DIGEST") ?? "unspecified-see-handoff",
                fixtureDigest = FixtureDigest, fixture = JsonDocument.Parse(FixturePlan).RootElement.Clone(), observerKind = "harness",
                input = "in-process control events; direct fixture APIs separately labeled", processId = Environment.ProcessId,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                backend = Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1" ? "X11/software (runner isolation)" : "platform-detected; inspect runner evidence",
                scale = owner.RenderScaling, size = owner.ClientSize.ToString(), language = Texts.Culture.Name,
                theme = _actualConfig.Theme, mode = _actualConfig.WorkspaceMode
            });
        }
        internal void Action(string action, string prediction, object? detail) => Write("before action", action, prediction, detail);
        internal void Note(string action, string prediction, object? detail) => Write("observation", action, prediction, detail);
        private void Write(string kind, string action, string prediction, object? detail)
        {
            var page = Owner.ArchivePage;
            string? fileHash = File.Exists(SearchFile(_profile)) ? Hash(File.ReadAllBytes(SearchFile(_profile))) : null;
            Window active = Owner.OwnedWindows.LastOrDefault(x => x.IsVisible) ?? Owner;
            string line = JsonSerializer.Serialize(new { sequence = ++_sequence, atUtc = DateTimeOffset.UtcNow, stage = Stage, sourceDigest = RuntimeSourceDigest,
                kind, action, prediction, detail, focus = FocusLabel(active),
                rawDraft = page.FindControl<TextBox>("ArchiveSearchText")?.Text, displayedIds = Rows(page),
                applied = page.AppliedSearchSpec == null ? null : Spec(page.AppliedSearchSpec), persistedHash = fileHash });
            _writer.WriteLine(line); Console.WriteLine("SAVEDSEARCH_EVIDENCE " + line);
        }
        public void Dispose() => _writer.Dispose();
    }
}
