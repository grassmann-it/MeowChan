using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// PT-02, PT-03 and RD-02 with the real window and real pointer events: a press in a post's text selects the post and keeps the focus in the
/// text, a right-click in the text opens the post menu (with "Copy" first for a selection), and the key of the reader's find finds in the
/// thread from inside the reader while the results list keeps the archive search. Runs for the embedded and the large reader.
/// </summary>
internal static class ReaderPointerChecks
{
    internal static async Task RunAsync(MainWindow window, ArchivePage page, bool large)
    {
        string where = large ? "large reader" : "embedded reader";
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        ReaderBodyTextBlock BodyOf(string id) => posts.GetVisualDescendants().OfType<ReaderBodyTextBlock>()
            .First(body => body.GetVisualAncestors().OfType<ListBoxItem>().Any(item => item.DataContext is ArchivePostRow row && row.Id == id));
        string SelectedId() => (posts.SelectedItem as ArchivePostRow)?.Id ?? "";

        // Another post is selected; the press lands in the text of 1001.
        await page.FocusPostAsync("1000");
        await FlushAsync();
        ReaderBodyTextBlock body = BodyOf("1001");
        Require(body.PlainText.Length >= 4, $"The {where} fixture post has no text to press in.");
        Require(SelectedId() == "1000", $"The {where} fixture did not start with another post selected.");
        body.Focus();
        PressAndRelease(body);
        await FlushAsync();
        Require(SelectedId() == "1001", $"A press in the text of a post did not select that post in the {where}.");
        Require(body.IsFocused || body.IsKeyboardFocusWithin, $"Selecting the post by a press moved the focus out of its text in the {where}.");

        // A right-click on the text opens the post menu, the same one the card margin opens.
        await page.FocusPostAsync("1000");
        await FlushAsync();
        body = BodyOf("1001");
        body.Focus();
        body.SelectionStart = body.SelectionEnd = 0;
        Require(body.ContextFlyout == null, $"The post text keeps its own menu in the {where}.");
        string[] plain = OpenMenu(page, body, $"without a selection in the {where}");
        Require(plain.Length > 0 && plain[0] == "post.copy-text", $"The post menu does not start with the post commands without a selection in the {where}.");
        page.PostMenu!.Close();
        ListBoxItem card = body.GetVisualAncestors().OfType<ListBoxItem>().First();
        string[] fromCard = OpenMenu(page, card, $"on the card in the {where}");
        Require(plain.SequenceEqual(fromCard), $"The post menu from the text differs from the card's in the {where}.");
        page.PostMenu!.Close();

        // With a selection "Copy" leads, then a separator, then the unchanged post menu; it copies exactly the selection.
        body.Focus();
        body.SelectionStart = 0; body.SelectionEnd = 4;
        string expected = body.PlainText[..4];
        string? copied = null;
        page.CopyTextForTests = text => { copied = text; return Task.CompletedTask; };
        try
        {
            string[] withSelection = OpenMenu(page, body, $"with a selection in the {where}");
            Require(withSelection.Length == plain.Length + 2 && withSelection[0] == "post.copy-selection" && withSelection[1] == "-"
                && withSelection.Skip(2).SequenceEqual(plain), $"The post menu with a selection is not Copy, a separator and the post menu in the {where}.");
            ContextMenu menu = page.PostMenu!;
            Require(menu.Items.OfType<MenuItem>().First().Header as string == Texts.T("post.copy.selection"), $"The selection Copy has the wrong label in the {where}.");
            SmokeActionClick.Execute(menu.Items.OfType<MenuItem>().First());
            await Program.WaitUntilAsync(() => copied != null);
            Require(copied == expected, $"The selection Copy copied something other than the selection in the {where}.");
            menu.Close();
        }
        finally { page.CopyTextForTests = null; }

        // The reader's find by key: inside the reader it finds in the thread, in the results list the key stays with the archive search.
        KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        Control bar = page.FindControl<Control>("ReaderFindBar")!;
        Control query = page.FindControl<Control>("ReaderFindQuery")!;
        body.SelectionStart = body.SelectionEnd = 0;
        KeyEventArgs find = Press(body, Key.F, primary);
        await FlushAsync();
        Require(find.Handled && bar.IsVisible && query.IsKeyboardFocusWithin, $"The find key from a post's text did not open the thread find in the {where}.");
        SmokeActionClick.Execute(page.FindControl<Button>("ReaderFindClose")!);
        await FlushAsync();
        Require(!bar.IsVisible, $"The thread find did not close in the {where}.");
        body.SelectionStart = 0; body.SelectionEnd = 4;
        find = Press(body, Key.F, primary);
        await FlushAsync();
        Require(find.Handled && bar.IsVisible, $"A text selection held the find key back in the {where}.");
        SmokeActionClick.Execute(page.FindControl<Button>("ReaderFindClose")!);
        await FlushAsync();

        if (!large)
        {
            // The embedded reader shares the page with the results list: there the key stays with the archive search.
            ListBox threads = page.FindControl<ListBox>("ArchiveThreads")!;
            Require(threads.IsEffectivelyVisible, $"The results list is not shown beside the {where}.");
            Press(threads, Key.F, primary);
            await FlushAsync();
            Require(!bar.IsVisible && page.FindControl<Control>("ArchiveSearchText")!.IsKeyboardFocusWithin,
                $"The find key in the results list did not keep focusing the archive search in the {where}.");
            body.Focus();
        }
        else
        {
            // The large reader covers the whole archive page (ReaderHost spans the workspace rows), so the results list and the archive search
            // are not reachable there; the shell never focuses the archive search while it is open. Its sidebar is part of the reader (RD-02):
            // the key there finds in the thread too.
            Require(page.IsMediaSidebarShown && page.MediaSidebar is { IsEffectivelyVisible: true },
                $"The media sidebar is not shown in the {where}, so the find key from it cannot be checked.");
            Control sidebar = page.MediaSidebar!;
            Control sidebarFocus = sidebar.GetSelfAndVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Focusable && control.IsEffectivelyVisible && control is not TextBox) ?? sidebar;
            find = Press(sidebarFocus, Key.F, primary);
            await FlushAsync();
            Require(find.Handled && bar.IsVisible && query.IsKeyboardFocusWithin, $"The find key from the media sidebar did not open the thread find in the {where}.");
            SmokeActionClick.Execute(page.FindControl<Button>("ReaderFindClose")!);
            await FlushAsync();
            Require(!bar.IsVisible, $"The thread find did not close after the sidebar check in the {where}.");
            body.Focus();
        }
        Console.WriteLine($"SMOKE reader pointer and find key ({where}): press selects the post, right-click opens the post menu with Copy for a selection, Primary+F finds inside the reader{(large ? " and its media sidebar" : " and the list keeps the archive search")}.");
    }

    // The menu order as ids ("-" for a separator), opened by a context request raised on the control.
    private static string[] OpenMenu(ArchivePage page, Control source, string what)
    {
        ContextRequestedEventArgs request = new() { RoutedEvent = Control.ContextRequestedEvent };
        source.RaiseEvent(request);
        Require(request.Handled && page.PostMenu is { IsOpen: true }, $"The context request did not open the post menu {what}.");
        return page.PostMenu!.Items.OfType<Control>().Select(item => item is Separator ? "-" : item.Tag as string ?? "").ToArray();
    }

    private static void PressAndRelease(Control target)
    {
        Visual root = TopLevel.GetTopLevel(target) ?? target;
        Point at = target.TranslatePoint(new Point(2, 2), root) ?? default;
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        target.RaiseEvent(new PointerPressedEventArgs(target, pointer, root, at, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, root, at, 1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        pointer.Capture(null);
    }

    private static KeyEventArgs Press(Control control, Key key, KeyModifiers modifiers)
    {
        control.Focus();
        KeyEventArgs args = new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
        control.RaiseEvent(args);
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers });
        return args;
    }

    private static async Task FlushAsync() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded); await Task.Delay(20); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
