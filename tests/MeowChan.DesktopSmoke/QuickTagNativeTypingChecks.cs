using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Tags;
using SkiaSharp;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

// Tom 17.11.2026 04:31: "Typing in the input field in tagging does not work" (Archive, List mode, the media sidebar playing the selected post's video,
// the quick tag popover open with the tag field showing focus and a caret). Every earlier check raised KeyDown and then a separate TextInput event
// on the field, so it never saw what a real keyboard does: the platform turns a key into text only when its KeyDown comes back UNHANDLED
// (X11 raises the TextInput of a key press after an unhandled KeyDown, Win32 drops the WM_CHAR of a handled WM_KEYDOWN, macOS runs interpretKeyEvents
// only for an unhandled keyDown). This smoke types with OS input (XTest, private Xvfb) into the real main window: the popover from R, Ctrl+Shift+T and
// the media menu over a playing sidebar video and over a picture in the embedded reader, R and Ctrl+Shift+T in the large reader with its sidebar video,
// R on a Media page tile and a Tagging page row, the gallery's tag panel (Ctrl+Shift+T, R) and the Tagging page's stamp field. It records which element
// marked a typed key handled and whether a TextInput was raised at all, and fails unless the typed text arrives (also right after R, with the field
// focused), the popover stays, the video keeps playing and no page shortcut ran; the star keys, Alt+1, Ctrl+Z, Backspace, Ctrl+A, Esc and the focus
// hand-back are checked with the same OS input.
internal static class QuickTagNativeTypingChecks
{
    internal static readonly ResourceId Resource = new("quick-tag-smoke", "synthetic", "480");
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string VideoPost = "480", PicturePost = "481";
    // Letters that are shortcuts elsewhere (M favours, R rates, F, J, K), digits and a space.
    private const string Word = "mrf jk12";

    internal static async Task SeedAsync(string profile)
    {
        string fixture = Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE") is { Length: > 0 } configured ? configured : FindRepositoryFixture();
        string directory = Path.Combine(profile, "media", "quick-tag");
        Directory.CreateDirectory(directory);
        Dictionary<string, object> entries = new();
        List<CapturedPost> posts = new();
        void Add(string postId, int ordinal, string file, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(directory, file), bytes);
            string media = "qt-" + file, url = "https://offline.invalid/" + file;
            entries[file] = new { MediaIdentifier = media, Variant = "original", SourceUrl = url, FileName = file, OriginalFileName = file,
                Length = bytes.Length, Md5 = Convert.ToBase64String(MD5.HashData(bytes)), Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
            posts.Add(new CapturedPost(postId, ordinal, PostFieldPresence.All, Html: "<p>Quick tag post " + postId + "</p>",
                Attachments: [new CapturedAttachment(0, media, "original", url, file)]));
        }
        Add(VideoPost, 0, "1745935984041883.webm", await File.ReadAllBytesAsync(fixture));
        Add(PicturePost, 1, "picture-481.jpg", Jpeg(1));
        Add("482", 2, "picture-482.jpg", Jpeg(2));
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        CapturedPost[] bound = await StrictMediaFixture.BindAsync(directory, Resource, posts);
        using Persistence.ProfileLease lease = Persistence.ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(new(Resource, "https://offline.invalid/480", "TikThots", ThreadSourceState.Active, DateTimeOffset.UnixEpoch, directory, bound));
    }

    private static string FindRepositoryFixture()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "Fixtures", "gallery-playback-vp8.webm");
            if (File.Exists(candidate)) return candidate;
        }
        string current = Path.Combine(Environment.CurrentDirectory, "tests", "Fixtures", "gallery-playback-vp8.webm");
        if (File.Exists(current)) return current;
        throw new FileNotFoundException("The quick tag typing smoke needs tests/Fixtures/gallery-playback-vp8.webm or MEOWCHAN_VIDEO_FIXTURE.");
    }

    private sealed record Case(string Surface, string Entry, string Medium, string FocusOnOpen, string FieldAfterOpenTyping, string FocusAfterClick,
        string FieldAfterTyping, bool VideoPlaying, string Handled, int TextInputs, string? Failure);

    internal static async Task RunAsync(MainWindow window)
    {
        ArchivePage page = window.ArchivePage;
        window.Width = 1920; window.Height = 1080;
        using MediaNativeInput input = new(window);
        KeyTrace trace = new();
        trace.Install();
        List<Case> cases = new();
        // One press, one character: no server autorepeat when a loaded host stretches a 40 ms press past the repeat delay.
        input.SetAutoRepeat(false);
        try
        {
            // Suggestions for Alt+1 (the strip offers the profile's tags).
            await window.SessionHost.DefinePrivateTagAsync("innocent");
            await window.SessionHost.DefinePrivateTagAsync("existing");
            Navigate(window, "Archive", activate: true);
            await page.LoadThreadAsync(Resource);
            await page.MediaLoadTask;
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { Sidebar = true, SidebarAlways = true,
                SidebarMinimumReaderWidth = ReaderDisplayPreferences.MediaSidebarReaderWidthMinimum });
            await Settle(window);

            // A: the embedded reader (Tom's case), each entry point on the playing sidebar video and on a picture.
            foreach (string medium in new[] { "video", "picture" })
                foreach (string entry in new[] { "R", "Ctrl+Shift+T", "menu" })
                    cases.Add(await ArchiveCaseAsync(window, page, input, trace, "archive", entry, medium));
            cases.Add(await PopoverKeysAsync(window, page, input));

            // B: the large reader (opened the Downloads way), its sidebar playing the video.
            Navigate(window, "Downloads", activate: true);
            await Settle(window);
            typeof(MainWindow).GetMethod("OpenLargeReader", Private, null, [typeof(Control), typeof(IReadOnlyList<ResourceId>)], null)!
                .Invoke(window, [null, new[] { Resource }]);
            await page.LoadThreadAsync(Resource);
            await page.MediaLoadTask;
            if (!ReaderMediaSidebarRules.IsOn(page.ReaderMedia, true)) page.ToggleMediaSidebar();
            await Settle(window);
            try { await Program.WaitUntilAsync(() => page.IsMediaSidebarShown && !page.IsBusy); } catch (TimeoutException) { }
            await Settle(window);
            foreach (string entry in new[] { "R", "Ctrl+Shift+T" })
                cases.Add(await ArchiveCaseAsync(window, page, input, trace, "large-reader", entry, "video"));
            typeof(MainWindow).GetMethod("CloseLargeReader", Private)!.Invoke(window, [true]);
            await Settle(window);

            // C: the gallery's tag panel (Ctrl+Shift+T and R: the field) over the picture.
            cases.AddRange(await GalleryCasesAsync(window, page, input, trace));

            // D: the Media page (R on a tile) and E: the Tagging page (R on a row, the stamp field).
            cases.Add(await MediaPageCaseAsync(window, input, trace));
            cases.AddRange(await TaggingPageCasesAsync(window, page, input, trace));
        }
        finally
        {
            input.SetAutoRepeat(true);
            trace.Uninstall();
            QuickTagPopover.Current?.Close();
        }
        foreach (Case result in cases)
            Console.WriteLine("SMOKE quick-tag-native-typing " + JsonSerializer.Serialize(result));
        string[] failures = cases.Where(result => result.Failure != null).Select(result => result.Surface + "/" + result.Entry + "/" + result.Medium + ": " + result.Failure).ToArray();
        Program.CaptureWindow(window, "smoke-quick-tag-native-typing.png");
        if (failures.Length > 0) throw new InvalidOperationException("Quick tag native typing failed: " + string.Join(" | ", failures));
        Console.WriteLine("SMOKE quick-tag-native-typing PASS: " + cases.Count + " cases typed with XTest (Rate and tag from R, Ctrl+Shift+T and the menu over a playing sidebar video "
            + "and a picture in the embedded and the large reader, Media and Tagging page, gallery tag panel, stamp field; star keys, Alt+1, Ctrl+Z, Backspace, Ctrl+A, Esc); private Xvfb only.");
    }

    // ---- the archive reader ----------------------------------------------------------------------------------------------------------

    private static async Task<ListBoxItem?> SelectPostAsync(MainWindow window, ArchivePage page, MediaNativeInput input, string postId, bool video)
    {
        ListBox list = Named<ListBox>(window, "ArchivePosts");
        ArchivePostRow post = list.ItemsSource!.Cast<ArchivePostRow>().Single(row => row.Id == postId);
        list.SelectedItem = post;
        list.ScrollIntoView(post);
        await Settle(window);
        if (video)
        {
            await Program.WaitUntilAsync(() => page.SidebarInlineVideo?.IsOffered == true, () => $"sidebar shown={page.IsMediaSidebarShown} offered={page.SidebarInlineVideo?.IsOffered}");
            ReaderInlineVideoSlot slot = page.SidebarInlineVideo!;
            if (!slot.IsPlaying)
            {
                if (slot.Activator.IsEffectivelyVisible) await input.ClickAsync(slot.Activator);
                await slot.ActivationTask;
                await Program.WaitUntilAsync(() => slot.IsPlaying);
            }
        }
        ListBoxItem? card = list.ContainerFromItem(post) as ListBoxItem;
        card?.Focus(NavigationMethod.Tab);
        await Settle(window);
        return card;
    }

    private static string ArchiveState(MainWindow window, ArchivePage page) =>
        (Named<ListBox>(window, "ArchivePosts").SelectedItem as ArchivePostRow)?.Id + "|" + page.MediaSidebarIndex + "|" + page.ReaderMedia
        + "|" + string.Join(",", window.GetVisualDescendants().OfType<ArchiveFavoriteButton>().Select(star => star.IsChecked));

    private static async Task<Case> ArchiveCaseAsync(MainWindow window, ArchivePage page, MediaNativeInput input, KeyTrace trace, string surface, string entry, string medium)
    {
        bool video = medium == "video";
        ListBoxItem? card;
        try { card = await SelectPostAsync(window, page, input, video ? VideoPost : PicturePost, video); }
        catch (TimeoutException error) { return Fail(surface, entry, medium, "the sidebar video did not play: " + error.Message); }
        if (card == null) return Fail(surface, entry, medium, "the post card is not realized");
        async Task<bool> Open()
        {
            if (entry == "menu")
            {
                ArchiveAttachmentView view = card.GetVisualDescendants().OfType<ArchiveAttachmentView>().First();
                await input.RightClickAsync(view);
                object presenter = typeof(ArchivePage).GetField("_mediaMenuPresenter", Private)!.GetValue(page)!;
                ContextMenu? menu = null;
                await Program.WaitUntilAsync(() => (menu = (ContextMenu?)presenter.GetType().GetProperty("Menu", Private)!.GetValue(presenter)) is { IsOpen: true });
                await Settle(window);
                string label = Texts.T("command.media.rating.edit");
                MenuItem? item = menu!.GetLogicalDescendants().OfType<MenuItem>().FirstOrDefault(candidate => Equals(candidate.Header, label));
                if (item == null) return false;
                await input.ClickAsync(item, requireHitTest: false);
            }
            else if (entry == "R") await input.KeyAsync('r');
            else await input.KeyAsync('t', modified: true); // Ctrl+Shift+T
            return true;
        }
        Console.WriteLine($"SMOKE quick-tag-native-typing focus-before {surface}/{entry}/{medium}: {Describe(window.FocusManager?.GetFocusedElement())} card={Describe(card)}");
        return await PopoverCaseAsync(window, input, trace, surface, entry, medium, Open, () => !video || page.SidebarInlineVideo?.IsPlaying == true,
            () => ArchiveState(window, page), card);
    }

    // The keys of the popover itself, with OS input: Shift+3 and Shift+0 on the stars (R), Alt+1 and Ctrl+Z on an empty field, Backspace and Ctrl+A.
    private static async Task<Case> PopoverKeysAsync(MainWindow window, ArchivePage page, MediaNativeInput input)
    {
        const string surfaceName = "archive", entry = "keys", medium = "picture";
        ListBoxItem? card = await SelectPostAsync(window, page, input, PicturePost, video: false);
        if (card == null) return Fail(surfaceName, entry, medium, "the post card is not realized");
        // R held past the server's repeat delay (Xvfb: 660 ms): the repeats must not land in the field as "rrr" (the popover latches the opening key).
        input.SetAutoRepeat(true);
        try { await input.HoldAsync('r', TimeSpan.FromMilliseconds(1400)); }
        finally { input.SetAutoRepeat(false); }
        try { await Program.WaitUntilAsync(() => QuickTagPopover.Current is { IsOpen: true, Surface: not null, DecisionRow: not null }); }
        catch (TimeoutException) { return Fail(surfaceName, entry, medium, "R opened no popover"); }
        QuickTagPopover popover = QuickTagPopover.Current!;
        QuickTagSurface surface = popover.Surface!;
        GalleryDecisionRow row = popover.DecisionRow!;
        await Settle(window);
        List<string> problems = new();
        if (!string.IsNullOrEmpty(surface.Input.Text)) problems.Add("the repeats of a held R were typed into the field: '" + surface.Input.Text + "'");
        if (!surface.Input.IsKeyboardFocusWithin) problems.Add("R did not put the caret in the tag input");
        // Preserve the existing direct-rating contract on an empty field; once typing starts Shift+digit is text, not a rating.
        await input.ChordAsync('3', MediaNativeInput.ShiftLeft);
        await WaitQuietly(() => row.View.Rating == 3);
        if (row.View.Rating != 3 || !string.IsNullOrEmpty(surface.Input.Text)) problems.Add("Shift+3 on the empty field did not rate 3");
        await input.ChordAsync('0', MediaNativeInput.ShiftLeft);
        await WaitQuietly(() => row.View.Rating == null);
        if (row.View.Rating != null) problems.Add("Shift+0 on the empty field did not clear the rating");
        await TypeAsync(input, "r");
        await Settle(window);
        if (surface.Input.Text != "r") problems.Add("a new R after the held one did not type 'r': '" + surface.Input.Text + "'");
        await input.ChordAsync('3', MediaNativeInput.ShiftLeft);
        await Settle(window);
        if (surface.Input.Text != "r#" || row.View.Rating != null) problems.Add("Shift+3 while typing did not remain text: '" + surface.Input.Text + "'");
        surface.Input.Text = "";
        surface.Input.Dismiss();
        row.FocusRating();
        await Settle(window);
        string focusOnOpen = Describe(TopLevel.GetTopLevel(popover.Frame)?.FocusManager?.GetFocusedElement());
        if (!row.IsRatingFocused) problems.Add("Explicit star focus failed (" + focusOnOpen + ")");
        await input.KeyAsync(MediaNativeInput.Right);
        await Settle(window);
        if (!row.StarButtons[1].IsFocused) problems.Add("Right did not rove from the unrated star to star 2");
        await input.KeyAsync(MediaNativeInput.Space);
        await WaitQuietly(() => row.View.Rating == 2);
        if (row.View.Rating != 2) problems.Add("Space on the focused star did not rate 2");
        await input.ChordAsync('3', MediaNativeInput.ShiftLeft);
        await WaitQuietly(() => row.View.Rating == 3);
        if (row.View.Rating != 3) problems.Add("Shift+3 on the stars did not rate 3 (" + row.View.Rating + ")");
        await input.ChordAsync('0', MediaNativeInput.ShiftLeft);
        await WaitQuietly(() => row.View.Rating == null);
        if (row.View.Rating != null) problems.Add("Shift+0 did not clear the rating (" + row.View.Rating + ")");
        // A typed tag and Enter assign it; Ctrl+Z on the then empty field takes it back. Alt+1 toggles the first suggestion (the tag just used).
        int steps = surface.Session.UndoSteps.Count;
        TextBox field = surface.Input.GetVisualDescendants().OfType<TextBox>().First(candidate => candidate.IsEffectivelyVisible);
        await input.ClickAsync(field, requireHitTest: false);
        await Settle(window);
        // The suggestion list of the field is a popup inside the popover's popup: typing opens it, a real click on its row takes the tag.
        await TypeAsync(input, "inn");
        string listState = "";
        try
        {
            await Program.WaitUntilAsync(() => surface.Input.IsDropDownOpen && SuggestionRow(surface.Input, "innocent") is { Bounds.Width: > 0 } shownRow
                && TopLevel.GetTopLevel(shownRow) is { IsVisible: true });
            Control suggestion = SuggestionRow(surface.Input, "innocent")!;
            listState = $"root={TopLevel.GetTopLevel(suggestion)?.GetType().Name} visible={TopLevel.GetTopLevel(suggestion)?.IsVisible} at={suggestion.PointToScreen(default)}";
            await input.ClickAsync(suggestion);
            await WaitQuietly(() => surface.Session.UndoSteps.Count == steps + 1 || surface.Input.Text == "innocent");
            if (surface.Session.UndoSteps.Count == steps + 1) { await input.ChordAsync('z', MediaNativeInput.ControlLeft); await WaitQuietly(() => surface.Session.UndoSteps.Count == steps); }
            else if (surface.Input.Text != "innocent") problems.Add("a click on the suggestion row took nothing (" + listState + ", field '" + surface.Input.Text + "')");
        }
        catch (TimeoutException) { problems.Add("typing 'inn' showed no suggestion row (open=" + surface.Input.IsDropDownOpen + ")"); }
        catch (ArgumentException error) { problems.Add("the suggestion row cannot be clicked: " + error.Message + " " + listState); }
        Console.WriteLine("SMOKE quick-tag-native-typing suggestion-list: " + listState + " steps " + steps + "->" + surface.Session.UndoSteps.Count + " field '" + surface.Input.Text + "'");
        surface.Input.Text = "";
        surface.Input.Dismiss();
        await Settle(window);
        await TypeAsync(input, "innocent");
        await input.KeyAsync(MediaNativeInput.Enter);
        await WaitQuietly(() => surface.Session.UndoSteps.Count == steps + 1);
        if (surface.Session.UndoSteps.Count != steps + 1) problems.Add("typing 'innocent' and Enter assigned nothing (field '" + surface.Input.Text + "')");
        await input.ChordAsync('z', MediaNativeInput.ControlLeft);
        await WaitQuietly(() => surface.Session.UndoSteps.Count == steps);
        if (surface.Session.UndoSteps.Count != steps) problems.Add("Ctrl+Z on the empty field did not undo the tag");
        await WaitQuietly(() => surface.Shown.Count > 0);
        if (surface.Shown.Count == 0) problems.Add("no suggestion shown for Alt+1");
        else
        {
            await input.ChordAsync('1', MediaNativeInput.AltLeft);
            await WaitQuietly(() => surface.Session.UndoSteps.Count == steps + 1);
            if (surface.Session.UndoSteps.Count != steps + 1) problems.Add("Alt+1 did not toggle the first suggestion (" + surface.Shown[0].Name.Display + ")");
            else
            {
                await input.ChordAsync('z', MediaNativeInput.ControlLeft);
                await WaitQuietly(() => surface.Session.UndoSteps.Count == steps);
            }
        }
        TextBox box = surface.Input.GetVisualDescendants().OfType<TextBox>().First(candidate => candidate.IsEffectivelyVisible);
        await input.ClickAsync(box, requireHitTest: false);
        await Settle(window);
        await TypeAsync(input, "abc");
        await input.KeyAsync(MediaNativeInput.BackSpace);
        await Settle(window);
        string afterBackspace = surface.Input.Text ?? "";
        if (afterBackspace != "ab") problems.Add("Backspace: '" + afterBackspace + "'");
        await input.ChordAsync('a', MediaNativeInput.ControlLeft);
        await TypeAsync(input, "x");
        await Settle(window);
        if (surface.Input.Text != "x") problems.Add("Ctrl+A then a letter: '" + surface.Input.Text + "'");
        // Esc empties the field, the next closes; the keyboard goes back to the post card.
        await input.KeyAsync(MediaNativeInput.Escape);
        await Settle(window);
        if (popover.IsOpen) { await input.KeyAsync(MediaNativeInput.Escape); await Settle(window); }
        if (popover.IsOpen) problems.Add("Esc did not close the popover");
        Control? focused = window.FocusManager?.GetFocusedElement() as Control;
        if (focused == null || !(ReferenceEquals(focused, card) || focused.GetVisualAncestors().Contains(card))) problems.Add("the focus did not return to the post card: " + Describe(focused));
        return new Case(surfaceName, entry, medium, focusOnOpen, "", "", afterBackspace, true, "", 0, problems.Count == 0 ? null : string.Join("; ", problems));
    }

    // ---- one popover: open, type right away, click the field, type, Esc ---------------------------------------------------------------------

    private static async Task<Case> PopoverCaseAsync(MainWindow window, MediaNativeInput input, KeyTrace trace, string surfaceName, string entry, string medium,
        Func<Task<bool>> open, Func<bool> videoPlaying, Func<string> state, Control? origin)
    {
        string before = state();
        QuickTagPopover? previous = QuickTagPopover.Current;
        trace.Begin();
        try { if (!await open()) return Fail(surfaceName, entry, medium, "the entry point is missing"); }
        catch (TimeoutException error) { return Fail(surfaceName, entry, medium, "opening timed out: " + error.Message); }
        try { await Program.WaitUntilAsync(() => QuickTagPopover.Current is { IsOpen: true, Surface: not null } current && !ReferenceEquals(current, previous)); }
        catch (TimeoutException) { return Fail(surfaceName, entry, medium, "the popover did not open (open key handled at " + trace.FirstHandled() + ")"); }
        finally { Console.WriteLine($"SMOKE quick-tag-native-typing open-key {surfaceName}/{entry}/{medium}: handled at {trace.FirstHandled()}, text inputs {trace.TextInputs}"); trace.End(); }
        QuickTagPopover popover = QuickTagPopover.Current!;
        QuickTagSurface surface = popover.Surface!;
        await Settle(window);
        TopLevel? popupRoot = TopLevel.GetTopLevel(popover.Frame);
        string focusOnOpen = Describe(popupRoot?.FocusManager?.GetFocusedElement());

        // All general entry points must already have the caret in the field, before any typing or click.
        bool fieldFocusedOnOpen = surface.Input.IsKeyboardFocusWithin;
        trace.Begin();
        await TypeAsync(input, "q");
        await Settle(window);
        string afterOpenTyping = surface.Input.Text ?? "";
        trace.End();

        surface.Input.Text = "";
        await Settle(window);
        TextBox? box = surface.Input.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(candidate => candidate.IsEffectivelyVisible);
        await input.ClickAsync((Control?)box ?? surface.Input, requireHitTest: false);
        await Settle(window);
        string focusAfterClick = Describe(popupRoot?.FocusManager?.GetFocusedElement());
        trace.Begin();
        await TypeAsync(input, Word);
        await Settle(window);
        string typed = surface.Input.Text ?? "";
        string handled = trace.FirstHandled();
        int textInputs = trace.TextInputs;
        trace.End();
        bool same = ReferenceEquals(QuickTagPopover.Current, popover) && popover.IsOpen;
        bool playing = videoPlaying();
        string after = state();
        surface.Input.Text = "";
        await Settle(window);
        await input.KeyAsync(MediaNativeInput.Escape);
        await Settle(window);
        if (popover.IsOpen) { await input.KeyAsync(MediaNativeInput.Escape); await Settle(window); }
        Control? focused = (origin == null ? null : TopLevel.GetTopLevel(origin))?.FocusManager?.GetFocusedElement() as Control;
        List<string> problems = new();
        if (!fieldFocusedOnOpen) problems.Add("opening did not focus the tag input (" + focusOnOpen + ")");
        if (afterOpenTyping != "q") problems.Add($"'q' typed right after opening gave '{afterOpenTyping}' (focus {focusOnOpen})");
        if (typed != Word) problems.Add($"'{Word}' typed after a click gave '{typed}' (focus {focusAfterClick}; first handled at {handled}; text inputs {textInputs})");
        if (!same) problems.Add("the popover closed or was replaced while typing");
        if (!playing) problems.Add("the sidebar video stopped");
        if (after != before) problems.Add("a typed key changed the page: " + before + " -> " + after);
        if (popover.IsOpen) problems.Add("Esc did not close the popover");
        if (origin != null && (focused == null || !(ReferenceEquals(focused, origin) || focused.GetVisualAncestors().Contains(origin))))
            problems.Add("the focus did not return to " + Describe(origin) + " but to " + Describe(focused));
        return new Case(surfaceName, entry, medium, focusOnOpen, afterOpenTyping, focusAfterClick, typed, playing, handled, textInputs,
            problems.Count == 0 ? null : string.Join("; ", problems));
    }

    // ---- the gallery's tag panel -----------------------------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<Case>> GalleryCasesAsync(MainWindow window, ArchivePage page, MediaNativeInput input, KeyTrace trace)
    {
        List<Case> cases = new();
        Navigate(window, "Archive", activate: true);
        await page.LoadThreadAsync(Resource);
        await page.MediaLoadTask;
        await Settle(window);
        // The reader's own "Gallery" button over the selected picture post (the real path; the shell wires the tag panel).
        await SelectPostAsync(window, page, input, PicturePost, video: false);
        await input.ClickAsync(Named<Button>(window, "ArchiveOpenGallery"));
        ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
        try { await Program.WaitUntilAsync(() => host.Content is GalleryViewer && host.IsVisible); }
        catch (TimeoutException) { return [Fail("gallery", "-", "picture", "the Gallery button opened no gallery")]; }
        GalleryViewer viewer = (GalleryViewer)host.Content!;
        try { await Program.WaitUntilAsync(() => viewer.TagsButton.IsEnabled); }
        catch (TimeoutException)
        {
            GalleryViewerChecksAccess.Click(viewer, "GalleryClose");
            await Settle(window);
            return [Fail("gallery", "-", "picture", "the gallery offers no tags for the picture")];
        }
        await Settle(window);
        viewer.Focus();
        await Settle(window);
        foreach (string entry in new[] { "Ctrl+Shift+T", "R" })
        {
            string before = viewer.CaptureState().ToString();
            Console.WriteLine($"SMOKE quick-tag-native-typing focus-before gallery/{entry}: {Describe(window.FocusManager?.GetFocusedElement())} viewer-focus-within={viewer.IsKeyboardFocusWithin}");
            trace.Begin();
            if (entry == "R") await input.KeyAsync('r'); else await input.KeyAsync('t', modified: true);
            await Settle(window);
            Console.WriteLine($"SMOKE quick-tag-native-typing open-key gallery/{entry}: handled at {trace.FirstHandled()}, text inputs {trace.TextInputs}, focus {Describe(window.FocusManager?.GetFocusedElement())}");
            trace.End();
            try { await Program.WaitUntilAsync(() => viewer.IsTagPanelOpen && viewer.TagSurface != null); }
            catch (TimeoutException) { cases.Add(Fail("gallery", entry, "picture", "the tag panel did not open (popover " + (QuickTagPopover.Current?.IsOpen == true) + ")")); QuickTagPopover.Current?.Close(); continue; }
            await Settle(window);
            QuickTagSurface surface = viewer.TagSurface!;
            surface.Input.Text = "";
            string focusOnOpen = Describe(window.FocusManager?.GetFocusedElement());
            bool fieldFocusedOnOpen = surface.Input.IsKeyboardFocusWithin;
            trace.Begin();
            await TypeAsync(input, "q");
            await Settle(window);
            string afterOpenTyping = surface.Input.Text ?? "";
            trace.End();
            surface.Input.Text = "";
            TextBox box = surface.Input.GetVisualDescendants().OfType<TextBox>().First(candidate => candidate.IsEffectivelyVisible);
            await input.ClickAsync(box, requireHitTest: false);
            await Settle(window);
            string focusAfterClick = Describe(window.FocusManager?.GetFocusedElement());
            trace.Begin();
            await TypeAsync(input, Word);
            await Settle(window);
            string typed = surface.Input.Text ?? "";
            string handled = trace.FirstHandled();
            int textInputs = trace.TextInputs;
            trace.End();
            surface.Input.Text = "";
            List<string> problems = new();
            if (!fieldFocusedOnOpen) problems.Add("opening did not focus the tag input (" + focusOnOpen + ")");
            if (typed != Word) problems.Add($"'{Word}' typed after a click gave '{typed}' (focus {focusAfterClick}; first handled at {handled}; text inputs {textInputs})");
            if (afterOpenTyping != "q") problems.Add($"'q' typed into the focused field gave '{afterOpenTyping}' (focus {focusOnOpen})");
            if (!viewer.IsTagPanelOpen || !ReferenceEquals(host.Content, viewer)) problems.Add("typing closed the tag panel or the gallery");
            cases.Add(new Case("gallery", entry, viewer.CaptureState().PostId == VideoPost ? "video" : "picture", focusOnOpen, afterOpenTyping, focusAfterClick, typed, true, handled, textInputs,
                problems.Count == 0 ? null : string.Join("; ", problems)));
            Console.WriteLine("SMOKE quick-tag-native-typing gallery-state " + entry + ": before=" + before + " after=" + viewer.CaptureState());
            // Esc leaves the field / closes the panel; the next case reopens it.
            await input.KeyAsync(MediaNativeInput.Escape);
            await Settle(window);
            if (viewer.IsTagPanelOpen) { await input.KeyAsync(MediaNativeInput.Escape); await Settle(window); }
            if (!ReferenceEquals(host.Content, viewer)) break;
            viewer.Focus();
            await Settle(window);
        }
        if (ReferenceEquals(host.Content, viewer)) { await input.KeyAsync(MediaNativeInput.Escape); await Settle(window); }
        if (host.Content is GalleryViewer open) { GalleryViewerChecksAccess.Click(open, "GalleryClose"); await Settle(window); }
        return cases;
    }

    // ---- the Media page and the Tagging page ------------------------------------------------------------------------------------------

    private static async Task<Case> MediaPageCaseAsync(MainWindow window, MediaNativeInput input, KeyTrace trace)
    {
        Navigate(window, "Media", activate: true);
        MediaPage? media = null;
        try
        {
            await Program.WaitUntilAsync(() => (media = window.MediaPageForTests) != null);
            await media!.BuildTask;
            await Program.WaitUntilAsync(() => ((MediaLibraryTileRow[]?)media.Grid.ItemsSource)?.Any(row => row.Entry.PostId == PicturePost) == true);
        }
        catch (TimeoutException) { return Fail("media", "R", "picture", "the Media page shows no tile of the fixture"); }
        MediaLibraryTileRow tile = ((MediaLibraryTileRow[])media.Grid.ItemsSource!).First(row => row.Entry.PostId == PicturePost);
        media.Grid.SelectedItem = tile;
        await Settle(window);
        Control? container = media.Grid.ContainerFromItem(tile) as Control;
        container?.Focus(NavigationMethod.Tab);
        await Settle(window);
        string State() => (media.Grid.SelectedItem as MediaLibraryTileRow)?.Entry.PostId + "|" + media.ShownEntries.Count;
        return await PopoverCaseAsync(window, input, trace, "media", "R", "picture", async () => { await input.KeyAsync('r'); return true; }, () => true, State, container);
    }

    private static async Task<IReadOnlyList<Case>> TaggingPageCasesAsync(MainWindow window, ArchivePage page, MediaNativeInput input, KeyTrace trace)
    {
        List<Case> cases = new();
        Navigate(window, "Archive", activate: true);
        await page.LoadThreadAsync(Resource);
        await page.MediaLoadTask;
        ArchivePostRow post = Named<ListBox>(window, "ArchivePosts").ItemsSource!.Cast<ArchivePostRow>().Single(row => row.Id == PicturePost);
        GalleryEntry? picture = post.Attachments[0].ResolveEntry is { } resolve ? await resolve(CancellationToken.None) : null;
        TagQueueItem? item = picture == null ? null : TagQueueItem.From(picture, DateTimeOffset.UtcNow);
        if (item == null) return [Fail("tagging", "R", "picture", "the picture makes no queue item")];
        await window.SessionHost.EnqueueTagQueueAsync([item]);
        SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Tagging);
        TaggingPage? tagging = null;
        try { await Program.WaitUntilAsync(() => (tagging = window.TaggingPage) is { IsBusy: false } && tagging.Rows.Count > 0); }
        catch (TimeoutException) { return [Fail("tagging", "R", "picture", "the Tagging page shows no row")]; }
        await Settle(window);

        // The stamp field: a text box of the page itself.
        TextBox stamp = tagging!.StampInput;
        if (!tagging.StampOpen) { await input.ClickAsync(tagging.StampToggle); await Settle(window); }
        stamp.Text = "";
        await input.ClickAsync(stamp, requireHitTest: false);
        await Settle(window);
        trace.Begin();
        await TypeAsync(input, Word);
        await Settle(window);
        string stamped = stamp.Text ?? "";
        string handled = trace.FirstHandled();
        int inputs = trace.TextInputs;
        trace.End();
        cases.Add(new Case("tagging", "stamp-field", "-", "", "", Describe(window.FocusManager?.GetFocusedElement()), stamped, true, handled, inputs,
            stamped == Word ? null : $"'{Word}' typed into the stamp field gave '{stamped}' (first handled at {handled}; text inputs {inputs})"));
        stamp.Text = "";

        // R on the selected row: the same popover.
        ListBox? rows = tagging.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(list => list.IsEffectivelyVisible && list.ItemCount > 0);
        if (rows == null) { cases.Add(Fail("tagging", "R", "picture", "the Tagging page has no row list")); return cases; }
        rows.SelectedIndex = 0;
        await Settle(window);
        Control? row = rows.ContainerFromIndex(0);
        row?.Focus(NavigationMethod.Tab);
        await Settle(window);
        cases.Add(await PopoverCaseAsync(window, input, trace, "tagging", "R", "picture", async () => { await input.KeyAsync('r'); return true; }, () => true,
            () => rows.SelectedIndex + "|" + tagging.Rows.Count, row));
        return cases;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    private static Control? SuggestionRow(IdentitySuggestionBox field, string text) =>
        field.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().FirstOrDefault()?.Child?.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(item => item.IsEffectivelyVisible && item.DataContext?.ToString()?.Contains(text, StringComparison.Ordinal) == true);

    private static Case Fail(string surface, string entry, string medium, string reason) => new(surface, entry, medium, "", "", "", "", false, "", 0, reason);

    private static async Task WaitQuietly(Func<bool> condition)
    {
        try { await Program.WaitUntilAsync(condition); } catch (TimeoutException) { }
    }

    private static async Task TypeAsync(MediaNativeInput input, string text)
    {
        foreach (char character in text) await input.KeyAsync(character == ' ' ? MediaNativeInput.Space : character);
    }

    // Records, for the keys typed while it is on, the first element of a KeyDown route that saw the key already handled (class handlers run before the
    // element's own handlers, so the handler that set it sits on the element before) and how many TextInput events reached their target.
    private sealed class KeyTrace
    {
        private bool _on;
        private readonly List<string> _handled = new();
        internal int TextInputs { get; private set; }
        private string _previous = "";

        internal void Install()
        {
            InputElement.KeyDownEvent.AddClassHandler(typeof(Interactive), (sender, args) =>
            {
                KeyEventArgs e = (KeyEventArgs)args;
                if (!_on || QuickTagPopover.IsModifierKey(e.Key)) return;
                string here = (e.Route == RoutingStrategies.Tunnel ? "tunnel:" : "bubble:") + Describe(sender);
                if (e.Handled && !_handled.Any(entry => entry.StartsWith(e.Key + "@", StringComparison.Ordinal)))
                    _handled.Add(e.Key + "@" + here + " (after " + _previous + ")");
                _previous = here;
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            InputElement.TextInputEvent.AddClassHandler(typeof(Interactive), (sender, args) =>
            {
                if (_on && args.Route == RoutingStrategies.Bubble && ReferenceEquals(sender, args.Source)) TextInputs++;
            }, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        internal void Uninstall() => _on = false; // class handlers cannot be removed; they stay inert

        internal void Begin() { _handled.Clear(); TextInputs = 0; _previous = ""; _on = true; }
        internal void End() => _on = false;
        internal string FirstHandled() => _handled.Count == 0 ? "never" : string.Join(" | ", _handled.Take(3));
    }

    private static string Describe(object? element) => element switch
    {
        null => "null",
        Control control => control.GetType().Name + (string.IsNullOrEmpty(control.Name) ? "" : "#" + control.Name),
        _ => element.GetType().Name
    };

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(control => control.Name == name && control.IsEffectivelyVisible);

    private static void Navigate(MainWindow window, string page, bool activate) =>
        typeof(MainWindow).GetMethod("NavigateWorkspace", Private)!.Invoke(window, [Enum.Parse(typeof(MainWindow.WorkspacePage), page), activate]);

    private static async Task Settle(Window window)
    {
        for (int turn = 0; turn < 3; turn++)
        {
            await SmokeDispatcher.SettleAsync(2);
            window.UpdateLayout();
            await Task.Delay(50);
        }
    }

    private static byte[] Jpeg(int index)
    {
        using SKBitmap bitmap = new(320, 240, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (SKCanvas canvas = new(bitmap)) canvas.Clear(new SKColor((byte)(index * 67), (byte)(120 + index * 30), 200));
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }
}
