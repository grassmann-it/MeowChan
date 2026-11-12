using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopSmoke;

// Root scenario "booru-workbench": the Danbooru window opened through its real app-menu command over
// fake ports. No provider, queue, scheduler or network exists in this run; every fake counts its calls.
internal static class BooruWorkbenchChecks
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
                BooruUiFixtures.Watcher("w-active", "cat blue_eyes", clock: h.Clock, hits: 6, queued: 5),
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

            // ---- search: the query is visible exactly as sent -------------------------------------------------------
            TextBox input = Find<TextBox>(window, BooruWorkbenchWindow.InputName);
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
            foreach (string expected in new[] { "Post #101", "https://danbooru.donmai.us/posts/101", "Danbooru rating: General",
                "Danbooru score: 12", "Original available", "Source tags (from Danbooru, not your own tags)", "Page 1 of at most 5" })
                Require(text.Contains(expected), "Result card lacks '" + expected + "'.");
            Require(Find<Button>(window, BooruWorkbenchWindow.PreviousName).IsVisible == false
                || !Find<Button>(window, BooruWorkbenchWindow.PreviousName).IsEnabled, "Previous page enabled on page 1.");
            Capture(window, shots, "search-light");

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
            Require(Text(window).Contains("Danbooru rating: Explicit"), "The provider rating is not shown as provider metadata.");
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
                "The last check failed (http-network)", "none while paused", "Removing a watcher only stops watching" })
                Require(watcherText.Contains(expected), "Watcher list lacks '" + expected + "'.");
            Require(!Find<Button>(window, BooruWorkbenchWindow.CheckPrefix + "w-paused").IsEnabled, "Check now is enabled for a paused watcher.");
            Require(!Find<Button>(window, BooruWorkbenchWindow.CheckPrefix + "w-cooling").IsEnabled, "Check now is enabled during a cooldown.");
            Require(Find<Button>(window, BooruWorkbenchWindow.ResumePrefix + "w-paused").IsEnabled, "Resume is not offered for a paused watcher.");
            Capture(window, shots, "watchers-light");

            // check now / pause / resume
            int checks = h.Watchers.Checks;
            Click(Find<Button>(window, BooruWorkbenchWindow.CheckPrefix + "w-active"));
            await Program.WaitUntilAsync(() => h.Watchers.Checks == checks + 1 && Text(window).Contains("Check finished: 2 new posts"), () => "check result missing");
            Click(Find<Button>(window, BooruWorkbenchWindow.PausePrefix + "w-active"));
            await Program.WaitUntilAsync(() => Exists(window, BooruWorkbenchWindow.ResumePrefix + "w-active")
                && !Find<Button>(window, BooruWorkbenchWindow.CheckPrefix + "w-active").IsEnabled, () => "pause not reflected");
            Require(h.Watchers.Checks == checks + 1, "Pausing ran a check.");
            Click(Find<Button>(window, BooruWorkbenchWindow.ResumePrefix + "w-active"));
            await Program.WaitUntilAsync(() => Exists(window, BooruWorkbenchWindow.PausePrefix + "w-active")
                && Find<Button>(window, BooruWorkbenchWindow.CheckPrefix + "w-active").IsEnabled, () => "resume not reflected");
            Require(h.Watchers.Checks == checks + 1, "Resuming ran a check.");

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
            Console.WriteLine("SMOKE booru workbench: menu hidden without ports and shown with fakes; no request on open; exact query visible; 3 result cards with previews, provider rating and original availability; bounded paging; one explicit load; offline, 429 (disabled and not retried), restricted, error and empty states; post link one-shot; watcher create/check/pause/resume/remove with confirmation and local-media notice; EN/DE/ES/FR/RU live; light and dark screenshots in " + shots);
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

    // ---- helpers ------------------------------------------------------------------------------------------------------------

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException("Control not found: " + name);

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
