using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopSmoke;

// Root scenario "booru-workbench": the Danbooru window opened through its real app-menu command over
// fake ports. No provider, queue, scheduler or network exists in this run; every fake counts its calls.
internal static partial class BooruWorkbenchChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        string originalLanguage = Texts.Language;
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        string shots = Environment.GetEnvironmentVariable("MEOWCHAN_BOORU_SHOTS") ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(shots);
        BooruWorkbenchWindow? window = null;
        try
        {
            Texts.SetLanguage("en");
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            MenuItem menu = owner.FindControl<MenuItem>("WorkspaceMenuItem")!;
            await CheckMenuHiddenWithoutPortsAsync(owner, menu);

            BooruUiHarness h = new();
            h.Serve(pages: 3, perPage: 3);
            h.Watchers.Seed(
                BooruUiFixtures.Watcher("w-active", "cat blue_eyes", clock: h.Clock, hits: 6, queued: 5) with { LatestPostId = "9001" },
                BooruUiFixtures.Watcher("w-paused", "landscape", BooruWatcherStatus.Paused, h.Clock, hits: 2, queued: 2),
                BooruUiFixtures.Watcher("w-cooling", "scenery sky", BooruWatcherStatus.Cooldown, h.Clock,
                    cooldownUntil: h.Clock.GetUtcNow() + TimeSpan.FromMinutes(7), hits: 1, queued: 1),
                BooruUiFixtures.Watcher("w-error", "ruins", BooruWatcherStatus.Error, h.Clock, errorCode: "http-network"));
            owner.SessionHost.BooruServices = h.Services;

            window = await OpenAsync(owner, menu);
            Require(h.Search.Requests == 0 && h.Queue.Loads.Count == 0 && h.Watchers.Checks == 0,
                "Opening the window made a provider, queue or check request.");
            Require(h.Watchers.Calls.All(call => call == "list"), "Opening the window did more than read the watcher list.");
            Require(Text(window).Contains("Nothing is requested until you do."), "Idle state does not say that nothing is requested.");
            Require(Text(window).Contains("adds no rating filter") || Text(window).Contains("no rating filter"),
                "The no-rating-filter note is missing.");
            Require(!Text(window).Contains("18+"), "An 18+ notice appeared.");
            // M10 / M5: the window title is a level-1 heading for screen readers, and the window fits the screen it opened on.
            Require(window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Classes.Contains("page-title")
                    && AutomationProperties.GetHeadingLevel(block) == 1),
                "The window title is not a level-1 heading.");
            if (window.Screens.ScreenFromWindow(window) is { } screen)
                Require(window.Bounds.Height * window.RenderScaling <= screen.WorkingArea.Height + 1
                    && window.Bounds.Width * window.RenderScaling <= screen.WorkingArea.Width + 1,
                    "The Danbooru window is larger than the working area: " + window.Bounds + " on " + screen.WorkingArea);

            // ---- search: the query is visible exactly as sent -------------------------------------------------------
            TextBox input = Find<TextBox>(window, BooruWorkbenchWindow.InputName);
            await CheckInvalidInputAsync(window, input);
            input.Text = "cat blue_eyes";
            await Program.WaitUntilAsync(() => Find<SelectableTextBlock>(window, BooruWorkbenchWindow.QueryName).Text == "Query sent to Danbooru: cat blue_eyes",
                () => "query line: " + Find<SelectableTextBlock>(window, BooruWorkbenchWindow.QueryName).Text);
            Require(h.Search.Requests == 0, "Typing into the field sent a request.");
            Click(Find<Button>(window, BooruWorkbenchWindow.SubmitName));
            await Program.WaitUntilAsync(() => Cards(window, "BooruResult:").Count == 3 && Previews(window) == 3,
                () => "cards=" + Cards(window, "BooruResult:").Count + " previews=" + Previews(window));
            Require(h.Search.Searches.Single() == ("cat blue_eyes", 1), "The search did not send the exact query.");
            Require(!h.Search.Searches[0].Query.Contains("rating"), "A rating term was added to the query.");
            string text = Text(window);
            foreach (string expected in new[] { "Post #101", "Danbooru rating", "Danbooru score", "Original available",
                "Source tags (from Danbooru, not your own tags)", "Page 1 of at most 5" })
                Require(text.Contains(expected), "Result card lacks '" + expected + "'.");
            // I8: facts as a definition list (values named "term: value"), no address text line, tags as chips in Danbooru order.
            Grid details = Find<Grid>(window, BooruWorkbenchWindow.DetailsPrefix + "101");
            string[] detailNames = details.Children.OfType<TextBlock>().Select(block => AutomationProperties.GetName(block) ?? string.Empty).ToArray();
            Require(detailNames.Any(name => name.StartsWith("Danbooru rating: General", StringComparison.Ordinal)) && detailNames.Contains("Danbooru score: 12")
                && details.ColumnDefinitions.Count == 2,
                "The post facts are not a definition list: " + string.Join(" | ", detailNames));
            Require(!text.Contains("https://danbooru.donmai.us/posts/101"), "The post address is still shown as a text line.");
            Button[] chips101 = window.GetVisualDescendants().OfType<Button>().Where(button => button.Name?.StartsWith(BooruWorkbenchWindow.ResultTagPrefix + "101:", StringComparison.Ordinal) == true).ToArray();
            Require(chips101.Select(button => button.Name![(BooruWorkbenchWindow.ResultTagPrefix.Length + 4)..]).SequenceEqual(new[] { "sky", "cat", "blue_eyes" })
                && chips101.All(button => button.Focusable && button.IsTabStop && button.Classes.Contains("chip-source-button")
                    && button.Content is ChipOutline { IsDashed: true })
                && AutomationProperties.GetName(chips101[0]) == "Add source tag sky (Artist) to the search",
                "The source tag chips are not in Danbooru order or not named actions: " + string.Join(",", chips101.Select(button => button.Name)));
            Require(Find<Button>(window, BooruWorkbenchWindow.PreviousName).IsVisible == false
                || !Find<Button>(window, BooruWorkbenchWindow.PreviousName).IsEnabled, "Previous page enabled on page 1.");
            Capture(window, shots, "search-light");

            // ---- post link actions: canonical page only, explicit clicks, keyboard reachable --------------------------------
            Require(h.Links.Opened.Count == 0 && h.Links.Copied.Count == 0, "Showing results opened or copied a link.");
            Button openPost = Find<Button>(window, BooruWorkbenchWindow.OpenPostPrefix + "101");
            Button copyPost = Find<Button>(window, BooruWorkbenchWindow.CopyLinkPrefix + "101");
            Require(openPost.Focusable && openPost.IsTabStop && copyPost.Focusable && copyPost.IsTabStop, "The link buttons are not keyboard reachable.");
            Require(AutomationProperties.GetName(openPost) == "Open post 101 in the browser"
                && AutomationProperties.GetName(copyPost) == "Copy link of post 101", "The link buttons lack their automation names.");
            Require(openPost.Content as string == "Open post in browser" && copyPost.Content as string == "Copy link", "The link buttons lack their labels.");
            Click(openPost);
            await Program.WaitUntilAsync(() => h.Links.Opened.Count == 1, () => "open not requested");
            Require(h.Links.Opened[0] == "https://danbooru.donmai.us/posts/101", "The browser got another URL: " + h.Links.Opened[0]);
            Click(copyPost);
            await Program.WaitUntilAsync(() => h.Links.Copied.Count == 1 && Text(window).Contains("Link copied: https://danbooru.donmai.us/posts/101"),
                () => "copy note missing");
            Require(h.Links.Copied[0] == "https://danbooru.donmai.us/posts/101", "The clipboard got another text: " + h.Links.Copied[0]);
            Require(h.Search.Requests == 1 && h.Queue.Loads.Count == 0, "A link action caused a provider or queue request.");
            Capture(window, shots, "search-links-light");

            // ---- I8: a source tag chip adds its tag to the search field, never a request -------------------------------
            Click(chips101[0]); // the field already holds two tags: refused and said, the field is unchanged
            await Program.WaitUntilAsync(() => Text(window).Contains("A search takes at most 2 tags"), () => "full search note missing");
            Require(input.Text == "cat blue_eyes", "A refused chip changed the field: " + input.Text);
            input.Text = string.Empty;
            await Program.WaitUntilAsync(() => window.Model.Search.View.Input == string.Empty, () => "field not cleared");
            Click(chips101[0]);
            Click(Find<Button>(window, BooruWorkbenchWindow.ResultTagPrefix + "101:cat"));
            await Program.WaitUntilAsync(() => input.Text == "sky cat" && window.Model.Search.View.Input == "sky cat", () => "chips did not fill the field: " + input.Text);
            Click(Find<Button>(window, BooruWorkbenchWindow.ResultTagPrefix + "101:cat"));
            await Program.WaitUntilAsync(() => Text(window).Contains("cat is already in the search."), () => "duplicate note missing");
            Require(h.Search.Requests == 1, "A tag chip sent a request.");
            Capture(window, shots, "search-tag-chips-light");
            input.Text = "cat blue_eyes";
            await Program.WaitUntilAsync(() => window.Model.Search.View.Input == "cat blue_eyes", () => "field not restored");
            h.Links.OnOpen = _ => throw new InvalidOperationException("no browser");
            Click(Find<Button>(window, BooruWorkbenchWindow.OpenPostPrefix + "102"));
            await Program.WaitUntilAsync(() => Text(window).Contains("could not be opened in your browser"), () => "open failure not shown");
            h.Links.OnOpen = null;
            Click(Find<Button>(window, BooruWorkbenchWindow.OpenPostPrefix + "102"));
            await Program.WaitUntilAsync(() => !Text(window).Contains("could not be opened in your browser") && h.Links.Opened.Count == 3,
                () => "open failure note not cleared");
            // The real clipboard path (no injected copier): the window's own top-level clipboard receives exactly the canonical link.
            BooruWorkbenchServices plain = new(h.Search, h.Queue, h.Watchers, h.Previews, h.Clock);
            Require(plain.Links.Copy is null && ReferenceEquals(plain.Links, BooruLinkActions.System), "The default link actions are not the app launcher and the window clipboard.");

            // ---- paging is bounded -----------------------------------------------------------------------------------
            Click(Find<Button>(window, BooruWorkbenchWindow.NextName));
            await Program.WaitUntilAsync(() => Text(window).Contains("Post #201"), () => "page 2 not shown");
            Require(h.Search.Searches[1] == ("cat blue_eyes", 2), "Next page asked for another query/page.");
            Click(Find<Button>(window, BooruWorkbenchWindow.PreviousName));
            await Program.WaitUntilAsync(() => Text(window).Contains("Post #101"), () => "page 1 not shown again");

            // ---- load original: one explicit handover ----------------------------------------------------------------
            Click(Find<Button>(window, BooruWorkbenchWindow.LoadPrefix + "101"));
            await Program.WaitUntilAsync(() => Text(window).Contains("Added to the download queue."), () => "queued state not shown");
            Require(h.Queue.Loads.Count == 1 && h.Queue.Loads[0] == ("101", BooruLoadOrigin.Search), "Load original did not hand over exactly once.");
            Require(!Find<Button>(window, BooruWorkbenchWindow.LoadPrefix + "101").IsEnabled, "Load original stayed enabled after queuing.");
            await CheckResultAnnouncementsAsync(window);

            // ---- a non-loadable original is explained and disabled ----------------------------------------------------
            h.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true,
                BooruUiFixtures.Post("301", BooruOriginalAccess.NotOffered), BooruUiFixtures.Post("302", BooruOriginalAccess.Restricted),
                BooruUiFixtures.Post("303", rating: BooruRating.Explicit, rawRating: "e"))));
            input.Text = "scenery";
            Click(Find<Button>(window, BooruWorkbenchWindow.SubmitName));
            await Program.WaitUntilAsync(() => Cards(window, "BooruResult:").Count == 3 && Text(window).Contains("Post #303"), () => "restricted page not shown");
            Require(!Find<Button>(window, BooruWorkbenchWindow.LoadPrefix + "301").IsEnabled
                && !Find<Button>(window, BooruWorkbenchWindow.LoadPrefix + "302").IsEnabled, "A post without an original has an enabled load button.");
            Require(Text(window).Contains("Danbooru offers no original for this post") && Text(window).Contains("Original restricted by Danbooru"),
                "Original availability is not explained.");
            Require(Find<Grid>(window, BooruWorkbenchWindow.DetailsPrefix + "303").Children.OfType<TextBlock>()
                .Any(block => AutomationProperties.GetName(block)?.StartsWith("Danbooru rating: Explicit", StringComparison.Ordinal) == true), "The provider rating is not shown as provider metadata.");
            Require(h.Queue.Loads.Count == 1, "A search or a disabled button queued something.");

            // ---- states ----------------------------------------------------------------------------------------------------
            await CheckStateAsync(window, h, BooruCallStatus.Offline, "Danbooru could not be reached", null, shots, "state-offline-light");
            DateTimeOffset retry = h.Clock.GetUtcNow() + TimeSpan.FromMinutes(5);
            await CheckStateAsync(window, h, BooruCallStatus.RateLimited, "asked for a pause (HTTP 429)", retry, shots, "state-429-light");
            int requests = h.Search.Requests;
            Require(!Find<Button>(window, BooruWorkbenchWindow.SubmitName).IsEnabled, "Search stayed enabled during the 429 pause.");
            Require(h.Search.Requests == requests, "A request was made during the pause.");
            h.Clock.Advance(TimeSpan.FromMinutes(6));
            await Program.WaitUntilAsync(() => Find<Button>(window, BooruWorkbenchWindow.SubmitName).IsEnabled, () => "Search not re-enabled after the pause");
            Require(h.Search.Requests == requests, "The end of the pause started a request by itself.");
            await CheckStateAsync(window, h, BooruCallStatus.Denied, "denied access", null, shots, "state-restricted-light");
            await CheckStateAsync(window, h, BooruCallStatus.Failed, "The request failed", null, shots, "state-error-light");
            h.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true)));
            input.Text = "nothing_here";
            Click(Find<Button>(window, BooruWorkbenchWindow.SubmitName));
            await Program.WaitUntilAsync(() => Text(window).Contains("No posts found for"), () => "empty state not shown");

            // ---- one-shot by post link --------------------------------------------------------------------------------------
            h.Search.OnPost = null;
            input.Text = "https://danbooru.donmai.us/posts/4242";
            await Program.WaitUntilAsync(() => Find<SelectableTextBlock>(window, BooruWorkbenchWindow.QueryName).Text!.Contains("#4242"),
                () => "post line missing");
            Click(Find<Button>(window, BooruWorkbenchWindow.SubmitName));
            await Program.WaitUntilAsync(() => Cards(window, "BooruResult:").Count == 1 && Text(window).Contains("Post #4242"), () => "one-shot card missing");
            Require(h.Search.PostReads.Single() == "4242", "The post link was not read as a single post.");
            Require(!Find<Button>(window, BooruWorkbenchWindow.CreateWatcherName).IsEnabled, "A single post can create a watcher.");
            Click(Find<Button>(window, BooruWorkbenchWindow.LoadPrefix + "4242"));
            await Program.WaitUntilAsync(() => h.Queue.Loads.Count == 2, () => "one-shot load missing");
            Require(h.Queue.Loads[1] == ("4242", BooruLoadOrigin.OneShot), "One-shot load has the wrong origin.");

            // ---- query -> watcher -> watcher list -------------------------------------------------------------------------------
            h.Serve(pages: 2, perPage: 2);
            h.Search.Searches.Clear();
            ComboBox modeBox = Find<ComboBox>(window, BooruWorkbenchWindow.ModeName);
            string before = "combo=" + modeBox.SelectedIndex + " model=" + window.Model.Search.View.Mode;
            modeBox.SelectedIndex = 0;
            await Program.WaitUntilAsync(() => window.Model.Search.View.Mode == BooruInputMode.Tags,
                () => "mode switch failed; before: " + before + "; after combo=" + modeBox.SelectedIndex + " model=" + window.Model.Search.View.Mode);
            input.Text = "solo moon";
            await Program.WaitUntilAsync(() => Find<SelectableTextBlock>(window, BooruWorkbenchWindow.QueryName).Text == "Query sent to Danbooru: solo moon",
                () => "query line after the mode switch: " + Find<SelectableTextBlock>(window, BooruWorkbenchWindow.QueryName).Text);
            Click(Find<Button>(window, BooruWorkbenchWindow.SubmitName));
            await Program.WaitUntilAsync(() => Cards(window, "BooruResult:").Count == 2, () => "second search not shown: cards=" + Cards(window, "BooruResult:").Count + " status=" + BooruText.SearchStatus(window.Model.Search.View, false) + " model=" + window.Model.Search.View.Status + "/" + window.Model.Search.View.Mode + "/" + window.Model.Search.View.Input);
            Click(Find<Button>(window, BooruWorkbenchWindow.CreateWatcherName));
            await Program.WaitUntilAsync(() => Text(window).Contains("Watcher created for"), () => "created notice missing");
            Require(h.Watchers.Calls.Contains("create:solo moon"), "The watcher was not created for the exact query.");
            Require(h.Watchers.Checks == 0, "Creating a watcher ran a check.");
            Require(h.Queue.Loads.Count == 2, "Creating a watcher downloaded existing posts.");

            TabControl tabs = Find<TabControl>(window, BooruWorkbenchWindow.TabsName);
            tabs.SelectedIndex = 1;
            await Program.WaitUntilAsync(() => Cards(window, "BooruWatcher:").Count == 5, () => "watcher cards=" + Cards(window, "BooruWatcher:").Count);
            string watcherText = Text(window);
            foreach (string expected in new[] { "cat blue_eyes", "landscape", "scenery sky", "ruins", "Active", "Paused", "Cooling down", "Error",
                "Last check:", "Next check:", "Hits: 6 · added to the download queue: 5", "Danbooru paused requests until",
                // The app points the code seam at the booru message catalog, so the code reads as a sentence.
                Texts.T("booru.ui.watcher.error", BooruText.CodeText("http-network")), "none while paused", "Removing a watcher only stops watching" })
                Require(watcherText.Contains(expected), "Watcher list lacks '" + expected + "'.");
            Require(Find<Button>(window, BooruWorkbenchWindow.CheckPrefix + "w-paused").IsEnabled, "Check now is not available for a paused watcher.");
            Require(!Find<Button>(window, BooruWorkbenchWindow.CheckPrefix + "w-cooling").IsEnabled, "Check now is enabled during a cooldown.");
            Require(Find<Button>(window, BooruWorkbenchWindow.ResumePrefix + "w-paused").IsEnabled, "Resume is not offered for a paused watcher.");
            Capture(window, shots, "watchers-light");

            // ---- watcher card: newest seen post link, only where the watcher knows one --------------------------------------
            Require(Exists(window, BooruWorkbenchWindow.WatcherOpenPrefix + "w-active") && Exists(window, BooruWorkbenchWindow.WatcherCopyPrefix + "w-active"),
                "The watcher with a newest post lacks the link buttons.");
            Require(!Exists(window, BooruWorkbenchWindow.WatcherOpenPrefix + "w-paused"), "A watcher without a newest post offers a link.");
            Require(watcherText.Contains("Newest post seen: #9001"), "The newest post is not named.");
            Click(Find<Button>(window, BooruWorkbenchWindow.WatcherOpenPrefix + "w-active"));
            await Program.WaitUntilAsync(() => h.Links.Opened.Last() == "https://danbooru.donmai.us/posts/9001", () => "watcher open missing");
            Click(Find<Button>(window, BooruWorkbenchWindow.WatcherCopyPrefix + "w-active"));
            await Program.WaitUntilAsync(() => h.Links.Copied.Last() == "https://danbooru.donmai.us/posts/9001", () => "watcher copy missing");
            Capture(window, shots, "watchers-links-light");

            // ---- notification after an automatic check that queued originals ----------------------------------------------------
            Require(Toasts(owner).Count == 0, "A notification was visible before any check.");
            h.Watchers.RaiseCheckQueued(new BooruWatcherCheckNotice("w-active", "cat blue_eyes", 3));
            await Program.WaitUntilAsync(() => Toasts(owner).Count == 1, () => "no notification for the queued originals");
            FeedbackToastContent toast = Toasts(owner)[0];
            Require(toast.Title == "Danbooru watcher" && toast.Message == "Watcher “cat blue_eyes” found new posts. Originals added to the download queue: 3.",
                "Unexpected notification text: " + toast.Title + " / " + toast.Message);
            Capture(owner, shots, "watcher-notification-light");
            foreach (NotificationCard card in owner.GetVisualDescendants().OfType<NotificationCard>().ToArray()) card.Close();
            await Program.WaitUntilAsync(() => Toasts(owner).Count == 0, () => "notification not closed");
            h.Watchers.RaiseCheckQueued(new BooruWatcherCheckNotice("w-active", "cat blue_eyes", 0));
            await Task.Delay(200);
            Require(Toasts(owner).Count == 0, "A check that queued nothing was announced.");

            // a paused watcher: one explicit check, it stays paused
            int pausedChecks = h.Watchers.Checks;
            Click(Find<Button>(window, BooruWorkbenchWindow.CheckPrefix + "w-paused"));
            await Program.WaitUntilAsync(() => h.Watchers.Checks == pausedChecks + 1 && Text(window).Contains("One-time check finished: 2 new posts")
                && Text(window).Contains("The watcher is still paused."), () => "one-time check result missing");
            Require(Exists(window, BooruWorkbenchWindow.ResumePrefix + "w-paused") && !Exists(window, BooruWorkbenchWindow.PausePrefix + "w-paused"),
                "The paused watcher did not stay paused after the one-time check.");
            Require(h.Watchers.Calls.Count(call => call == "check:w-paused") == 1 && !h.Watchers.Calls.Contains("resume:w-paused"),
                "The one-time check was not exactly one request without a resume.");

            // check now / pause / resume
            int checks = h.Watchers.Checks;
            Click(Find<Button>(window, BooruWorkbenchWindow.CheckPrefix + "w-active"));
            await Program.WaitUntilAsync(() => h.Watchers.Checks == checks + 1 && Text(window).Contains("Check finished: 2 new posts"), () => "check result missing");
            await Program.WaitUntilAsync(() => Find<TextBlock>(window, BooruWorkbenchWindow.WatcherNoticeName).Text?.StartsWith("cat blue_eyes: Check finished: 2 new posts", StringComparison.Ordinal) == true
                    && Find<TextBlock>(window, BooruWorkbenchWindow.WatcherNoticeName).IsEffectivelyVisible,
                () => "watcher notice line does not repeat the check result: " + Find<TextBlock>(window, BooruWorkbenchWindow.WatcherNoticeName).Text);
            Require(AutomationProperties.GetLiveSetting(Find<TextBlock>(window, BooruWorkbenchWindow.WatcherNoticeName)) == AutomationLiveSetting.Polite,
                "The watcher notice line is not a polite live region.");
            RequireSilentCards(window, "BooruWatcher:");
            Click(Find<Button>(window, BooruWorkbenchWindow.PausePrefix + "w-active"));
            await Program.WaitUntilAsync(() => Exists(window, BooruWorkbenchWindow.ResumePrefix + "w-active")
                && !Exists(window, BooruWorkbenchWindow.PausePrefix + "w-active"), () => "pause not reflected");
            Require(h.Watchers.Checks == checks + 1, "Pausing ran a check.");
            Click(Find<Button>(window, BooruWorkbenchWindow.ResumePrefix + "w-active"));
            await Program.WaitUntilAsync(() => Exists(window, BooruWorkbenchWindow.PausePrefix + "w-active")
                && Find<Button>(window, BooruWorkbenchWindow.CheckPrefix + "w-active").IsEnabled, () => "resume not reflected");
            Require(h.Watchers.Checks == checks + 1, "Resuming ran a check.");

            // removal: keyboard focus on the safe answer, Escape answers the question, focus kept after removal
            await CheckRemovalKeyboardAsync(window, h);

            // removal: inline confirmation, notice about local media, nothing deleted
            Click(Find<Button>(window, BooruWorkbenchWindow.RemovePrefix + "w-error"));
            await Program.WaitUntilAsync(() => Text(window).Contains("Downloaded files, source tags and your own tags are not deleted."),
                () => "removal confirmation missing");
            Require(!h.Watchers.Calls.Contains("remove:w-error"), "Opening the confirmation removed the watcher.");
            Click(Find<Button>(window, BooruWorkbenchWindow.RemoveCancelPrefix + "w-error"));
            await Program.WaitUntilAsync(() => !Text(window).Contains("Remove the watcher for"), () => "confirmation not closed");
            Require(!h.Watchers.Calls.Contains("remove:w-error"), "Cancelling removed the watcher.");
            Click(Find<Button>(window, BooruWorkbenchWindow.RemovePrefix + "w-error"));
            await Program.WaitUntilAsync(() => Exists(window, BooruWorkbenchWindow.RemoveConfirmPrefix + "w-error"), () => "confirm button missing");
            Click(Find<Button>(window, BooruWorkbenchWindow.RemoveConfirmPrefix + "w-error"));
            await Program.WaitUntilAsync(() => Cards(window, "BooruWatcher:").Count == 4 && Text(window).Contains("Watcher removed. Downloaded files and tags were kept."),
                () => "removal result missing");
            await Program.WaitUntilAsync(() => Focused(window) is Button focusedAfter && Cards(window, "BooruWatcher:").Any(card => card.IsVisualAncestorOf(focusedAfter)),
                () => "focus after removal: " + Describe(Focused(window)));
            Require(h.Queue.Loads.Count == 2, "Removing a watcher touched the download side.");
            int callsAfter = h.Watchers.Calls.Count;
            await Task.Delay(300);
            Require(h.Watchers.Calls.Count == callsAfter, "The watcher port received a hidden call after the commands.");

            // ---- language and theme --------------------------------------------------------------------------------------------
            tabs.SelectedIndex = 0;
            foreach (string language in new[] { "de", "es", "fr", "ru", "en" })
            {
                Texts.SetLanguage(language);
                string expectedTitle = Texts.T("booru.ui.watcher.create");
                await Program.WaitUntilAsync(() => Find<Button>(window, BooruWorkbenchWindow.CreateWatcherName).Content as string == expectedTitle,
                    () => "language " + language + " not applied");
                Require(!Text(window).Contains("booru.ui."), "A raw key is visible in " + language + ".");
            }
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            await Program.WaitUntilAsync(() => window.ActualThemeVariant == ThemeVariant.Dark, () => "dark theme not applied");
            h.Serve(pages: 3, perPage: 3);
            input.Text = "cat blue_eyes";
            Click(Find<Button>(window, BooruWorkbenchWindow.SubmitName));
            await Program.WaitUntilAsync(() => Cards(window, "BooruResult:").Count == 3 && Previews(window) == 3, () => "dark search not shown");
            Capture(window, shots, "search-dark");
            tabs.SelectedIndex = 1;
            await Program.WaitUntilAsync(() => Cards(window, "BooruWatcher:").Count == 4, () => "dark watchers not shown");
            Capture(window, shots, "watchers-dark");

            // ---- closing stops everything ------------------------------------------------------------------------------------
            window.Close();
            window = null;
            await Task.Delay(100);
            int total = h.ProviderRequests;
            h.Clock.Advance(TimeSpan.FromHours(3));
            await Task.Delay(300);
            Require(h.ProviderRequests == total, "A request happened after the window was closed.");
            Console.WriteLine("SMOKE booru workbench accessibility: invalid input explained under the field and on the disabled Search; load and watcher results repeated once in the persistent polite notice lines, rebuilt cards silent and unnamed; URL line keeps its text; removal question focuses Cancel first, Escape cancels it (window stays), focus returns to Remove and stays in the list after removal.");
            Console.WriteLine("SMOKE booru workbench: menu hidden without ports and shown with fakes; no request on open; exact query visible; 3 result cards with previews, provider rating and original availability; bounded paging; one explicit load; offline, 429 (disabled and not retried), restricted, error and empty states; post link one-shot; canonical post link open/copy on result and watcher cards with failure note; watcher notification for queued originals (none for zero); watcher create/check/pause/resume/remove with confirmation and local-media notice; EN/DE/ES/FR/RU live; light and dark screenshots in " + shots);
        }
        finally
        {
            window?.Close();
            owner.SessionHost.BooruServices = null;
            Application.Current!.RequestedThemeVariant = originalTheme;
            Texts.SetLanguage(originalLanguage);
            await SettleAsync();
        }
    }

    private static async Task CheckMenuHiddenWithoutPortsAsync(MainWindow owner, MenuItem menu)
    {
        if (owner.SessionHost.BooruComposition is not null)
        {
            // Danbooru is delivered and enabled in this app: the composed ports are real, so the entry is visible.
            // The hidden-entry case is covered by the booru-composition scenario (plugin disabled).
            Console.WriteLine("SMOKE booru workbench: Danbooru is composed in this app; hidden-entry check left to booru-composition.");
            return;
        }
        Require(owner.SessionHost.BooruServices is null, "Ports are connected in the plain app.");
        MenuItem entry = Entry(menu);
        menu.IsSubMenuOpen = true;
        await SettleAsync();
        Require(!entry.IsVisible, "The Danbooru entry is visible although no ports are connected.");
        menu.IsSubMenuOpen = false;
        await SettleAsync();
    }

    private static MenuItem Entry(MenuItem menu) => menu.Items.OfType<MenuItem>()
        .FirstOrDefault(item => item.Header as string == CommandLabel()) ?? throw new InvalidOperationException("The Danbooru menu entry does not exist.");

    private static string CommandLabel() => MeowChan.Desktop.Commands.CommandCatalog.Label("booru.open");

    private static async Task<BooruWorkbenchWindow> OpenAsync(MainWindow owner, MenuItem menu)
    {
        MenuItem entry = Entry(menu);
        menu.IsSubMenuOpen = true;
        await SettleAsync();
        Require(entry.IsVisible && entry.IsEnabled, "The Danbooru entry is not available although ports are connected.");
        menu.IsSubMenuOpen = false;
        entry.Command!.Execute(null);
        IClassicDesktopStyleApplicationLifetime lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        await Program.WaitUntilAsync(() => lifetime.Windows.OfType<BooruWorkbenchWindow>().Any(candidate => candidate.IsVisible), () => "window not shown");
        BooruWorkbenchWindow window = lifetime.Windows.OfType<BooruWorkbenchWindow>().Single();
        await SettleAsync(window);
        entry.Command.Execute(null);
        await SettleAsync(window);
        Require(lifetime.Windows.OfType<BooruWorkbenchWindow>().Count() == 1, "The command opened a second window.");
        return window;
    }

    private static async Task CheckStateAsync(BooruWorkbenchWindow window, BooruUiHarness h, BooruCallStatus status, string expected,
        DateTimeOffset? retryAt, string shots, string shot)
    {
        TextBox input = Find<TextBox>(window, BooruWorkbenchWindow.InputName);
        h.Search.OnSearch = (_, _, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Of(status, "x", retryAt, status == BooruCallStatus.RateLimited ? 429 : null));
        input.Text = "state " + shot.Length;
        Click(Find<Button>(window, BooruWorkbenchWindow.SubmitName));
        await Program.WaitUntilAsync(() => Text(window).Contains(expected), () => "state '" + expected + "' not shown: " + BooruText.SearchStatus(window.Model.Search.View, false));
        Require(Cards(window, "BooruResult:").Count == 0, "Result cards stayed visible in the " + status + " state.");
        Capture(window, shots, shot);
    }

    // ---- accessibility (1.1.0 audit H1-H3, M2, M3) ---------------------------------------------------------------------------

    private static async Task CheckInvalidInputAsync(BooruWorkbenchWindow window, TextBox input)
    {
        TextBlock issue = Find<TextBlock>(window, BooruWorkbenchWindow.InputIssueName);
        Button submit = Find<Button>(window, BooruWorkbenchWindow.SubmitName);
        Require(!issue.IsVisible, "An input issue is visible for an empty field.");
        input.Text = "cat blue_eyes red_hair";
        await Program.WaitUntilAsync(() => window.Model.Search.View.Parsed.Kind == BooruInputKind.Invalid && issue.IsEffectivelyVisible,
            () => "invalid input not explained under the field: " + issue.Text);
        string expected = BooruText.Issue(window.Model.Search.View.Parsed.Issue);
        Require(issue.Text == expected && issue.Classes.Contains("workspace-error"), "The visible input issue is wrong: " + issue.Text);
        Require(AutomationProperties.GetLiveSetting(issue) == AutomationLiveSetting.Polite, "The input issue is not a polite live region.");
        Require(!submit.IsEnabled && AutomationProperties.GetHelpText(submit) == expected && ToolTip.GetTip(submit) as string == expected
            && ToolTip.GetShowOnDisabled(submit), "The disabled Search does not name its reason.");
        Require(AutomationProperties.GetHelpText(input) == expected, "The field lost its issue help text.");
        input.Text = string.Empty;
        await Program.WaitUntilAsync(() => !issue.IsVisible && string.IsNullOrEmpty(AutomationProperties.GetHelpText(submit)),
            () => "the input issue stayed after clearing the field");
    }

    private static async Task CheckResultAnnouncementsAsync(BooruWorkbenchWindow window)
    {
        TextBlock notice = Find<TextBlock>(window, BooruWorkbenchWindow.NoticeName);
        await Program.WaitUntilAsync(() => notice.Text == "Post #101: Added to the download queue." && notice.IsEffectivelyVisible,
            () => "the search notice line does not repeat the load result: " + notice.Text);
        Require(AutomationProperties.GetLiveSetting(notice) == AutomationLiveSetting.Polite, "The search notice line is not a polite live region.");
        RequireSilentCards(window, "BooruResult:");
        Control card = Cards(window, "BooruResult:").First(control => control.Name == "BooruResult:101");
        Require(card.GetVisualDescendants().OfType<Control>().Count(control => AutomationProperties.GetName(control) == "Post #101") == 0
            && card.GetVisualDescendants().OfType<TextBlock>().Count(text => text.Text == "Post #101") == 1,
            "\"Post #101\" is named more than once in its card.");
        // I8: the address is not a text line any more; it is the tooltip of the copy button, and the open button names the post.
        Require(!card.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.Text == "https://danbooru.donmai.us/posts/101"),
            "The post address is still a text line in its card.");
        Button copy = card.GetVisualDescendants().OfType<Button>().Single(button => button.Name == BooruWorkbenchWindow.CopyLinkPrefix + "101");
        Require(ToolTip.GetTip(copy) as string == "https://danbooru.donmai.us/posts/101" && !string.IsNullOrEmpty(AutomationProperties.GetName(copy)),
            "The copy button lacks the address as its tooltip or a name.");
    }

    private static void RequireSilentCards(Window window, string prefix)
    {
        // Rebuilt card lines never announce (their peers are new); the persistent notice lines do it once instead.
        foreach (Control card in Cards(window, prefix))
        {
            Require(string.IsNullOrEmpty(AutomationProperties.GetName(card)), "Card " + card.Name + " repeats its title as a name.");
            Require(card.GetVisualDescendants().OfType<Control>().All(control => AutomationProperties.GetLiveSetting(control) == AutomationLiveSetting.Off),
                "Card " + card.Name + " carries its own live region.");
        }
    }

    private static async Task CheckRemovalKeyboardAsync(BooruWorkbenchWindow window, BooruUiHarness h)
    {
        Button remove = Find<Button>(window, BooruWorkbenchWindow.RemovePrefix + "w-error");
        remove.Focus(NavigationMethod.Tab);
        await Program.WaitUntilAsync(() => ReferenceEquals(Focused(window), remove), () => "Remove not focused: " + Describe(Focused(window)));
        Click(remove);
        await Program.WaitUntilAsync(() => Focused(window) is Button { Name: { } name } && name == BooruWorkbenchWindow.RemoveCancelPrefix + "w-error",
            () => "the removal question did not focus Cancel: " + Describe(Focused(window)));
        Button cancel = Find<Button>(window, BooruWorkbenchWindow.RemoveCancelPrefix + "w-error");
        Button confirm = Find<Button>(window, BooruWorkbenchWindow.RemoveConfirmPrefix + "w-error");
        Require(cancel.Parent is Panel answers && answers.Children.IndexOf(cancel) < answers.Children.IndexOf(confirm),
            "The destructive answer comes before Cancel.");
        cancel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        await Program.WaitUntilAsync(() => !Exists(window, BooruWorkbenchWindow.RemoveConfirmPrefix + "w-error")
                && Focused(window) is Button { Name: { } back } && back == BooruWorkbenchWindow.RemovePrefix + "w-error",
            () => "Escape did not answer the question with Cancel: " + Describe(Focused(window)));
        Require(window.IsVisible && !h.Watchers.Calls.Contains("remove:w-error"), "Escape closed the window or removed the watcher.");
    }

    private static object? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    private static string Describe(object? focused) => focused is Control { Name: { } name } ? focused.GetType().Name + ":" + name : focused?.GetType().Name ?? "nothing";

    // ---- helpers ------------------------------------------------------------------------------------------------------------

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException("Control not found: " + name);

    private static List<FeedbackToastContent> Toasts(Window window) => window.GetVisualDescendants().OfType<NotificationCard>()
        .Where(card => !card.IsClosing && !card.IsClosed).Select(card => card.Content).OfType<FeedbackToastContent>().ToList();

    private static bool Exists(Control root, string name) => root.GetVisualDescendants().OfType<Control>().Any(control => control.Name == name);

    private static List<Control> Cards(Control root, string prefix) => root.GetVisualDescendants().OfType<Border>()
        .Where(border => border.Name is { } name && name.StartsWith(prefix, StringComparison.Ordinal)).Cast<Control>().ToList();

    private static int Previews(Control root) => root.GetVisualDescendants().OfType<Image>().Count(image => image.Source is Bitmap);

    private static string Text(Control root) => string.Join('\n', root.GetVisualDescendants().OfType<Control>().Where(control => control.IsVisible)
        .Select(control => control switch
        {
            TextBlock block => block.Text,
            ContentControl { Content: string content } => content,
            _ => null
        }).Where(text => !string.IsNullOrEmpty(text)));

    private static void Click(Button button)
    {
        Require(button.IsVisible && button.IsEffectivelyEnabled, "Button is not clickable: " + button.Name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Capture(Window window, string directory, string name)
    {
        window.UpdateLayout();
        double scale = window.RenderScaling;
        PixelSize size = new((int)Math.Ceiling(window.Bounds.Width * scale), (int)Math.Ceiling(window.Bounds.Height * scale));
        using RenderTargetBitmap bitmap = new(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        string path = Path.Combine(directory, "booru-" + name + ".png");
        bitmap.Save(path);
        Console.WriteLine("SMOKE booru screenshot: " + path + " (" + size + ")");
    }

    private static async Task SettleAsync(Window? window = null)
    {
        await Dispatcher.UIThread.InvokeAsync(() => window?.UpdateLayout(), DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
