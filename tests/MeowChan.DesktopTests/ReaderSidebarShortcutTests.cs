using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class ReaderSidebarShortcutTests
{
    // No window/renderer is needed for the source ancestry policy. These remain real ListBox/ListBoxItem
    // types, so the generic CommandRouter native-input check still sees a selecting control.
    private sealed class List : ListBox
    {
        internal void Mount(Control child) => VisualChildren.Add(child);
    }

    private static int Route(Control source, Key key, KeyModifiers modifiers, ListBox posts, ListBox? strip,
        ShortcutBindingsSection? saved = null, ShortcutPlatform platform = ShortcutPlatform.Linux)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(saved ?? new(), platform);
        KeyEventArgs e = new() { Key = key, KeyModifiers = modifiers, Source = source };
        using (EffectiveShortcutBindings.EnterKeyContext("reader"))
            return ReaderSidebarStepKeys.Direction(e, posts, strip, id => bindings.Matches(e, id));
    }

    // The tunnel changes only event ordering. Every owned family must still use effective bindings and
    // the shared native-editor guard (the source contracts below/RateAndTagSurfaceTests pin the wiring).
    public static IEnumerable<object[]> OwnedCommands() => new[]
    {
        "favorites.add", "favorites.remove", "favorites.toggle", "media.tags.edit", "media.export-original",
        "media.show-in-folder", "media.copy-path", "media.details", "gallery.show-in-thread", "post.hide", "post.unhide",
        "reader.view.hide-media", "reader.view.media-only", "reader.view.conversation", "reader.view.textless-large",
        "reader.view.focused-enlarge", "reader.view.hover-enlarge", "reader.sidebar.toggle", "reader.quick-settings",
        "reader.post-filters", "archive.media-view"
    }.Concat(ReaderVideoKeys.Commands.Select(command => command.Id)).Select(id => new object[] { id });

    [Theory, MemberData(nameof(OwnedCommands))]
    public void ConfiguredOwnedCommandsMatchOnSelectionControlsButNeverInEditors(string id)
    {
        FieldInfo current = typeof(ShortcutService).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!;
        EffectiveShortcutBindings original = ShortcutService.Current;
        try
        {
            ShortcutBindingsSection saved = new ShortcutBindingsSection().Set(id, [ShortcutGesture.Parse("J")]);
            EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(saved, ShortcutPlatform.Linux);
            Assert.Empty(bindings.Issues);
            current.SetValue(null, bindings);
            using (EffectiveShortcutBindings.EnterKeyContext("reader"))
            {
                foreach (Control source in new Control[] { new ListBox(), new ListBoxItem(), new DataGridRow(), new Button() })
                {
                    KeyEventArgs press = new() { Key = Key.J, Source = source };
                    Assert.True(CommandBindings.Matches(press, id), id + " on " + source.GetType().Name);
                    Assert.False(press.Handled); // Matching alone never consumes a key or invokes a command.
                }
                foreach (Control source in new Control[] { new TextBox(), new ComboBox(), new NumericUpDown() })
                    Assert.False(CommandBindings.Matches(new KeyEventArgs { Key = Key.J, Source = source }, id));
                Assert.False(CommandBindings.Matches(new KeyEventArgs { Key = Key.Z, Source = new ListBoxItem() }, id));
                current.SetValue(null, EffectiveShortcutBindings.Build(saved.Set(id, []), ShortcutPlatform.Linux));
                Assert.False(CommandBindings.Matches(new KeyEventArgs { Key = Key.J, Source = new ListBoxItem() }, id));
            }
        }
        finally { current.SetValue(null, original); }
    }

    [Fact]
    public void AHandledKeyCannotStepTwiceAndUnboundLettersRemainTypeahead()
    {
        List posts = new();
        ListBoxItem source = new(); posts.Mount(source);
        KeyEventArgs press = new() { Source = source, Key = Key.Right, KeyModifiers = KeyModifiers.Alt | KeyModifiers.Shift, Handled = true };
        Assert.Equal(0, ReaderSidebarStepKeys.Direction(press, posts, null, _ => throw new InvalidOperationException("A handled key must not be matched twice.")));
        Assert.Equal(0, Route(source, Key.Z, KeyModifiers.None, posts, null));
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void TheTwoOwnedListsYieldOnlyTheBoundMediaStepChord(ShortcutPlatform platform)
    {
        List posts = new(), strip = new();
        ListBoxItem post = new(), thumbnail = new();
        posts.Mount(post); strip.Mount(thumbnail);
        foreach (Control source in new Control[] { posts, post, strip, thumbnail, new Button(), new Border() })
        {
            Assert.Equal(-1, Route(source, Key.Left, KeyModifiers.Alt | KeyModifiers.Shift, posts, strip, platform: platform));
            Assert.Equal(1, Route(source, Key.Right, KeyModifiers.Alt | KeyModifiers.Shift, posts, strip, platform: platform));
        }
        // The scoped exception did not weaken the shared router for the rest of the application.
        Assert.True(CommandRouter.IsNativeInput(post, Key.Right));
        Assert.True(CommandRouter.IsNativeInput(thumbnail, Key.Left));
    }

    [Theory]
    [InlineData(KeyModifiers.None)]
    [InlineData(KeyModifiers.Shift)]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(KeyModifiers.Alt)]
    public void SavedBindingsCannotStealLocalListArrows(KeyModifiers modifiers)
    {
        List posts = new(), strip = new();
        ListBoxItem item = new();
        strip.Mount(item);
        ShortcutBindingsSection saved = new ShortcutBindingsSection().Set("reader.sidebar.next-media",
            [new ShortcutGesture("Right", modifiers.ToString().Split(", ", StringSplitOptions.RemoveEmptyEntries).Where(m => m != "None").ToArray())]);
        Assert.Equal(0, Route(item, Key.Right, modifiers, posts, strip, saved));
    }

    [Theory]
    [InlineData("TextBox")]
    [InlineData("ComboBox")]
    [InlineData("NumericUpDown")]
    [InlineData("Slider")]
    [InlineData("ScrollBar")]
    [InlineData("MenuItem")]
    [InlineData("AutoCompleteBox")]
    [InlineData("SelectedText")]
    [InlineData("OtherList")]
    [InlineData("DataGrid")]
    public void EditorsAndNestedForeignSelectionControlsKeepTheMediaStepChord(string kind)
    {
        List posts = new(), strip = new();
        Control source = kind switch
        {
            "TextBox" => new TextBox(), "ComboBox" => new ComboBox(), "NumericUpDown" => new NumericUpDown(),
            "Slider" => new Slider(), "ScrollBar" => new Avalonia.Controls.Primitives.ScrollBar(), "MenuItem" => new MenuItem(),
            "AutoCompleteBox" => new Ursa.Controls.AutoCompleteBox(),
            "SelectedText" => new SelectableTextBlock { Text = "selected", SelectionStart = 0, SelectionEnd = 3 },
            "OtherList" => new ListBox(), "DataGrid" => new DataGrid(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        posts.Mount(source);
        Assert.Equal(0, Route(source, Key.Right, KeyModifiers.Alt | KeyModifiers.Shift, posts, strip));
    }

    [Fact]
    public void AnUnrelatedListDoesNotYieldAndARebindingDoesNotLeaveTheOldChordActive()
    {
        List posts = new(), strip = new(), unrelated = new();
        ListBoxItem otherItem = new(), ownItem = new();
        unrelated.Mount(otherItem); posts.Mount(ownItem);
        Assert.Equal(0, Route(otherItem, Key.Right, KeyModifiers.Alt | KeyModifiers.Shift, posts, strip));
        ShortcutBindingsSection saved = new ShortcutBindingsSection().Set("reader.sidebar.next-media", [ShortcutGesture.Parse("J")]);
        Assert.Equal(0, Route(ownItem, Key.Right, KeyModifiers.Alt | KeyModifiers.Shift, posts, strip, saved));
        Assert.Equal(1, Route(ownItem, Key.J, KeyModifiers.None, posts, strip, saved));
        Assert.Equal(0, Route(new TextBox(), Key.J, KeyModifiers.None, posts, strip, saved));
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    public void SavedActivationBindingsLeaveTheListRootLocalToo(Key key)
    {
        List posts = new();
        ShortcutBindingsSection saved = new ShortcutBindingsSection().Set("reader.sidebar.next-media", [ShortcutGesture.Parse(key.ToString())]);

        // Avalonia can report the ListBox itself as the source when focus is on its
        // chrome. The media-step route must not rely on a realized ListBoxItem to
        // preserve activation ownership.
        Assert.Equal(0, Route(posts, key, KeyModifiers.None, posts, null, saved));
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    public void SavedActivationBindingsLeaveRowsAndButtonsLocal(Key key)
    {
        List posts = new();
        ListBoxItem item = new(); posts.Mount(item);
        ShortcutBindingsSection saved = new ShortcutBindingsSection().Set("reader.sidebar.next-media", [ShortcutGesture.Parse(key.ToString())]);
        Assert.Equal(0, Route(item, key, KeyModifiers.None, posts, null, saved));
        Assert.Equal(0, Route(new Button(), key, KeyModifiers.None, posts, null, saved));
    }

    [Fact]
    public void ReaderViewOptionsRespectNativeSelectionNavigation()
    {
        string readerView = Source("Desktop/Views/Archive/ArchivePage.ReaderView.cs");
        int start = readerView.IndexOf("private void OnReaderViewKeyDown", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string handler = readerView[start..];
        Assert.Contains("CommandRouter.IsNativeInput(e.Source, e.Key)", handler);

        // This is the native-input family that a user binding must never steal.
        Assert.True(CommandRouter.IsNativeInput(new ListBox(), Key.Up));
        Assert.True(CommandRouter.IsNativeInput(new ListBox(), Key.Down));
        Assert.True(CommandRouter.IsNativeInput(new ListBox(), Key.Home));
        Assert.True(CommandRouter.IsNativeInput(new ListBox(), Key.PageDown));
    }

    [Fact]
    public void BothReaderHostsHaveAGuardedTunnelWithoutRelaxingTheShellRouter()
    {
        string sidebar = Source("Desktop/Views/Archive/ArchivePage.MediaSidebar.cs");
        Assert.Contains("pane.AddHandler(KeyDownEvent, OnMediaSidebarStepKey, RoutingStrategies.Tunnel);", sidebar);
        Assert.Contains("sidebar.AddHandler(KeyDownEvent, OnMediaSidebarStepKey, RoutingStrategies.Tunnel);", sidebar);
        int start = sidebar.IndexOf("private void OnMediaSidebarStepKey", StringComparison.Ordinal);
        string handler = sidebar[start..sidebar.IndexOf("internal void StepMediaSidebar", start, StringComparison.Ordinal)];
        Assert.Contains("CommandRouter.IsRoutingContextActive(owner, source)", handler);
        Assert.Contains("source.GetSelfAndVisualAncestors().Contains(owner)", handler);
        Assert.Contains("ShortcutService.IsCapturing", handler);
        Assert.Contains("_obscuredByGallery || IsReaderDialogOpen", handler);
        Assert.Contains("id => ShortcutService.Current.Matches(e, id)", handler);
        Assert.Contains("e.Handled = true;", handler);
        Assert.Contains("StepMediaSidebar(direction);", handler);
        Assert.True(sidebar.IndexOf("KeyContextHost.Install(sidebar", StringComparison.Ordinal)
            > sidebar.IndexOf("sidebar.AddHandler(KeyDownEvent, OnMediaSidebarStepKey", StringComparison.Ordinal));
    }

    [Fact]
    public void RatingCommandsAlsoOwnTheSidebarSiblingInTheLargeReader()
    {
        string rating = Source("Desktop/Views/Archive/ArchivePage.RatingCommands.cs");
        string sidebar = Source("Desktop/Views/Archive/ArchivePage.MediaSidebar.cs");

        // The large-reader sidebar is beside (not below) ArchiveReaderPane. A
        // pane-only registration can therefore disappear when focus is on the
        // sidebar's selected thumbnail or its header controls.
        Assert.Contains("RegisterRatingCommands(pane);", rating);
        Assert.Contains("if (_mediaSidebar is { } sidebar) RegisterRatingCommands(sidebar);", rating);
        Assert.Contains("if (_ratingCommands != null) RegisterRatingCommands(sidebar);", sidebar);
        Assert.Contains("_ratingCommands.Register(new(\"media.rating.edit\", owner", rating);
    }

    private static string Source(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../..", relative)));
}
