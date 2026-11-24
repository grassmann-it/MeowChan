using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using Semi.Avalonia.DataGrid;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class ContextGestureHarnessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RightClickOpensTheRealMenuForTheHitRow(bool useGrid) => Run(useGrid, fixture =>
    {
        ContextGestures.RightClick(fixture.RowHit(fixture.Second));

        Assert.Same(fixture.Second, fixture.Selected);
        Assert.Same(fixture.Second, Assert.Single(fixture.Compositions));
        Assert.Equal(1, fixture.PositionedRequests);
        AssertMenuAndActivate(fixture, fixture.Second);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ARequestAfterTheRightPressKeepsTheOriginalMenu(bool useGrid, bool releaseOnOtherRow) => Run(useGrid, fixture =>
    {
        ContextGestures.Press(fixture.RowHit(fixture.Second), MouseButton.Right);
        ContextMenu menu = Assert.IsType<ContextMenu>(fixture.Host.OpenMenu);
        Assert.True(fixture.Gate.IsPending);
        Assert.Equal(0, fixture.PositionedRequests);

        Row releaseRow = releaseOnOtherRow ? fixture.First : fixture.Second;
        ContextGestures.Release(fixture.RowHit(releaseRow), MouseButton.Right);

        Assert.Same(releaseRow, fixture.RightReleaseHit);
        Assert.Equal(1, fixture.PositionedRequests);
        Assert.Equal(1, fixture.ConsumedRequests);
        Assert.False(fixture.Gate.IsPending);
        Assert.Same(menu, fixture.Host.OpenMenu);
        Assert.Same(fixture.Second, fixture.Selected);
        Assert.Same(fixture.Second, Assert.Single(fixture.Compositions));
        AssertMenuAndActivate(fixture, fixture.Second);
    }, openOnPress: true);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MenuKeysOpenBelowTheControlAndUseTheFocusedRow(bool useGrid, bool shiftF10) => Run(useGrid, fixture =>
    {
        ShortcutService.Publish(new ShortcutBindingsSection().Set("menu.context-open", []));
        ContextGestures.RightClick(fixture.RowHit(fixture.First));
        fixture.Host.Close();

        Control focus = fixture.FocusableRowPart(fixture.Second);
        if (shiftF10) ContextGestures.ShiftF10(focus);
        else ContextGestures.MenuKey(focus);

        Assert.Equal(new[] { fixture.First, fixture.Second }, fixture.Compositions);
        Assert.Equal(PlacementMode.BottomEdgeAlignedLeft, fixture.Host.OpenMenu?.Placement);
        AssertMenuAndActivate(fixture, fixture.Second);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MiddleClickRequiresTheSameRowAndYieldsToInteractiveChildren(bool useGrid) => Run(useGrid, fixture =>
    {
        ContextGestures.Press(fixture.RowHit(fixture.First), MouseButton.Middle);
        ContextGestures.Release(fixture.RowHit(fixture.Second), MouseButton.Middle);
        Assert.Empty(fixture.MiddleActions);

        Button child = fixture.ChildButton(fixture.Second);
        ContextGestures.MiddleClick(child);
        ContextGestures.Press(fixture.RowHit(fixture.Second), MouseButton.Middle);
        ContextGestures.Release(child, MouseButton.Middle);
        Assert.Empty(fixture.MiddleActions);

        ContextGestures.MiddleClick(fixture.RowHit(fixture.Second));
        Assert.Same(fixture.Second, Assert.Single(fixture.MiddleActions));
        Assert.Same(fixture.First, fixture.Selected);
        Assert.Null(fixture.Host.OpenMenu);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoubleClickActivatesTheSelectedRowAndYieldsToAnInteractiveChild(bool useGrid) => Run(useGrid, fixture =>
    {
        ContextGestures.DoubleClick(fixture.RowHit(fixture.Second), new Point(12, 12));

        Assert.Same(fixture.Second, fixture.Selected);
        Assert.Same(fixture.Second, Assert.Single(fixture.PrimaryActions));

        ContextGestures.DoubleClick(fixture.ChildButton(fixture.First));
        Assert.NotEmpty(fixture.ChildActions);
        Assert.All(fixture.ChildActions, row => Assert.Same(fixture.First, row));
        Assert.Same(fixture.Second, Assert.Single(fixture.PrimaryActions));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointerModifiersReachTheRowIndependently(bool useGrid) => Run(useGrid, fixture =>
    {
        foreach ((RawInputModifiers raw, KeyModifiers routed) in new[]
        {
            (RawInputModifiers.Control, KeyModifiers.Control),
            (RawInputModifiers.Meta, KeyModifiers.Meta), // Cmd on macOS
            (RawInputModifiers.Shift, KeyModifiers.Shift),
            (RawInputModifiers.Alt, KeyModifiers.Alt) // Option on macOS
        })
        {
            ContextGestures.MiddleClick(fixture.RowHit(fixture.Second), modifiers: raw);
            Assert.Equal(routed, fixture.LastPressModifiers);
            Assert.Equal(routed, fixture.LastReleaseModifiers);
        }
        Assert.Equal(4, fixture.MiddleActions.Count);
        Assert.All(fixture.MiddleActions, row => Assert.Same(fixture.Second, row));
    });

    private static void AssertMenuAndActivate(Fixture fixture, Row target)
    {
        ContextMenu menu = Assert.IsType<ContextMenu>(fixture.Host.OpenMenu);
        Assert.Equal(target.Name, AutomationProperties.GetName(menu));
        IReadOnlyList<MenuProbe.Entry> entries = MenuProbe.Entries(menu);
        Assert.Collection(entries,
            open =>
            {
                Assert.Equal("gesture.open", open.Id);
                Assert.Equal(0, open.Section);
                Assert.True(open.Enabled);
                Assert.Null(open.DisabledReason);
                Assert.True(open.HasIcon("external-link"));
            },
            unavailable =>
            {
                Assert.Equal("gesture.unavailable", unavailable.Id);
                Assert.Equal(1, unavailable.Section);
                Assert.False(unavailable.Enabled);
                Assert.Equal(Fixture.DisabledReason, unavailable.DisabledReason);
                Assert.True(unavailable.HasIcon("copy"));
            });

        entries[0].Activate();
        Assert.Equal((entries[0].Id, target), Assert.Single(fixture.MenuActions));
    }

    private static void Run(bool useGrid, Action<Fixture> test, bool openOnPress = false) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        if (useGrid) Application.Current!.Styles.Add(new DataGridSemiTheme());
        using Fixture fixture = new(useGrid, openOnPress);
        test(fixture);
    });

    private sealed record Row(string Name);

    // The fixture binds spies to the production host, target resolver, gate and row activation helpers.
    private sealed class Fixture : IDisposable
    {
        internal const string DisabledReason = "This row cannot be copied.";

        private readonly Control _surface;
        private readonly Window _window;
        private readonly ContextMenuTarget _target;
        private readonly MiddleClickTracker _middle = new();
        private readonly bool _openOnPress;

        internal Row First { get; } = new("First row");
        internal Row Second { get; } = new("Second row");
        internal ContextMenuHost Host { get; }
        internal PressOpenedContextGate Gate { get; } = new();
        internal List<Row> Compositions { get; } = [];
        internal List<(string Id, Row Row)> MenuActions { get; } = [];
        internal List<Row> MiddleActions { get; } = [];
        internal List<Row> PrimaryActions { get; } = [];
        internal List<Row> ChildActions { get; } = [];
        internal int PositionedRequests { get; private set; }
        internal int ConsumedRequests { get; private set; }
        internal Row? RightReleaseHit { get; private set; }
        internal KeyModifiers LastPressModifiers { get; private set; }
        internal KeyModifiers LastReleaseModifiers { get; private set; }

        internal Row? Selected
        {
            get => (Row?)(_surface is DataGrid grid ? grid.SelectedItem : ((ListBox)_surface).SelectedItem);
            private set
            {
                if (_surface is DataGrid grid) grid.SelectedItem = value;
                else ((ListBox)_surface).SelectedItem = value;
            }
        }

        internal Fixture(bool useGrid, bool openOnPress)
        {
            _openOnPress = openOnPress;
            FuncDataTemplate<Row> template = new((row, _) => RowContent(row!));
            Row[] rows = [First, Second];
            if (useGrid)
            {
                DataGrid grid = new() { AutoGenerateColumns = false, IsReadOnly = true, ItemsSource = rows, HeadersVisibility = DataGridHeadersVisibility.None };
                grid.Columns.Add(new DataGridTemplateColumn { CellTemplate = template, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
                grid.DoubleTapped += (_, e) =>
                {
                    if (ItemActivation.IsItemDoubleClick(e, grid) && Selected is { } row) PrimaryActions.Add(row);
                };
                _surface = grid;
            }
            else
            {
                ListBox list = new() { ItemsSource = rows, ItemTemplate = template };
                ListRowActions.OnDoubleClick(list, () => PrimaryActions.Add(Assert.IsType<Row>(Selected)));
                _surface = list;
            }

            _target = new ContextMenuTarget(_surface);
            Host = new ContextMenuHost(_surface, Compose);
            _surface.AddHandler(InputElement.PointerPressedEvent, OnPress, RoutingStrategies.Tunnel, handledEventsToo: true);
            _surface.AddHandler(InputElement.PointerReleasedEvent, OnRelease, RoutingStrategies.Tunnel, handledEventsToo: true);
            _surface.AddHandler(InputElement.ContextRequestedEvent, OnContextRequest, RoutingStrategies.Tunnel, handledEventsToo: true);
            _window = new Window { Width = 480, Height = 320, Content = _surface };
            _window.Show();
            _window.Activate();
            ContextGestures.Settle(_window);
            Selected = First;
        }

        internal Border RowHit(Row row) => FindRowPart<Border>(row, "GestureRowHit");

        internal Button ChildButton(Row row) => FindRowPart<Button>(row, "GestureChildButton");

        internal Control FocusableRowPart(Row row)
        {
            Border hit = RowHit(row);
            Control focus = hit.FindAncestorOfType<DataGridCell>() ?? ItemActivation.ContainerAt(hit, _surface)!;
            focus.Focusable = true;
            return focus;
        }

        private T FindRowPart<T>(Row row, string name) where T : Control => Assert.Single(_surface.GetVisualDescendants().OfType<T>(),
            part => part.Name == name && ReferenceEquals(ItemActivation.ItemAt<Row>(part, _surface), row));

        private StackPanel RowContent(Row row)
        {
            Button child = new() { Name = "GestureChildButton", Content = "Child" };
            child.Click += (_, _) => ChildActions.Add(row);
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Border { Name = "GestureRowHit", Background = Brushes.Transparent, Width = 112, Height = 40, Child = new TextBlock { Text = row.Name } },
                    child
                }
            };
        }

        private ContextMenuBuilder? Compose()
        {
            if (ItemActivation.ItemAt<Row>(_target.Source, _surface) is not { } row) return null;
            Compositions.Add(row);
            return new ContextMenuBuilder().Named(row.Name)
                .Item("gesture.open", "Open", () => MenuActions.Add(("gesture.open", row)), icon: "external-link")
                .Section()
                .Item("gesture.unavailable", "Copy", () => MenuActions.Add(("gesture.unavailable", row)), false, DisabledReason, icon: "copy");
        }

        private void OnPress(object? sender, PointerPressedEventArgs e)
        {
            LastPressModifiers = e.KeyModifiers;
            Gate.BeginPress();
            Row? row = MiddleClickTracker.RowAt<Row>(e.Source, _surface);
            _middle.Pressed(MiddleClickTracker.IsPress(e, _surface) ? row : null);
            if (row == null || !e.GetCurrentPoint(_surface).Properties.IsRightButtonPressed) return;
            Selected = row;
            if (!_openOnPress) return;
            Gate.MenuOpenedByPress();
            Host.Show(atPointer: true);
        }

        private void OnRelease(object? sender, PointerReleasedEventArgs e)
        {
            LastReleaseModifiers = e.KeyModifiers;
            Gate.Released(e.InitialPressMouseButton);
            // Implicit capture can keep the press source on the event even when the pointer is over another row.
            IInputElement? hit = _surface.InputHitTest(e.GetPosition(_surface));
            if (e.InitialPressMouseButton == MouseButton.Right) RightReleaseHit = ItemActivation.ItemAt<Row>(hit, _surface);
            if (!MiddleClickTracker.IsRelease(e)) return;
            Row? row = MiddleClickTracker.RowAt<Row>(hit, _surface);
            if (_middle.Released(row)) MiddleActions.Add(row!);
        }

        private void OnContextRequest(object? sender, ContextRequestedEventArgs e)
        {
            if (!e.TryGetPosition(_surface, out _)) return;
            PositionedRequests++;
            if (!Gate.TryConsume()) return;
            ConsumedRequests++;
            e.Handled = true;
        }

        public void Dispose()
        {
            Host.Dispose();
            _window.Close();
            ContextGestures.Settle(_window);
        }
    }
}
