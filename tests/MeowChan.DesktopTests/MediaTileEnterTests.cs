using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// P2-3 (goal delta 16 Nov 2026): a real Enter on a focused media tile is raised on the tile's own ListBoxItem, the route the keyboard takes. Avalonia 12 lets the
// ListBoxItem consume Enter/Space for its own selection, so the page's bubble handler must not be the only way to open the gallery. The page is built for real
// (MediaPage, its own grid, its own tile template); only the list's template and the item container's template are the minimum the headless test needs, and
// the key is raised on the container exactly like the focused item would receive it.
[Collection("Booru exact navigation")]
public sealed class MediaTileEnterTests
{
    private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void EnterOnTheFocusedTileContainerOpensTheGalleryOnceAtThatTile() => Run(fixture =>
    {
        fixture.Grid.SelectedIndex = 1;
        fixture.Opened.Clear();

        fixture.Press(fixture.Container(1), Key.Enter);

        Assert.True(fixture.LastHandled, "The Enter on the tile container is consumed by the page.");
        (IReadOnlyList<MediaLibraryEntry> window, int start) = Assert.Single(fixture.Opened);
        Assert.Same(fixture.Entries[1], window[start]);
        Assert.Equal(1, fixture.Grid.Selection.Count);
    });

    [Fact]
    public void EnterOnATileOfAMultiSelectionOpensTheSelectionGalleryNotTheSingleTile() => Run(fixture =>
    {
        fixture.Grid.Selection.SelectRange(0, 1);
        fixture.Opened.Clear();

        fixture.Press(fixture.Container(1), Key.Enter);

        (IReadOnlyList<MediaLibraryEntry> window, int start) = Assert.Single(fixture.Opened);
        Assert.Equal(1, start);
        Assert.Same(fixture.Entries[1], window[start]);
        Assert.Equal(new[] { fixture.Entries[0], fixture.Entries[1] }, window);
        Assert.Equal(2, fixture.Grid.Selection.Count);
    });

    [Fact]
    public void EnterOnAFocusedButUnselectedTileMakesItTheSelectionAndOpensIt() => Run(fixture =>
    {
        fixture.Grid.SelectedIndex = 0; // Ctrl+arrow moves the focus and leaves the selection where it was
        fixture.Opened.Clear();

        fixture.Press(fixture.Container(2), Key.Enter);

        (IReadOnlyList<MediaLibraryEntry> window, int start) = Assert.Single(fixture.Opened);
        Assert.Same(fixture.Entries[2], window[start]);
        Assert.Equal(new[] { 2 }, fixture.Grid.Selection.SelectedIndexes.ToArray());
    });

    [Fact]
    public void EnterRaisedOnTheListRootWithTheTileAsSourceStillOpensItBecauseTheBubbleBranchStays() => Run(fixture =>
    {
        // The route the smoke used to take around the item (the list's own Enter branch): it must keep working for a list-sourced key.
        fixture.Grid.SelectedIndex = 1;
        fixture.Opened.Clear();

        fixture.Grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = fixture.Container(1) });
        for (int attempt = 0; attempt < 50; attempt++) Dispatcher.UIThread.RunJobs();

        (IReadOnlyList<MediaLibraryEntry> window, int start) = Assert.Single(fixture.Opened);
        Assert.Same(fixture.Entries[1], window[start]);
    });

    [Fact]
    public void ATunnelHandlerRegisteredLaterThatHandlesEnterKeepsTheTileFromOpening() => Run(fixture =>
    {
        // Tunnel handlers of one element run in reverse registration order: the context-menu, tile-size and panel handlers are registered after the Enter one
        // and run before it, so a key they handled is not opened as a tile.
        fixture.Grid.AddHandler(InputElement.KeyDownEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
        fixture.Grid.SelectedIndex = 1;
        fixture.Opened.Clear();

        fixture.Press(fixture.Container(1), Key.Enter);

        Assert.Empty(fixture.Opened);
    });

    [Fact]
    public void EnterOnTheStarOrATagChipOpensNothing() => Run(fixture =>
    {
        fixture.Grid.SelectedIndex = 1;
        fixture.Opened.Clear();
        Control tile = fixture.Container(1);
        ToggleButton star = tile.GetLogicalDescendants().OfType<ToggleButton>().Single(button => button.Name == "MediaTileFavoriteToggle");
        Button chip = tile.GetLogicalDescendants().OfType<Button>().First(button => button.Name == "MediaTileTag");

        fixture.Press(star, Key.Enter);
        fixture.Press(chip, Key.Enter);

        Assert.Empty(fixture.Opened);
    });

    [Fact]
    public void EnterWithAModifierAndOtherKeysOpenNothing() => Run(fixture =>
    {
        fixture.Grid.SelectedIndex = 1;
        fixture.Opened.Clear();

        fixture.Press(fixture.Container(1), Key.Enter, KeyModifiers.Control);
        fixture.Press(fixture.Container(1), Key.Enter, KeyModifiers.Alt);
        fixture.Press(fixture.Container(1), Key.Space);

        Assert.Empty(fixture.Opened);
    });

    // Audit 16.11.2026: "Open in gallery" (gallery.open) lists Shift+Enter for the Media page, and the tile only knew a plain Enter: the key did nothing.
    [Fact]
    public void ShiftEnterOnTheFocusedTileOpensTheGalleryLikeEnterBecauseGalleryOpenIsBoundThere() => Run(fixture =>
    {
        fixture.Grid.SelectedIndex = 1;
        fixture.Opened.Clear();

        fixture.Press(fixture.Container(1), Key.Enter, KeyModifiers.Shift);

        Assert.True(fixture.LastHandled);
        (IReadOnlyList<MediaLibraryEntry> window, int start) = Assert.Single(fixture.Opened);
        Assert.Same(fixture.Entries[1], window[start]);
    });

    [Fact]
    public void TheOpenKeysComeFromTheBindingsNotFromAFixedEnter() => Run(fixture =>
    {
        // A person who gave "Open selected media" another key and freed Enter: the new key opens, Enter does not (a fixed plain-Enter test ignored the binding).
        EffectiveShortcutBindings original = ShortcutService.Current;
        FieldInfo current = typeof(ShortcutService).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            current.SetValue(null, EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("media.open", [ShortcutGesture.Parse("Alt+O")]), ShortcutPlatform.Linux));
            fixture.Grid.SelectedIndex = 1;
            fixture.Opened.Clear();
            fixture.Press(fixture.Container(1), Key.Enter);
            Assert.Empty(fixture.Opened);
            fixture.Press(fixture.Container(1), Key.O, KeyModifiers.Alt);
            Assert.Single(fixture.Opened);
        }
        finally { current.SetValue(null, original); }
    });

    [Fact]
    public void EnterOnATileWhoseFileIsNotSavedSaysSoInsteadOfDoingNothing() => Run(fixture =>
    {
        fixture.Grid.SelectedIndex = Fixture.MissingIndex;
        fixture.Opened.Clear();

        fixture.Press(fixture.Container(Fixture.MissingIndex), Key.Enter);

        Assert.Empty(fixture.Opened);
        Assert.Equal(MeowChan.Desktop.MediaActions.MediaMenuReasons.NotSaved, fixture.SelectionNote);
    });

    [Fact]
    public void ADoubleClickOnATileWhoseFileIsNotSavedSaysSoToo() => Run(fixture =>
    {
        fixture.Opened.Clear();
        MediaLibraryTileRow row = (MediaLibraryTileRow)fixture.Grid.ItemsSource!.Cast<object>().ElementAt(Fixture.MissingIndex);

        fixture.Page.OpenTileAsync(row).GetAwaiter().GetResult();

        Assert.Empty(fixture.Opened);
        Assert.Equal(MeowChan.Desktop.MediaActions.MediaMenuReasons.NotSaved, fixture.SelectionNote);
    });

    internal static void Run(Action<Fixture> test) => BooruComponentDispatcher.Run(() =>
    {
        // The media panel the selection opens creates inline video slots, which ask the platform for a cursor.
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<Avalonia.Platform.ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        using Fixture fixture = new();
        test(fixture);
    });

    internal sealed class Fixture : IDisposable
    {
        private static readonly FuncControlTemplate<ContentControl> BareContentTemplate = new((_, scope) =>
        {
            ContentPresenter presenter = new() { Name = "PART_ContentPresenter" };
            presenter[!ContentPresenter.ContentProperty] = new TemplateBinding(ContentControl.ContentProperty);
            presenter[!ContentPresenter.ContentTemplateProperty] = new TemplateBinding(ContentControl.ContentTemplateProperty);
            presenter.RegisterInNameScope(scope);
            return presenter;
        });

        private readonly MediaPage _page;
        private readonly Root _root = new();

        internal bool LastHandled { get; private set; }
        internal MediaLibraryEntry[] Entries { get; }
        internal MediaPage Page => _page;
        internal Control RootControl => _root;
        internal MediaLibraryTileRow[] Rows { get; }
        internal ReaderMediaSidebar? PanelView { get; private set; }
        internal string? SelectionNote => ((TextBlock)typeof(MediaPage).GetField("_selectionNote", InstanceNonPublic)!.GetValue(_page)!) is { IsVisible: true } note ? note.Text : null;
        /// <summary>The position of the tile whose file is not saved (shown with "missing files").</summary>
        internal const int MissingIndex = 3;
        /// <summary>A medium saved from a thread (no booru post), the one the tagging queue accepts.</summary>
        internal const int LocalIndex = 2;
        internal List<(IReadOnlyList<MediaLibraryEntry> Window, int Start)> Opened { get; } = new();
        internal ListBox Grid => _page.Grid;

        internal Fixture()
        {
            _page = new MediaPage(_ => throw new InvalidOperationException("The tile Enter fixture must not open archive data."));
            // Tile 1 belongs to a post of a thread (show in thread), tile 3 is a missing file (nothing to open).
            Entries = Enumerable.Range(0, 4).Select(index => index == 1 ? Entry("1") with { PostId = "101" } : index == LocalIndex ? Entry("2") with { BooruPostId = null } : Entry(index.ToString(), index == MissingIndex ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present)).ToArray();
            MediaLibraryTile[] tiles = Entries.Select(entry => new MediaLibraryTile(entry, [entry])).ToArray();
            MediaLibraryTileRow[] rows = Rows = tiles.Select((tile, index) => new MediaLibraryTileRow(tile, index)).ToArray();
            _page.OpenGallery = (window, start) => { Opened.Add((window, start)); return Task.CompletedTask; };
            Set("_tiles", tiles);
            Set("_shown", Entries);
            typeof(MediaPage).GetProperty(nameof(MediaPage.PresentPositions), InstanceNonPublic | BindingFlags.Public)!
                .SetValue(_page, MediaLibraryView.PresentIndex(Entries));

            // The list and its containers need a template to exist without a theme; both are the bare presenters of the product's theme.
            ListBox grid = Grid;
            // No layout pass: text measuring needs a platform this test does not start. A plain stack panel makes its containers when the presenter is
            // attached, the route of the key is the visual parent chain, and none of it depends on the tile panel's geometry.
            var panel = new FuncTemplate<Panel?>(() => new StackPanel());
            grid.Template = new FuncControlTemplate<ListBox>((_, scope) =>
            {
                ItemsPresenter presenter = new() { Name = "PART_ItemsPresenter", ItemsPanel = panel };
                presenter.RegisterInNameScope(scope);
                return presenter;
            });
            _root.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.TemplateProperty, BareContentTemplate)
                }
            });
            _root.Styles.Add(new Style(selector => selector.OfType<UserControl>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.TemplateProperty, BareContentTemplate)
                }
            });
            _root.Child = _page;
            grid.ItemsSource = rows;
            grid.ApplyTemplate();
            foreach (ItemsPresenter presenter in grid.GetVisualDescendants().OfType<ItemsPresenter>()) presenter.ApplyTemplate();
            ListBoxItem[] items = grid.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
            Assert.Equal(rows.Length, items.Length);
            // Without a layout pass every level of the tile (container, tile view) is templated by hand, outermost first.
            foreach (ListBoxItem item in items)
                for (int depth = 0; depth < 4; depth++)
                {
                    foreach (TemplatedControl control in item.GetSelfAndVisualDescendants().OfType<TemplatedControl>().ToArray()) control.ApplyTemplate();
                    foreach (ContentPresenter content in item.GetVisualDescendants().OfType<ContentPresenter>().ToArray()) content.UpdateChild();
                }
        }

        /// <summary>Presents the given panel row independently of grid selection and returns its visible "Open in gallery" button.</summary>
        internal Control ShowPanelOn(int index)
        {
            MediaLibraryTileRow row = Rows[index];
            // Seed the shown row so the layout can attach the panel without starting an image load.
            Set("_panelShown", true);
            Set("_panelRow", row);
            Set("_panelWanted", row);
            _page.ApplySavedPanelWidth(_page.PageLayout.PanelWidth);
            ReaderMediaSidebar panel = PanelView = Assert.IsType<ReaderMediaSidebar>(_page.Panel);
            typeof(MediaPage).GetMethod("DescribePanel", InstanceNonPublic)!.Invoke(_page, [panel, row]);

            // The action section exists only for a described medium. Materialize the presenters
            // so the real key route goes through the sidebar and the page without a layout pass.
            _page.ApplyTemplate();
            _page.Presenter!.UpdateChild();
            ScrollViewer details = panel.GetVisualDescendants().OfType<ScrollViewer>().Single(view => view.Name == "ReaderMediaSidebarDetails");
            MaterializeContent(details);
            foreach (ContentControl section in Assert.IsAssignableFrom<Panel>(details.Content).Children.OfType<ContentControl>())
                MaterializeContent(section);
            Assert.Contains(panel, panel.Open.GetVisualAncestors());
            Assert.Contains(_page, panel.Open.GetVisualAncestors());
            Assert.True(panel.Open.IsEffectivelyVisible && panel.Open.IsEffectivelyEnabled && panel.Open.Focusable);
            return panel.Open;
        }

        private static void MaterializeContent(ContentControl control)
        {
            control.Template = BareContentTemplate;
            control.ApplyTemplate();
            control.Presenter!.UpdateChild();
        }

        internal Control Container(int index) => Grid.ContainerFromIndex(index)
            ?? throw new InvalidOperationException("The tile " + index + " has no container.");

        // The key goes to the control that has the focus, exactly as Avalonia's input manager raises it.
        internal void Press(Control source, Key key, KeyModifiers modifiers = KeyModifiers.None)
        {
            KeyEventArgs args = new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = source };
            source.RaiseEvent(args);
            LastHandled = args.Handled;
            for (int attempt = 0; attempt < 50; attempt++) Dispatcher.UIThread.RunJobs();
        }

        private void Set(string field, object? value) => typeof(MediaPage).GetField(field, InstanceNonPublic)!.SetValue(_page, value);

        public void Dispose()
        {
            _root.Child = null;
            _page.EndLifetime();
        }

        private static MediaLibraryEntry Entry(string post, MediaLibraryPresence presence = MediaLibraryPresence.Present) => new(BooruMediaIdentity.Thread(post), "Post " + post, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, null, null, null, "/synthetic/not-opened",
            "asset-" + post, new string('b', 64), 7, BooruMediaIdentity.MediaIdentifier(post), "original", post + ".jpg",
            SavedMediaKind.Image, presence, true, false) { BooruPostId = post };

        private sealed class Root : Decorator, ILogicalRoot;
    }
}
