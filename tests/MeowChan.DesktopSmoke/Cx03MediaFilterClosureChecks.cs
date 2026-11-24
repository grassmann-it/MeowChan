using System.Reflection;
using Avalonia.Controls;
using MeowChan.Gallery;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Part of the media-page scenario (MediaPageChecks.RunAsync); shares the existing media fixture helpers.
internal static partial class MediaPageChecks
{
    internal static async Task Cx03MediaFilterClosureAsync(MainWindow owner)
    {
        await DisappearingFilterJourneyAsync(owner);
        await AudioScanReplacementJourneyAsync(owner);
        Console.WriteLine("SMOKE CX-03 disappearing filter journey passed.");
    }

    // Switch between isolated local catalogs on real forced rebuilds. Equal bytes make an accidental
    // selection restoration onto a different source/board observable, rather than merely unlikely.
    private static async Task DisappearingFilterJourneyAsync(MainWindow owner)
    {
        string initial = TemporaryWorkspacePaths.CreateDirectory("media-filter-initial-");
        string noSource = TemporaryWorkspacePaths.CreateDirectory("media-filter-no-source-");
        string noBoard = TemporaryWorkspacePaths.CreateDirectory("media-filter-no-board-");
        ResourceId ax = new("filter-a", "x", "101"), by = new("filter-b", "y", "201"), ay = new("filter-a", "y", "301");
        await SeedFilterRefreshAsync(initial, ax, by);
        await SeedFilterRefreshAsync(noSource, by);
        await SeedFilterRefreshAsync(noBoard, ay, by);
        await using DesktopSessionHost first = new(initial, Array.Empty<IImageBoard>());
        await using DesktopSessionHost second = new(noSource, Array.Empty<IImageBoard>());
        await using DesktopSessionHost third = new(noBoard, Array.Empty<IImageBoard>());
        await first.StartAsync().WaitAsync(Deadline);
        await second.StartAsync().WaitAsync(Deadline);
        await third.StartAsync().WaitAsync(Deadline);
        DesktopSessionHost active = first;
        int exports = 0;
        MediaPage page = new(token => active.OpenArchiveReaderAsync(token))
        {
            ExportOriginals = (_, _, _) => { exports++; return Task.CompletedTask; },
            ExportAllConfirmAbove = (0, long.MaxValue)
        };
        Window window = new() { Width = 1200, Height = 800, Content = page, Title = "Synthetic disappearing media filters", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            ComboBox source = C<ComboBox>(page, "MediaSourceFilter"), board = C<ComboBox>(page, "MediaBoardFilter");
            Button exportAll = C<Button>(page, "MediaExportAll"),
                exportSelected = C<Button>(page, "MediaSelectionExport");
            async Task Rebuild(DesktopSessionHost host)
            {
                // Deterministically cross the completed-and-disposed audio-scan boundary.
                await page.AudioScanTask.WaitAsync(Deadline);
                active = host;
                ClickRaise(C<Button>(page, "MediaRefresh"));
                await page.BuildTask.WaitAsync(Deadline);
                Require(page.Snapshot is { Complete: true }, "Refresh fixture must build a complete index.");
                Require(!C<TextBlock>(page, "MediaStatus").Text!.Contains(Texts.T("media.failed"), StringComparison.Ordinal),
                    "A completed audio scan must not turn the following rebuild into an archive read failure.");
            }
            void Select(ComboBox combo, string label) => combo.SelectedItem = combo.Items.Cast<object>().Single(item => item.ToString() == label);
            void PrepareSelectionAndAsk()
            {
                Require(page.Tiles.Count == 1 && page.ShownEntries.Count == 1, "The filter fixture must show one tile.");
                page.Grid.SelectedIndex = 0;
                Require(page.SelectedTiles.Count == 1 && exportSelected.IsEnabled && exportAll.IsEnabled, "The matching tile must be selected and exportable.");
                ClickRaise(exportAll);
                Require(page.ExportAllAsking && exports == 0, "The old presentation must have a pending export confirmation.");
            }
            async Task RequireClosed(string sourceId, string? boardId)
            {
                Require(page.CurrentFilter().Source == sourceId && page.CurrentFilter().Board == boardId && page.ActiveFilterCount > 0,
                    "A disappearing identity must remain an active filter.");
                Require(page.ShownEntries.Count == 0 && page.Tiles.Count == 0 && page.SelectedTiles.Count == 0
                    && C<TextBlock>(page, "MediaStatus").Text == CountPhrases.MediaFilterSummary(0, 1)
                    && C<TextBlock>(page, "MediaEmpty").IsVisible, "Missing identities must produce a truthful empty presentation and prune selection.");
                Require(!exportSelected.IsEnabled && !exportAll.IsEnabled && !page.ExportAllAsking,
                    "No selection or export-all target, including a pending confirmation, may survive the refresh.");
                await page.ExportSelectionAsync();
                await page.ExportAllAsync();
                ClickRaise(C<Button>(page, "MediaExportAllConfirmYes"));
                Require(exports == 0, "Disabled or stale export actions must not export a widened target.");
            }

            // Source alone must not widen even if there is no board constraint to mask the loss.
            Select(source, "filter-a");
            PrepareSelectionAndAsk();
            await Rebuild(second);
            await RequireClosed("filter-a", null);
            Require(source.SelectedItem!.ToString() == "filter-a (" + Texts.T("mediahash.unavailable") + ")"
                && board.SelectedIndex == 0, "The missing source alone must remain selected.");
            page.ResetFilters();
            Require(page.CurrentFilter().IsEmpty && page.ShownEntries.Count == 1 && page.ShownEntries[0].Thread == by
                && page.SelectedTiles.Count == 0 && page.ActiveFilterCount == 0, "Reset must explicitly release the missing source.");

            await Rebuild(first);
            Select(source, "filter-a");
            Select(board, "/x/");
            PrepareSelectionAndAsk();
            await Rebuild(second);
            await RequireClosed("filter-a", "x");
            Require(source.SelectedItem!.ToString() == "filter-a (" + Texts.T("mediahash.unavailable") + ")"
                && board.SelectedItem!.ToString() == "/x/ (" + Texts.T("mediahash.unavailable") + ")"
                && source.Items.Cast<object>().Any(item => item.ToString() == "filter-b"), "Missing source and board must be identified while valid sources remain offered.");
            Texts.SetLanguage("de");
            await UntilAsync(() => source.SelectedItem!.ToString() == "filter-a (" + Texts.T("mediahash.unavailable") + ")");
            await RequireClosed("filter-a", "x");
            Texts.SetLanguage("en");
            await UntilAsync(() => source.SelectedItem!.ToString() == "filter-a (" + Texts.T("mediahash.unavailable") + ")");
            await Rebuild(first);
            Require(page.ShownEntries.Count == 1 && page.CurrentFilter().Source == "filter-a" && page.CurrentFilter().Board == "x"
                && source.SelectedItem!.ToString() == "filter-a" && board.SelectedItem!.ToString() == "/x/"
                && page.SelectedTiles.Count == 0, "A returning identity must become available without resurrecting the pruned selection.");
            await Rebuild(second);
            await RequireClosed("filter-a", "x");
            page.ResetFilters();
            Require(page.CurrentFilter().IsEmpty && page.ShownEntries.Count == 1 && page.ShownEntries[0].Thread == by
                && page.SelectedTiles.Count == 0 && exportAll.IsEnabled && page.ActiveFilterCount == 0
                && source.Items.Count == 2 && board.Items.Count == 2, "Explicit reset must restore only the new snapshot, without restoring selection or stale choices.");

            await Rebuild(first);
            Select(board, Texts.T("media.filter.boardOfSource", "x", "filter-a")); // Board-only implies its own source.
            PrepareSelectionAndAsk();
            await Rebuild(third);
            await RequireClosed("filter-a", "x");
            Require(source.SelectedIndex == 0 && board.SelectedItem!.ToString() == Texts.T("media.filter.boardOfSource", "x", "filter-a")
                + " (" + Texts.T("mediahash.unavailable") + ")"
                && board.Items.Cast<object>().Any(item => item.ToString() == Texts.T("media.filter.boardOfSource", "y", "filter-a")),
                "A missing board-only choice must keep its source identity and offer current boards.");
            Select(source, "filter-a");
            await RequireClosed("filter-a", "x");
            Require(board.SelectedItem!.ToString() == "/x/ (" + Texts.T("mediahash.unavailable") + ")", "Choosing the same source must retain the missing board.");
            Select(board, "/y/");
            Require(page.CurrentFilter().Source == "filter-a" && page.CurrentFilter().Board == "y"
                && page.ShownEntries.Count == 1 && page.ShownEntries[0].Thread == ay && page.SelectedTiles.Count == 0
                && board.Items.Count == 2, "An explicit board change must show only that board and discard the retained choice.");
            await Rebuild(first);
            Select(board, "/x/");
            await Rebuild(third);
            await RequireClosed("filter-a", "x");
            page.ResetFilters();
            Require(page.CurrentFilter().IsEmpty && page.ShownEntries.Count == 1 && page.SelectedTiles.Count == 0
                && exportAll.IsEnabled && page.ActiveFilterCount == 0 && board.Items.Count == 3, "Reset must clear the missing board without selecting new results.");

            await Rebuild(first);
            Select(source, "filter-a");
            Select(board, "/x/");
            Select(source, "filter-b");
            Require(page.CurrentFilter().Source == "filter-b" && page.CurrentFilter().Board == null && page.ShownEntries.Count == 1,
                "Explicitly choosing another source must still clear an incompatible board.");
        }
        finally { page.EndLifetime(); window.Close(); Texts.SetLanguage("en"); }
    }

    private static async Task SeedFilterRefreshAsync(string profile, params ResourceId[] resources)
    {
        new Config().SaveConfig(profile);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        foreach (ResourceId resource in resources)
        {
            string directory = Path.Combine(profile, "media", resource.Thread!);
            CapturedAttachment bound = await WriteAsync(directory, resource, ("shared.png", "m" + resource.Thread, resource.Thread));
            await catalog.CaptureAsync(new CapturedThreadSnapshot(resource, "https://offline.invalid/" + resource.Thread, "Filter fixture",
                ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, directory,
                [new CapturedPost(resource.Thread!, 0, PostFieldPresence.All, Html: "op", Attachments: [bound])]));
        }
    }

    // Hold the manifest open across a restart: the old scan must cancel and dispose
    // without disposing the replacement or publishing its stale result.
    private static async Task AudioScanReplacementJourneyAsync(MainWindow owner)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("cx03-audio-replacement-");
        new Config().SaveConfig(profile);
        await SeedAudioAsync(profile);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        MediaPage seed = new(host.OpenArchiveReaderAsync);
        Window window = new() { Width = 1000, Height = 700, Content = seed, ShowInTaskbar = false };
        MediaPage? page = null;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            window.Show(owner);
            seed.IsAvailable = true;
            await seed.ActivateAsync().WaitAsync(Deadline);
            await seed.AudioScanTask.WaitAsync(Deadline);
            var snapshot = seed.Snapshot!;
            Require(snapshot.Complete && snapshot.Entries.Count == 3, "Audio replacement fixture must have three videos.");
            page = new(host.OpenArchiveReaderAsync, async (directory, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task;
                return await MediaAssetReader.OpenAsync(directory, cancellationToken);
            });
            MethodInfo start = typeof(MediaPage).GetMethod("StartAudioProbe", BindingFlags.Instance | BindingFlags.NonPublic)!;
            start.Invoke(page, [snapshot]);
            Task first = page.AudioScanTask;
            await entered.Task.WaitAsync(Deadline);
            Require(!first.IsCompleted, "The first scan must still be in its manifest boundary.");
            start.Invoke(page, [snapshot]);
            Task second = page.AudioScanTask;
            await first.WaitAsync(Deadline);
            Require(!second.IsCompleted, "Cancelling the old scan must not cancel its replacement.");
            release.TrySetResult();
            await second.WaitAsync(Deadline);
            await UntilAsync(() => page.AudioResults.Count == 3);
            var sound = snapshot.Entries.Single(entry => entry.Name == "snd-900.mp4");
            var silent = snapshot.Entries.Single(entry => entry.Name == "mute-910.mp4");
            Require(page.AudioResults.Get(sound) == MediaAudio.HasSound && page.AudioResults.Get(silent) == MediaAudio.Silent,
                "The replacement scan must publish the real current audio results.");
            // A completed replacement is safely followed by another scan and lifetime cancellation.
            start.Invoke(page, [snapshot]);
            await page.AudioScanTask.WaitAsync(Deadline);
            page.EndLifetime();
            Console.WriteLine("SMOKE CX-03 audio: completed scan restart and held old-scan cancellation/replacement passed.");
        }
        finally
        {
            release.TrySetResult();
            page?.EndLifetime();
            seed.EndLifetime();
            window.Close();
        }
    }

}
