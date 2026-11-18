using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using Semi.Avalonia;
using Ursa.Controls;
using Ursa.Themes.Semi;

namespace MeowChan.DesktopTests;

// The installed Ursa template, original controls, and shared projection. Windowless
// layout tests: Popup.IsOpen verifies the real template binding, not native input.
[Collection("Avalonia global state")]
public sealed class ActionToolBarOverflowContractTests
{
    [Fact]
    public void ZeroActionsCannotExposeOrOpenAnEmptyOverflow() => Run(fixture =>
    {
        Assert.False(fixture.More.IsVisible); // safe even before the first measure
        fixture.Layout();
        fixture.AssertEmpty();
        fixture.More.IsChecked = true; // stale/accessibility activation must also be safe
        fixture.AssertEmpty();
    });

    [Fact]
    public void OneActionRemainsTheSameReachableControlUnderMore() => Run(fixture =>
    {
        Button action = fixture.AddAction();
        int clicks = 0;
        action.Click += (_, _) => clicks++;
        fixture.Layout();
        Assert.True(fixture.More.IsVisible);
        Assert.Same(action, Assert.Single(fixture.Bar.OverflowHost!.Children));
        fixture.More.IsChecked = true;
        Assert.True(fixture.Popup.IsOpen);
        ((IInvokeProvider)new ButtonAutomationPeer(action)).Invoke();
        Assert.Equal(1, clicks);
        Assert.False(fixture.Popup.IsOpen);
    });

    [Fact]
    public void HiddenPermissionFilteredAndViewOmittedActionsDoNotCount() => Run(fixture =>
    {
        fixture.AddAction().IsVisible = false; // context/permission filtering owns IsVisible, not IsEnabled
        Button omitted = fixture.AddAction();
        // WorkViewPresenter owns the policy decision; Omitted is its shared
        // projection input (unlike IsVisible it also parks an otherwise visible action).
        ActionToolBar.SetOmitted(omitted, true);
        fixture.Layout();
        fixture.AssertEmpty();
        fixture.More.IsChecked = true;
        fixture.AssertEmpty();
    });

    [Fact]
    public void DisabledExplanatoryActionStaysDiscoverableAccordingToExistingToolbarPolicy() => Run(fixture =>
    {
        // ActionToolBar reparents originals including disabled reasons. Disabled is
        // NOT permission-hidden: the user must still discover why an action is unavailable.
        Button action = fixture.AddAction();
        action.IsEnabled = false;
        ToolTip.SetTip(action, "Select a target first");
        fixture.Layout();
        Assert.True(fixture.Bar.IsVisible);
        Assert.True(fixture.More.IsVisible);
        fixture.More.IsChecked = true;
        Assert.True(fixture.Popup.IsOpen);
        Assert.Same(action, Assert.Single(fixture.Bar.OverflowHost!.Children));
        Assert.False(action.IsEffectivelyEnabled);
        Assert.Equal("Select a target first", ToolTip.GetTip(action));
    });

    [Fact]
    public void VisibilityChangesCloseEmptyPopupAndReuseStableControlsAndLayout() => Run(fixture =>
    {
        Button direct = fixture.AddAction();
        ToolBar.SetOverflowMode(direct, OverflowMode.Never);
        Button action = fixture.AddAction();
        fixture.Layout();
        ToggleButton more = fixture.More;
        ActionToolBarPanel panel = fixture.Bar.DirectHost!;
        Rect directBounds = direct.Bounds;
        List<bool> visibilityTransitions = [];
        more.PropertyChanged += (_, e) =>
        {
            if (e.Property == Control.IsVisibleProperty) visibilityTransitions.Add(more.IsVisible);
        };
        for (int cycle = 0; cycle < 3; cycle++)
        {
            more.IsChecked = true;
            Assert.True(fixture.Popup.IsOpen);
            action.IsVisible = false;
            Assert.False(panel.IsMeasureValid);
            // No layout opportunity is needed to reject an empty menu.
            Assert.False(fixture.Popup.IsOpen);
            fixture.Layout();
            fixture.AssertEmpty();
            Rect emptyBounds = more.Bounds;
            fixture.Layout();
            Assert.Equal(emptyBounds, more.Bounds);
            action.IsVisible = true;
            Assert.False(panel.IsMeasureValid);
            fixture.Layout();
            Assert.True(more.IsVisible);
            Assert.Same(action, Assert.Single(fixture.Bar.OverflowHost!.Children));
            Assert.Same(more, fixture.More);
            Assert.Same(panel, fixture.Bar.DirectHost);
            Assert.Equal(directBounds, direct.Bounds);
            Rect visibleBounds = more.Bounds;
            fixture.Layout();
            Assert.Equal(visibleBounds, more.Bounds);
        }
        Assert.Equal(new[] { false, true, false, true, false, true }, visibilityTransitions);
    });

    [Fact]
    public void FilteringOneOfSeveralActionsKeepsTheNonemptyOverflowOpen() => Run(fixture =>
    {
        Button filtered = fixture.AddAction();
        Button remaining = fixture.AddAction();
        fixture.Layout();
        ToggleButton more = fixture.More;
        more.IsChecked = true;
        filtered.IsVisible = false;
        Assert.True(more.IsVisible);
        Assert.True(fixture.Popup.IsOpen);
        fixture.Layout();
        Assert.Same(more, fixture.More);
        Assert.Same(remaining, Assert.Single(fixture.Bar.OverflowHost!.Children));
        Assert.Equal(ActionToolBar.OverflowAccessibleName(1), Avalonia.Automation.AutomationProperties.GetName(more));
        Assert.True(fixture.Popup.IsOpen);
    });

    [Fact]
    public void RemovingTheLastActionClosesOverflowWithoutWaitingForLayout() => Run(fixture =>
    {
        fixture.AddAction();
        fixture.Layout();
        fixture.More.IsChecked = true;
        Assert.True(fixture.Popup.IsOpen);
        fixture.Bar.Items.Clear();
        fixture.AssertEmpty();
        fixture.Layout();
        fixture.AssertEmpty();
    });

    [Fact]
    public void OmittingTheLastActionClosesOverflowAndRestoresItWhenEligibleAgain() => Run(fixture =>
    {
        Button action = fixture.AddAction();
        fixture.Layout();
        fixture.More.IsChecked = true;
        ActionToolBar.SetOmitted(action, true);
        Assert.False(fixture.More.IsVisible);
        Assert.False(fixture.Popup.IsOpen);
        fixture.Layout();
        fixture.AssertEmpty();
        ActionToolBar.SetOmitted(action, false);
        fixture.Layout();
        Assert.True(fixture.More.IsVisible);
        Assert.Same(action, Assert.Single(fixture.Bar.OverflowHost!.Children));
    });

    [Fact]
    public void ChangingOverflowModeRemeasuresProjectionAtUnchangedWidth() => Run(fixture =>
    {
        Button action = fixture.AddAction();
        fixture.Layout();
        Assert.True(fixture.More.IsVisible);
        ToolBar.SetOverflowMode(action, OverflowMode.AsNeeded);
        Assert.False(fixture.Bar.DirectHost!.IsMeasureValid);
        fixture.Layout();
        fixture.AssertEmpty();
        Assert.Contains(action, fixture.Bar.DirectHost.Children);
        ToolBar.SetOverflowMode(action, OverflowMode.Always);
        Assert.False(fixture.Bar.DirectHost.IsMeasureValid);
        fixture.Layout();
        Assert.True(fixture.More.IsVisible);
        Assert.Same(action, Assert.Single(fixture.Bar.OverflowHost!.Children));
    });

    private static void Run(Action<Fixture> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        Application app = new() { RequestedThemeVariant = ThemeVariant.Dark };
        AvaloniaLocator.CurrentMutable.Bind<Application>().ToConstant(app);
        app.Styles.Add(new SemiTheme());
        app.Styles.Add(new UrsaSemiTheme());
        using Fixture fixture = new(app);
        test(fixture);
    });

    private sealed class Fixture : IDisposable
    {
        internal readonly ActionToolBar Bar = new();
        private readonly Root _root;
        internal ToggleButton More => Bar.More!;
        internal Popup Popup => ((Panel)More.Parent!).Children.OfType<Popup>().Single();

        internal Fixture(Application app)
        {
            _root = new Root { Child = Bar };
            Bar.Theme = (ControlTheme)app.FindResource(typeof(ToolBar))!;
            Bar.ApplyStyling();
            Bar.ApplyTemplate();
            Realize();
        }

        internal Button AddAction()
        {
            Button action = new() { Width = 90, Height = 32 };
            ToolBar.SetOverflowMode(action, OverflowMode.Always);
            Bar.Items.Add(action);
            return action;
        }

        private void Realize()
        {
            for (int pass = 0; pass < 5; pass++)
                foreach (Control control in Bar.GetVisualDescendants().OfType<Control>().ToArray())
                {
                    control.ApplyStyling();
                    if (control is TemplatedControl templated) templated.ApplyTemplate();
                    if (control is ItemsPresenter presenter) presenter.ApplyTemplate();
                }
        }

        internal void Layout()
        {
            Realize();
            for (int pass = 0; pass < 6; pass++)
            {
                // As in ActionToolBarRenderedStretchTests, the logical root has no
                // native layout manager. Invalidation contracts are asserted before
                // driving these explicit passes through the installed template.
                _root.InvalidateMeasure();
                Bar.InvalidateMeasure();
                foreach (Control control in Bar.GetVisualDescendants().OfType<Control>()) control.InvalidateMeasure();
                _root.Measure(new Size(600, 32));
                More.ApplyStyling();
                _root.Arrange(new Rect(0, 0, 600, 32));
            }
        }

        internal void AssertEmpty()
        {
            Assert.False(More.IsVisible);
            Assert.NotEqual(true, More.IsChecked);
            Assert.False(Popup.IsOpen);
            Assert.DoesNotContain(Bar.OverflowHost!.Children, item => item.IsVisible);
        }

        public void Dispose()
        {
            More.IsChecked = false;
            Popup.IsOpen = false;
            _root.Child = null;
        }
    }

    private sealed class Root : Decorator, Avalonia.LogicalTree.ILogicalRoot;
}
