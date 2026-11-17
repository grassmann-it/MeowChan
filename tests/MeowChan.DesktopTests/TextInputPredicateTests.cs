using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.MediaActions;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class TextInputPredicateTests
{
    // Non-text widgets must not migrate into the shared definition just because one
    // consumer protects their native keys. Each consumer's extras are tested below.
    public static IEnumerable<object[]> Controls()
    {
        foreach (string name in new[] { "text", "readonly", "search", "number", "combo", "autocomplete", "identity" })
            yield return new object[] { name, true };
        foreach (string name in new[] { "slider", "scrollbar", "menu", "button", "toggle", "selectable", "tab", "list", "grid", "header", "splitter", "thumb", "switch", "calendar", "border" })
            yield return new object[] { name, false };
    }

    private static Control Create(string name) => name switch
    {
        "text" => new TextBox(), "readonly" => new TextBox { IsReadOnly = true },
        "search" => new SearchBox(), "number" => new NumericUpDown(), "combo" => new ComboBox(),
        "autocomplete" => new Ursa.Controls.AutoCompleteBox(), "identity" => new IdentitySuggestionBox(),
        "slider" => new Slider(), "scrollbar" => new ScrollBar(), "menu" => new MenuItem(),
        "button" => new Button(), "toggle" => new ToggleButton(), "selectable" => new SelectableTextBlock(),
        "tab" => new TabItem(), "list" => new ListBox(), "grid" => new DataGrid(),
        "header" => new DataGridColumnHeader(), "splitter" => new GridSplitter(), "thumb" => new Thumb(),
        "switch" => new ToggleSwitch(), "calendar" => new CalendarDatePicker(), _ => new Border()
    };

    [Theory, MemberData(nameof(Controls))]
    public void EveryConsumerUsesTheSameTextCoreWithOnlyItsDocumentedExtras(string name, bool text)
    {
        Control control = Create(name);
        Assert.Equal(text, PointerFocusReclaim.IsTextInput(control));
        ListHost posts = new();
        posts.Mount(control);
        KeyEventArgs right = new() { Source = control, Key = Key.Right };
        // A neutral key isolates the always-native core from list/slider navigation.
        Assert.Equal(text || name is "scrollbar" or "menu", CommandRouter.IsNativeInput(control, Key.R));
        Assert.Equal(text || name is "slider" or "scrollbar" or "menu", ArchivePage.KeyBelongsToNativeEditor(right));
        Assert.Equal(text || name is "slider" or "selectable", MediaMenuPresenter.IsNativeTextSource(control));
        // ToggleSwitch inherits Button: it remains a native widget, never a text input.
        Assert.Equal(text || name is "button" or "toggle" or "switch" or "selectable" or "menu" or "slider" or "tab",
            MainWindow.IsNativeShortcutSource(control));
        Assert.Equal(!(text || name is "button" or "toggle" or "switch" or "selectable" or "menu" or "slider" or "tab" or "header"),
            MainWindow.IsListCommandSource(control, posts));
        Assert.Equal(!(text || name is "selectable" or "slider" or "menu" or "tab" or "header" or "scrollbar"),
            ArchivePage.IsRowButtonPath(new object[] { control, new Button(), new ListBoxItem(), posts }, posts));
        Assert.Equal(text || name is "button" or "toggle" or "switch" or "selectable" or "slider" or "scrollbar" or "menu" or "header" or "splitter" or "thumb" or "calendar",
            ItemActivation.IsOwnedByInteractivePart(control, posts));
        Assert.Equal(text || name is "button" or "toggle" or "switch" or "selectable" or "menu" or "slider" or "scrollbar" or "tab" or "list" or "grid",
            ArchivePage.ReaderMediaBelongsToNativeControl(control, posts));
        Assert.Equal(text || name is "menu" or "slider" or "scrollbar" or "tab" or "list" or "grid" or "header",
            ArchivePage.ReaderArrowBelongsToNativeControl(control, Key.Down, posts));
        Assert.Equal(text && name != "combo", SettingsEditor.SlashBelongsToEditor(control));
        Assert.Equal(name is "autocomplete" or "identity", RulesPage.EscapeBelongsToSuggestions(control));

        // The reader-owned Alt+Shift arrow exception must stop before any nested editor.
        right = new KeyEventArgs { Source = control, Key = Key.Right, KeyModifiers = KeyModifiers.Alt | KeyModifiers.Shift };
        Assert.Equal(text || name is "slider" or "scrollbar" or "menu" or "tab" or "list" or "grid" ? 0 : -1,
            ReaderSidebarStepKeys.Direction(right, posts, null, id => id == "reader.sidebar.previous-media"));

        // Test the nearer-node transport override, not just generic Matches(). A mounted
        // text input must stop the walk before it reaches the explicitly owned transport.
        posts.Unmount(control);
        SliderHost seek = new();
        seek.Mount(control);
        right = new KeyEventArgs { Source = control, Key = Key.Right };
        if (text)
            Assert.False(CommandBindings.MatchesGalleryTransport(right, "gallery.next", seek, new Slider()));
        // Resolve must also refuse explicit reclaim opt-in for a text input.
        if (text)
        {
            PointerFocusReclaim.SetReclaims(control, true);
            Assert.Null(PointerFocusReclaim.Resolve(seek, control));
        }
    }

    [Fact]
    public void OpenSelectorsAndEditorTemplateChildrenKeepTheNarrowSlashAndEscapeRules()
    {
        ComboBox combo = new() { IsDropDownOpen = true };
        Assert.True(SettingsEditor.SlashBelongsToEditor(combo));
        Assert.True(RulesPage.EscapeBelongsToSuggestions(combo));
        TextHost field = new();
        Border part = new();
        field.Mount(part);
        ListHost posts = new();
        posts.Mount(field);
        Assert.True(CommandRouter.IsNativeInput(part, Key.Delete));
        Assert.True(MainWindow.IsNativeShortcutSource(part));
        Assert.False(MainWindow.IsListCommandSource(part, posts));
        Assert.True(ArchivePage.KeyBelongsToNativeEditor(new KeyEventArgs { Source = part, Key = Key.PageDown }));
        Assert.True(ArchivePage.ReaderMediaBelongsToNativeControl(part, posts));
        Assert.True(ArchivePage.ReaderArrowBelongsToNativeControl(part, Key.Down, posts));
        Assert.True(MediaMenuPresenter.IsNativeTextSource(part));
        Assert.True(ItemActivation.IsOwnedByInteractivePart(part, posts));
        Assert.True(SettingsEditor.SlashBelongsToEditor(part));
        Assert.False(RulesPage.EscapeBelongsToSuggestions(part)); // ordinary editor Escape still dismisses wizard
        Assert.Null(PointerFocusReclaim.Resolve(posts, part));
    }

    [Fact]
    public void OwnedBoundariesStillAdmitTheirOwnKeysAndBareTransportIsNotBlocked()
    {
        ListHost posts = new();
        Assert.True(MainWindow.IsListCommandSource(posts, posts));
        Assert.False(ArchivePage.ReaderMediaBelongsToNativeControl(posts, posts));
        Assert.False(ArchivePage.ReaderArrowBelongsToNativeControl(posts, Key.Down, posts));
        Assert.True(ArchivePage.ReaderArrowBelongsToNativeControl(posts, Key.PageDown, posts));
        SliderHost seek = new();
        Border decoration = new(); seek.Mount(decoration);
        Assert.True(CommandBindings.MatchesGalleryTransport(new KeyEventArgs { Source = decoration, Key = Key.Right }, "gallery.next", seek, new Slider()));
    }

    private sealed class ListHost : ListBox
    {
        internal void Mount(Control child) => VisualChildren.Add(child);
        internal void Unmount(Control child) => VisualChildren.Remove(child);
    }
    private sealed class SliderHost : Slider
    {
        internal void Mount(Control child) => VisualChildren.Add(child);
    }
    private sealed class TextHost : TextBox
    {
        internal void Mount(Visual child) => VisualChildren.Add(child);
    }
}
