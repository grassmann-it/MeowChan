using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Testing;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopSmoke;

// Root scenario "booru-composition": a real desktop session composes the Danbooru services (search service, original
// queue over the shared download queue, watcher host and scheduler, bounded preview path) over an in-memory Danbooru
// with the Danbooru plugin called in-process. The window is opened from the real Workspace menu of a second main window.
// No socket is opened; the stub refuses every host except danbooru.donmai.us and cdn.donmai.us.
internal static class BooruCompositionChecks
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        string originalLanguage = Texts.Language;
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        string shots = Environment.GetEnvironmentVariable("MEOWCHAN_BOORU_SHOTS") ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(shots);
        string profile = TemporaryWorkspacePaths.CreateDirectory("booru-composition-");
        string media = Path.Combine(profile, "media");
        new Config { DownloadPath = media, AutomaticUpdateChecks = false, SaveUrlsOnExit = false, FirstRunSetupVersion = 1,
            MinimumFreeSpaceGiB = 0, CloseToTray = false, MinimizeToTray = false, WorkspaceMode = "Advanced", MaxParallelDownloads = 1 }
            .SaveConfig(profile);
        Directory.CreateDirectory(media);
        using DanbooruStub stub = new();
        uint[] colors = { 0xFFD9534F, 0xFF5CB85C, 0xFF428BCA, 0xFFF0AD4E };
        for (int index = 0; index < colors.Length; index++)
            stub.Add(new DanbooruStubPost(8004 - index, Rating: index switch { 0 => "g", 1 => "s", 2 => "q", _ => "e" },
                General: "cat_ears solo", Restricted: index == 3, Color: colors[index]));
        stub.Publish("cat_ears", 8004, 8003, 8002, 8001);
        stub.HoldOriginals(); // the explicit load stays visibly queued until the end
        using DanbooruTestBinding binding = new(stub, enabled: false);
        DesktopSessionHost? host = null;
        MainWindow? shell = null;
        BooruWorkbenchWindow? window = null;
        try
        {
            Texts.SetLanguage("en");
            host = new DesktopSessionHost(profile, Array.Empty<IImageBoard>())
            {
                BooruBindingForTests = () => binding.Binding,
                BooruSchedulerOptionsForTests = new DanbooruWatcherSchedulerOptions { Interval = TimeSpan.FromMinutes(30), CheckStartSpacing = TimeSpan.Zero }
            };
            shell = new MainWindow(host);
            shell.Show(owner);
            await host.StartAsync().WaitAsync(Wait);
            await Program.WaitUntilAsync(() => shell.FindControl<Button>("AddUrlButton")!.IsEnabled);
            // The second main window applies its profile's theme preference; the light screenshots need light.
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            MenuItem menu = shell.FindControl<MenuItem>("WorkspaceMenuItem")!;

            // Plugin disabled: nothing is constructed and the entry stays hidden.
            Require(host.BooruComposition is null && host.BooruServices is null, "Danbooru was composed although the plugin is disabled.");
            await RequireEntryVisibleAsync(menu, visible: false);
            // A Danbooru address in the Add line while the plugin is disabled: the normal "no source" feedback plus the hint, no request.
            await SubmitAddLineAsync(shell, "https://danbooru.donmai.us/posts/8003?q=secret");
            Require(Find<SelectableTextBlock>(shell, "AdmissionRepairText").Text?.Contains("enable the Danbooru plugin under Plugins") == true,
                "The disabled Danbooru address lacks the enable hint: " + Find<SelectableTextBlock>(shell, "AdmissionRepairText").Text);
            Require(stub.Requests.IsEmpty && host.BooruServices is null && !OpenWindows().Any(), "A disabled Danbooru address caused a request or a window.");
            Require(shell.FindControl<TextBox>("UrlInput")!.Text == "https://danbooru.donmai.us/posts/8003?q=secret", "A refused address was cleared from the Add line.");
            shell.FindControl<TextBox>("UrlInput")!.Text = string.Empty;
            // Enabling the plugin in the running session composes the services and shows the entry.
            binding.SetEnabled(true);
            await Program.WaitUntilAsync(() => host.BooruServices != null, () => "no composition after enabling the plugin");
            Require(host.BooruComposition!.WatcherHost.Scheduler.GetSnapshot().Running, "The watcher scheduler is not running.");
            await RequireEntryVisibleAsync(menu, visible: true);
            Require(stub.Requests.IsEmpty, "Composition or the menu sent a request.");

            window = await OpenAsync(menu);
            await Program.WaitUntilAsync(() => window.ActualThemeVariant == ThemeVariant.Light, () => "light theme not applied");
            Require(stub.Requests.IsEmpty, "Opening the window sent a request.");

            // ---- search through the real adapter, worker and bounded transport ----------------------------------------------
            Find<TextBox>(window, BooruWorkbenchWindow.InputName).Text = "cat_ears";
            await Program.WaitUntilAsync(() => Find<SelectableTextBlock>(window, BooruWorkbenchWindow.QueryName).Text == "Query sent to Danbooru: cat_ears",
                () => "query line: " + Find<SelectableTextBlock>(window, BooruWorkbenchWindow.QueryName).Text);
            Click(Find<Button>(window, BooruWorkbenchWindow.SubmitName));
            await Program.WaitUntilAsync(() => Cards(window).Count == 4 && Previews(window) == 4,
                () => $"results: cards={Cards(window).Count} previews={Previews(window)} status={BooruText.SearchStatus(window.Model.Search.View, false)}");
            Require(stub.Requests.Count(url => url.StartsWith("https://danbooru.donmai.us/", StringComparison.Ordinal)) == 1
                && stub.Requests.Contains("https://danbooru.donmai.us/posts.json?tags=cat_ears&page=1&limit=20"),
                "The search was not exactly one provider request with the user's query: " + string.Join(", ", stub.Requests));
            Require(stub.PreviewRequests == 4 && stub.OriginalRequests == 0 && stub.SampleRequests == 0,
                $"Previews were not fetched through the preview path only (previews={stub.PreviewRequests}, originals={stub.OriginalRequests}, samples={stub.SampleRequests}).");
            Require(!Find<Button>(window, BooruWorkbenchWindow.LoadPrefix + "8001").IsEnabled, "A restricted post can be loaded.");
            Capture(window, shots, "composition-results-light");

            // ---- copy link through the window's own top-level clipboard (the harness fixture; opening would start a browser: covered with fakes in booru-workbench) ----
            Click(Find<Button>(window, BooruWorkbenchWindow.CopyLinkPrefix + "8004"));
            await Program.WaitUntilAsync(() => Text(window).Contains("Link copied: https://danbooru.donmai.us/posts/8004"), () => "copy note missing: " + Text(window));
            Require(Find<Button>(window, BooruWorkbenchWindow.OpenPostPrefix + "8004").IsEffectivelyEnabled, "Open post in browser is missing on the result card.");
            Require(stub.Requests.Count(url => url.StartsWith("https://danbooru.donmai.us/", StringComparison.Ordinal)) == 1, "Copying a link sent a request.");
            Capture(window, shots, "composition-link-copied-light");

            // ---- one explicit original into the shared queue ----------------------------------------------------------------
            Click(Find<Button>(window, BooruWorkbenchWindow.LoadPrefix + "8004"));
            await Program.WaitUntilAsync(() => Text(window).Contains("Added to the download queue."), () => "queued notice missing");
            Require(host.BooruComposition.Originals.ActiveCount == 1, "Load original did not admit exactly one queue job.");
            Require(!Find<Button>(window, BooruWorkbenchWindow.LoadPrefix + "8004").IsEnabled, "Load original stayed enabled after queuing.");
            BooruPostRecord queued = host.BooruComposition.Catalog.Read().Posts.Single(post => post.PostId == "8004");
            Require(queued.OriginalIntent == BooruOriginalIntent.Requested && queued.Findings.Any(finding => finding.Kind == BooruFindingKind.Search),
                "The catalog does not record the requested original with its search finding.");
            Require(queued.Findings.Single(finding => finding.Kind == BooruFindingKind.Search).Query == "cat_ears",
                "The search finding does not keep the query that found the post.");
            Require(stub.SampleRequests == 0, "A sample was requested.");
            Capture(window, shots, "composition-queued-light");

            // ---- watcher through the real scheduler -------------------------------------------------------------------------
            Click(Find<Button>(window, BooruWorkbenchWindow.CreateWatcherName));
            await Program.WaitUntilAsync(() => Text(window).Contains("Watcher created for"), () => "created notice missing");
            Require(host.BooruComposition.Originals.ActiveCount == 1, "Creating the watcher queued existing posts.");
            TabControl tabs = Find<TabControl>(window, BooruWorkbenchWindow.TabsName);
            tabs.SelectedIndex = 1;
            await Program.WaitUntilAsync(() => WatcherCards(window).Count == 1, () => "watcher card missing");
            string watcherId = host.BooruComposition.WatcherHost.Scheduler.GetSnapshot().Watchers.Single().WatcherId;
            // The scheduler's own first check sets the baseline (from now); then one explicit check.
            await Program.WaitUntilAsync(() => host.BooruComposition.WatcherHost.Scheduler.GetStatus(watcherId)?.BaselineEstablished == true
                && Find<Button>(window, BooruWorkbenchWindow.CheckPrefix + watcherId).IsEffectivelyEnabled, () => "baseline not established");
            Click(Find<Button>(window, BooruWorkbenchWindow.CheckPrefix + watcherId));
            await Program.WaitUntilAsync(() => Text(window).Contains("Check finished: no new posts."), () => "check result missing: " + Text(window));
            Require(host.BooruComposition.Originals.ActiveCount == 1, "The watcher downloaded posts that existed before it.");
            Capture(window, shots, "composition-watchers-light");

            // ---- dark theme -------------------------------------------------------------------------------------------------
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            await Program.WaitUntilAsync(() => window.ActualThemeVariant == ThemeVariant.Dark, () => "dark theme not applied");
            Capture(window, shots, "composition-watchers-dark");
            tabs.SelectedIndex = 0;
            await Program.WaitUntilAsync(() => Cards(window).Count == 4 && Previews(window) == 4, () => "dark results not shown");
            Capture(window, shots, "composition-results-dark");

            // ---- Danbooru addresses in the Add line (the shared path of typing, pasting, the clipboard monitor and dropping) --------
            int before = stub.Requests.Count;
            TextBox urlInput = shell.FindControl<TextBox>("UrlInput")!;
            // A Safebooru search address: the existing window gets the query with the visible rating:g term; nothing is requested.
            await SubmitAddLineAsync(shell, "https://safebooru.donmai.us/posts?tags=cat_ears&page=2");
            await Program.WaitUntilAsync(() => Find<TextBox>(window, BooruWorkbenchWindow.InputName).Text == "cat_ears rating:g",
                () => "search field: " + Find<TextBox>(window, BooruWorkbenchWindow.InputName).Text);
            Require(stub.Requests.Count == before && urlInput.Text == string.Empty && OpenWindows().Count() == 1,
                "A search address requested something, kept its text or opened a second window.");
            // A Danbooru address with an unencoded comma list stays one entry; the query is shown exactly as written.
            await SubmitAddLineAsync(shell, "https://danbooru.donmai.us/posts?tags=cat_ears+rating:g,s");
            await Program.WaitUntilAsync(() => Find<TextBox>(window, BooruWorkbenchWindow.InputName).Text == "cat_ears rating:g,s",
                () => "search field: " + Find<TextBox>(window, BooruWorkbenchWindow.InputName).Text);
            Require(stub.Requests.Count == before, "A comma search address requested something.");
            // A bare site address: empty field, still nothing requested.
            await SubmitAddLineAsync(shell, "https://danbooru.donmai.us/");
            await Program.WaitUntilAsync(() => Find<TextBox>(window, BooruWorkbenchWindow.InputName).Text == string.Empty, () => "field not emptied");
            Require(stub.Requests.Count == before, "A site address requested something.");
            // A Safebooru post address with a search context: one post read on Danbooru, one one-shot original in the shared queue.
            await SubmitAddLineAsync(shell, "https://safebooru.donmai.us/posts/8003?q=secret");
            await Program.WaitUntilAsync(() => AdmissionSummary(shell).StartsWith("1 added", StringComparison.Ordinal) && urlInput.Text == string.Empty,
                () => "summary: " + AdmissionSummary(shell) + " | input=" + urlInput.Text + " | requests=" + string.Join(",", stub.Requests.Skip(before)));
            Require(stub.Requests.Skip(before).Count(url => url.StartsWith("https://danbooru.donmai.us/", StringComparison.Ordinal)) == 1
                && stub.Requests.Contains("https://danbooru.donmai.us/posts/8003.json"),
                "The post address was not exactly one post read: " + string.Join(", ", stub.Requests.Skip(before)));
            Require(!stub.Requests.Any(url => url.Contains("secret", StringComparison.Ordinal)),
                "The search context of the post address leaked into a request.");
            Require(host.BooruComposition.Originals.ActiveCount == 2, "The post address did not admit exactly one more queue job.");
            Require(host.BooruComposition.Catalog.Read().Posts.Single(post => post.PostId == "8003").Findings.Any(finding => finding.Kind == BooruFindingKind.OneShot),
                "The post address is not recorded as a one-shot.");
            Require(host.BooruComposition.WatcherHost.Scheduler.GetSnapshot().Watchers.Count == 1, "An address created a watcher.");
            Capture(shell, shots, "composition-add-line-light");

            // ---- the held original completes; the row's local state follows the catalog ------------------------------------
            stub.ReleaseOriginals();
            await Program.WaitUntilAsync(() => host.BooruComposition.Catalog.Read().Posts.Where(post => post.PostId is "8004" or "8003").All(post => post.LocalAssetId != null)
                && host.BooruComposition.Catalog.Read().Posts.Count(post => post.PostId is "8004" or "8003") == 2,
                () => "the originals were not linked");
            Require(stub.OriginalRequests == 2, "An original was transferred more than once.");
            Require(stub.Violations.IsEmpty, "Stub violations: " + string.Join(", ", stub.Violations));
            Require(!Text(window).Contains("booru.ui.") && !Text(window).Contains("booru.watcher."), "A raw key is visible.");

            window.Close();
            window = null;
            int requests = stub.Requests.Count;
            await CloseAsync(shell);
            shell = null;
            await host.DisposeAsync().AsTask().WaitAsync(Wait);
            Require(host.BooruServices is null, "The Danbooru ports outlived the session.");
            host = null;
            Require(stub.Requests.Count == requests, "A request happened during shutdown.");
            Console.WriteLine("SMOKE booru composition: plugin disabled -> nothing composed, entry hidden, a Danbooru address in the Add line only shows the enable hint (no request); Add line with the plugin enabled: Safebooru search address prefills the open window with a visible rating:g, a Danbooru comma list stays one entry, a bare site address empties the field, none requests anything; a Safebooru post address with a search context sends exactly one Danbooru post read without the context and queues one one-shot original, no watcher; enabled at runtime -> composed, entry shown; window from the Workspace menu; one exact search request through the real adapter and in-process plugin; 4 result cards with 4 previews via the bounded preview path (no sample/original); restricted post not loadable; copy link through the window clipboard path; one Load original -> exactly one queue job, Requested intent with search finding that keeps the query; watcher created, scheduler baseline, Check now -> no new posts; light and dark screenshots in " + shots + "; held original completed with one transfer; orderly shutdown without requests.");
        }
        finally
        {
            window?.Close();
            if (shell != null) await CloseAsync(shell);
            if (host != null) await host.DisposeAsync().AsTask().WaitAsync(Wait);
            stub.ReleaseOriginals();
            Application.Current!.RequestedThemeVariant = originalTheme;
            Texts.SetLanguage(originalLanguage);
            owner.Activate();
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static IEnumerable<BooruWorkbenchWindow> OpenWindows() =>
        ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Windows.OfType<BooruWorkbenchWindow>().Where(candidate => candidate.IsVisible);

    // Types into the Add line and presses Add, then waits until the busy state is over.
    private static async Task SubmitAddLineAsync(MainWindow shell, string text)
    {
        shell.FindControl<TextBox>("UrlInput")!.Text = text;
        Button add = shell.FindControl<Button>("AddUrlButton")!;
        Click(add);
        await Program.WaitUntilAsync(() => !add.IsEnabled || shell.FindControl<Border>("AdmissionResultsPanel")!.IsVisible || OpenWindows().Any(), () => "Add did not start");
        await Program.WaitUntilAsync(() => add.IsEnabled, () => "Add stayed busy");
        await SettleAsync();
    }

    private static string AdmissionSummary(MainWindow shell) => shell.FindControl<TextBlock>("AdmissionResultsTitle")?.Text ?? string.Empty;

    private static MenuItem Entry(MenuItem menu) => menu.Items.OfType<MenuItem>()
        .FirstOrDefault(item => item.Header as string == MeowChan.Desktop.Commands.CommandCatalog.Label("booru.open"))
        ?? throw new InvalidOperationException("The Danbooru menu entry does not exist.");

    private static async Task RequireEntryVisibleAsync(MenuItem menu, bool visible)
    {
        MenuItem entry = Entry(menu);
        menu.IsSubMenuOpen = true;
        await SettleAsync();
        Require(entry.IsVisible == visible, "The Danbooru entry visibility is " + entry.IsVisible + ", expected " + visible + ".");
        menu.IsSubMenuOpen = false;
        await SettleAsync();
    }

    private static async Task<BooruWorkbenchWindow> OpenAsync(MenuItem menu)
    {
        MenuItem entry = Entry(menu);
        menu.IsSubMenuOpen = true;
        await SettleAsync();
        Require(entry.IsVisible && entry.IsEnabled, "The Danbooru entry is not available.");
        menu.IsSubMenuOpen = false;
        entry.Command!.Execute(null);
        IClassicDesktopStyleApplicationLifetime lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        await Program.WaitUntilAsync(() => lifetime.Windows.OfType<BooruWorkbenchWindow>().Any(candidate => candidate.IsVisible), () => "window not shown");
        BooruWorkbenchWindow window = lifetime.Windows.OfType<BooruWorkbenchWindow>().Single(candidate => candidate.IsVisible);
        await SettleAsync(window);
        return window;
    }

    private static async Task CloseAsync(MainWindow shell)
    {
        var close = typeof(MainWindow).GetMethod("CloseApplicationAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)close.Invoke(shell, null)!).WaitAsync(Wait);
        await Program.WaitUntilAsync(() => !shell.IsVisible);
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException("Control not found: " + name);

    private static List<Border> Cards(Control root) => Named(root, "BooruResult:");
    private static List<Border> WatcherCards(Control root) => Named(root, "BooruWatcher:");

    private static List<Border> Named(Control root, string prefix) => root.GetVisualDescendants().OfType<Border>()
        .Where(border => border.Name is { } name && name.StartsWith(prefix, StringComparison.Ordinal)).ToList();

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
