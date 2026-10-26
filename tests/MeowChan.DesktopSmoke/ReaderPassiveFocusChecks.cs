using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

internal static class ReaderPassiveFocusChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task RunAsync(MainWindow owner, ArchivePage page, ResourceId id)
    {
        string language = Texts.Language, layout = page.ArchiveLayoutPreference;
        double width = owner.Width, height = owner.Height;
        WindowState state = owner.WindowState;
        try
        {
            // Exercise the actual shell's archive split, including its real search and toolbar.
            owner.WindowState = WindowState.Normal;
            owner.Width = 1320; owner.Height = 900;
            page.ArchiveLayoutPreference = "List";
            owner.Activate();
            await Program.WaitUntilAsync(() => owner.IsActive && page.Bounds.Width >= 1000);
            await page.LoadThreadAsync(id);
            await page.MediaLoadTask;
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            TextBox search = page.FindControl<TextBox>("ArchiveSearchText")!;
            Button header = page.FindControl<Button>("ArchiveOpenGallery")!;
            string postId = page.CurrentThread!.GetPosts().First().PostId;
            Require(search.IsEffectivelyVisible && header.IsEffectivelyVisible,
                "Passive-focus cases require the actual visible archive search and reader toolbar.");

            async Task LocalizationCase(string name, Control? newer, bool headerOrigin = false, bool missing = false)
            {
                await page.FocusPostAsync(postId);
                await SettleAsync();
                Control original = PostContainer(posts, postId);
                Require(original.IsKeyboardFocusWithin, name + ": cannot establish post focus.");
                if (headerOrigin) Require(header.Focus() && header.IsFocused, name + ": cannot focus surviving header.");
                double before = original.TranslatePoint(default, posts)!.Value.Y;
                TaskCompletionSource entered = Signal(), release = Signal();
                page.BeforePassiveReaderFocusAsync = () => { entered.TrySetResult(); return release.Task; };
                Task operation = Task.CompletedTask;
                int latePostFocus = 0;
                void Focused(object? sender, FocusChangedEventArgs e)
                {
                    if (e.Source is Visual target && target.GetVisualAncestors().Contains(posts)) latePostFocus++;
                }
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        operation = missing ? MissingAnchorAsync(page, id, postId) : Localize(page);
                        Require(!operation.IsCompleted, name + ": passive restoration did not reach the held boundary.");
                        Require(!original.IsAttachedToVisualTree(), name + ": original post container survived rendering.");
                        if (newer != null)
                            Require(newer.Focus() && newer.IsKeyboardFocusWithin, name + ": newer target refused focus.");
                        else if (!headerOrigin)
                        {
                            // Clearing the removed container's stale focus is permitted; this raises no new GotFocus.
                            owner.FocusManager!.Focus(null);
                            Require(owner.FocusManager.GetFocusedElement() == null, name + ": removed focus did not clear.");
                        }
                        // Start observation only after intentional focus changes in this same UI action.
                        owner.AddHandler(InputElement.GotFocusEvent, Focused, RoutingStrategies.Bubble, handledEventsToo: true);
                    });
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    release.TrySetResult();
                    await operation.WaitAsync(TimeSpan.FromSeconds(10));
                    await SettleAsync();
                    Control replacement = PostContainer(posts, postId);
                    if (newer != null || headerOrigin)
                    {
                        Control expected = newer ?? header;
                        Require(expected.IsKeyboardFocusWithin && latePostFocus == 0,
                            name + ": passive restoration stole newer/surviving focus. " + Describe(owner));
                    }
                    else
                    {
                        Require(!ReferenceEquals(original, replacement) && replacement.IsKeyboardFocusWithin,
                            name + ": replacement post did not regain focus. " + Describe(owner));
                        Require((posts.SelectedItem as ArchivePostRow)?.Id == postId
                            && Math.Abs(replacement.TranslatePoint(default, posts)!.Value.Y - before) <= 2,
                            name + ": localization lost the equivalent anchor or selection.");
                    }
                }
                finally
                {
                    release.TrySetResult();
                    await operation.WaitAsync(TimeSpan.FromSeconds(10));
                    owner.RemoveHandler(InputElement.GotFocusEvent, Focused);
                    page.BeforePassiveReaderFocusAsync = null;
                }
            }

            await LocalizationCase("new-external-search", search);
            await LocalizationCase("new-internal-toolbar", header);
            await LocalizationCase("surviving-toolbar-origin", null, headerOrigin: true);
            await LocalizationCase("replace-own-post", null);
            await LocalizationCase("missing-anchor-new-search", search, missing: true);
            await LocalizationCase("missing-anchor-replace-own-post", null, missing: true);

            foreach (string cover in new[] { "gallery", "owned-window", "coverage-roundtrip" })
            {
                await page.FocusPostAsync(postId);
                Require(PostContainer(posts, postId).IsKeyboardFocusWithin, cover + ": cannot establish reader focus.");
                TaskCompletionSource entered = Signal(), release = Signal();
                page.BeforePassiveReaderFocusAsync = () => { entered.TrySetResult(); return release.Task; };
                Task restoration = Localize(page);
                Window? child = null;
                bool? galleryContext = null;
                Control? expected = null;
                int latePostFocus = 0;
                void Focused(object? sender, FocusChangedEventArgs e)
                {
                    if (e.Source is Visual target && target.GetVisualAncestors().Contains(posts)) latePostFocus++;
                }
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    if (cover == "gallery")
                    {
                        await owner.OpenGalleryAsync(id, origin: posts);
                        ContentControl host = owner.FindControl<ContentControl>("GalleryHost")!;
                        await Program.WaitUntilAsync(() => host.IsVisible);
                        GalleryViewer viewer = (GalleryViewer)host.Content!;
                        galleryContext = viewer.CaptureState().ContextOpen;
                        if (!viewer.CaptureState().ContextOpen) Click(GalleryButton(viewer, "GalleryToggleContext"));
                        await Program.WaitUntilAsync(() => viewer.GetVisualDescendants().OfType<Button>()
                            .Any(button => button.Name == "GalleryReadPost" && button.IsEffectivelyVisible));
                        expected = GalleryButton(viewer, "GalleryReadPost");
                    }
                    else if (cover == "owned-window")
                    {
                        TextBox input = new() { Text = "new owned-window focus" };
                        child = new() { Width = 480, Height = 240, Content = input, Title = "PF05 reader focus owner" };
                        child.Show(owner); child.Activate();
                        await Program.WaitUntilAsync(() => child.IsActive);
                        expected = input;
                    }
                    else
                    {
                        // A transient cover must revoke permission even when no new target is focused.
                        page.SetObscuredByGallery(true); page.SetObscuredByGallery(false);
                    }
                    if (expected != null) Require(expected.Focus() && expected.IsKeyboardFocusWithin,
                        cover + ": cannot establish covering focus.");
                    owner.AddHandler(InputElement.GotFocusEvent, Focused, RoutingStrategies.Bubble, handledEventsToo: true);
                    release.TrySetResult();
                    await restoration.WaitAsync(TimeSpan.FromSeconds(10));
                    await SettleAsync();
                    Require(latePostFocus == 0 && (expected == null || expected.IsKeyboardFocusWithin),
                        cover + ": held restoration focused the underlying reader. " + Describe(owner));
                }
                finally
                {
                    release.TrySetResult();
                    await restoration.WaitAsync(TimeSpan.FromSeconds(10));
                    owner.RemoveHandler(InputElement.GotFocusEvent, Focused);
                    page.BeforePassiveReaderFocusAsync = null;
                    if (cover == "gallery" && owner.FindControl<ContentControl>("GalleryHost")!.Content is GalleryViewer viewer)
                    {
                        if (galleryContext.HasValue && viewer.CaptureState().ContextOpen != galleryContext.Value)
                            Click(GalleryButton(viewer, "GalleryToggleContext"));
                        Click(GalleryButton(viewer, "GalleryClose"));
                    }
                    child?.Close();
                    owner.Activate();
                    await Program.WaitUntilAsync(() => owner.IsActive && !page.IsObscuredByGallery);
                    await SettleAsync();
                }
            }
            await CheckLiveAsync(owner);
            Console.WriteLine("SMOKE passive reader focus: PASS; actual-shell same-action external/internal intent; surviving toolbar; replacement anchor; missing-anchor fallback; held gallery/owned window; coverage roundtrip; whole live reload.");
        }
        finally
        {
            page.BeforePassiveReaderFocusAsync = null;
            Texts.SetLanguage(language);
            await page.ReaderLocalizationTask;
            page.ArchiveLayoutPreference = layout;
            owner.Width = width; owner.Height = height; owner.WindowState = state;
            owner.Activate();
            await Program.WaitUntilAsync(() => owner.IsActive);
            await SettleAsync();
        }
    }

    private static Task Localize(ArchivePage page)
    {
        Texts.SetLanguage(Texts.Language == "de" ? "en" : "de");
        return page.ReaderLocalizationTask;
    }

    private static async Task MissingAnchorAsync(ArchivePage page, ResourceId id, string selected)
    {
        // Invoke the existing private fallback with the same operation guard; no synthetic focus logic.
        Type intentType = typeof(ArchivePage).GetNestedType("ReaderFocusIntent", BindingFlags.NonPublic)!;
        using IDisposable intent = (IDisposable)Activator.CreateInstance(intentType, Private, null, [page, false], null)!;
        Type anchorType = typeof(ArchivePage).GetNestedType("ReaderAnchor", BindingFlags.NonPublic)!;
        object anchor = Activator.CreateInstance(anchorType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [id, "__pf05_missing_post__", 0d, selected], null)!;
        typeof(ArchivePage).GetMethod("RenderThread", Private)!.Invoke(page, null);
        await (Task)typeof(ArchivePage).GetMethod("RestoreReaderAnchorAsync", Private)!
            .Invoke(page, [anchor, true, intent])!;
    }

    private static async Task CheckLiveAsync(MainWindow owner)
    {
        string directory = TemporaryWorkspacePaths.CreateDirectory("pf05-passive-live-");
        ArchivePage? page = null;
        Window? host = null;
        TaskCompletionSource? release = null;
        Task update = Task.CompletedTask;
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(directory);
            await using MeowChan.Archive.ArchiveCatalog catalog = await MeowChan.Archive.ArchiveCatalog.OpenAsync(directory, lease);
            string media = Path.Combine(directory, "media"); Directory.CreateDirectory(media);
            ResourceId id = new("fixture", "focus", "1000");
            const string url = "https://focus.fixture.invalid/thread/1000";
            CapturedThreadSnapshot Snapshot(int count) => new(id, url, "Passive live focus", ThreadSourceState.Active,
                DateTimeOffset.UtcNow, media, Enumerable.Range(1000, count).Select((number, index) =>
                    new CapturedPost(number.ToString(), index, PostFieldPresence.All, Html: "Saved post " + number)).ToArray());
            await catalog.CaptureAsync(Snapshot(2));
            bool holdReload = false;
            TaskCompletionSource entered = Signal();
            page = new(async token =>
            {
                if (holdReload && page!.IsBusy)
                {
                    holdReload = false; entered.TrySetResult(); await release!.Task.WaitAsync(token);
                }
                return await ArchiveReader.OpenAsync(directory, token);
            }, (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true, CanFollowLive = _ => true };
            // Own the polling clock so the awaited check is the complete reload operation.
            ((DispatcherTimer)typeof(ArchivePage).GetField("_readerUpdateTimer", Private)!.GetValue(page)!).Stop();
            TextBox external = new() { Text = "new external live-reload focus" };
            Grid surface = new() { RowDefinitions = new RowDefinitions("Auto,*") };
            Control pane = page.DetachReader(); Grid.SetRow(pane, 1);
            surface.Children.Add(external); surface.Children.Add(pane);
            host = new() { Width = 1100, Height = 800, Content = surface, Title = "PF05 passive live reader" };
            host.Show(owner); host.Activate();
            await Program.WaitUntilAsync(() => host.IsActive);
            await page.LoadThreadAsync(id);
            await page.MediaLoadTask;
            CheckBox follow = page.FindControl<CheckBox>("ReaderFollowLive")!;
            follow.IsChecked = true;
            await Program.WaitUntilAsync(() => !(bool)typeof(ArchivePage).GetField("_checkingReaderUpdate", Private)!.GetValue(page)!);
            Require(follow.IsChecked == true, "Live focus fixture did not enable real following.");
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            int count = 2;
            foreach (string scenario in new[] { "external-during-load", "coverage-during-anchor", "no-new-intent" })
            {
                await page.FocusPostAsync("1000");
                Require(PostContainer(posts, "1000").IsKeyboardFocusWithin, scenario + ": no initial live-reader focus.");
                await catalog.CaptureAsync(Snapshot(++count));
                entered = Signal(); release = Signal();
                if (scenario == "coverage-during-anchor")
                    page.BeforePassiveReaderFocusAsync = () => { entered.TrySetResult(); return release.Task; };
                else holdReload = true;
                update = page.CheckReaderUpdatesAsync();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(!update.IsCompleted, scenario + ": reload did not remain held.");
                if (scenario == "external-during-load")
                    Require(external.Focus() && external.IsFocused, scenario + ": external input refused focus.");
                else if (scenario == "coverage-during-anchor")
                {
                    page.SetObscuredByGallery(true); page.SetObscuredByGallery(false);
                    Require(external.Focus() && external.IsFocused, scenario + ": covering focus refused.");
                }
                int latePostFocus = 0;
                void Focused(object? sender, FocusChangedEventArgs e)
                {
                    if (e.Source is Visual target && target.GetVisualAncestors().Contains(posts)) latePostFocus++;
                }
                host.AddHandler(InputElement.GotFocusEvent, Focused, RoutingStrategies.Bubble, handledEventsToo: true);
                try
                {
                    release.TrySetResult();
                    await update.WaitAsync(TimeSpan.FromSeconds(10));
                    await SettleAsync();
                    string newest = (1000 + count - 1).ToString();
                    Require(page.CurrentThread?.PostCount == count && (posts.SelectedItem as ArchivePostRow)?.Id == newest,
                        scenario + ": guarded refresh lost the appended content/follow target.");
                    Require(scenario == "no-new-intent" ? PostContainer(posts, newest).IsKeyboardFocusWithin
                        : external.IsFocused && latePostFocus == 0,
                        scenario + ": whole passive reload violated focus intent. " + Describe(host));
                }
                finally
                {
                    release.TrySetResult();
                    await update.WaitAsync(TimeSpan.FromSeconds(10));
                    host.RemoveHandler(InputElement.GotFocusEvent, Focused);
                    page.BeforePassiveReaderFocusAsync = null;
                }
                await page.MediaLoadTask;
            }
        }
        finally
        {
            release?.TrySetResult();
            page?.EndLifetime();
            await update.WaitAsync(TimeSpan.FromSeconds(10));
            host?.Close();
            owner.Activate();
            await Program.WaitUntilAsync(() => owner.IsActive);
            await SettleAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Control PostContainer(ListBox posts, string id) => (Control)posts.ContainerFromItem(
        posts.ItemsSource!.Cast<ArchivePostRow>().Single(row => row.Id == id))!;
    private static Button GalleryButton(GalleryViewer viewer, string name) => viewer.GetVisualDescendants()
        .OfType<Button>().Single(button => button.Name == name);
    private static string Describe(Window host) => "active=" + host.IsActive + "; focused="
        + (host.FocusManager?.GetFocusedElement() is Control control ? control.GetType().Name + "#" + control.Name : "none");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
