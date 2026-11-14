using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using MeowChan.Desktop;
using MeowChan.Desktop.WorkViews;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

/// <summary>The appliers per element kind (plan AP5 §3.1 item 4) on real, windowless controls.</summary>
public sealed class WorkViewPresenterTests
{
    [Fact]
    public void ToolbarItemsFollowTheirRowOrTheFallbackAndOthersKeepTheirMode()
    {
        Button columns = new(), plain = new(), later = new();
        WorkViewElement.SetId(columns, "downloads.toolbar.columns");
        ToolBar.SetOverflowMode(plain, OverflowMode.Never);
        foreach (WorkView view in WorkViewSetting.All)
        {
            OverflowMode expected = view == WorkView.Barebones ? OverflowMode.Always : OverflowMode.AsNeeded;
            // Columns… is under More in Barebones and Simple (plan §1.3), on the bar in Advanced.
            Assert.True(WorkViewPresenter.ApplyToolBarItem(columns, view));
            Assert.Equal(view == WorkView.Advanced ? OverflowMode.AsNeeded : OverflowMode.Always, ToolBar.GetOverflowMode(columns));
            Assert.False(WorkViewPresenter.ApplyToolBarItem(plain, view));
            Assert.Equal(OverflowMode.Never, ToolBar.GetOverflowMode(plain));
            Assert.True(WorkViewPresenter.ApplyToolBarItem(later, view, "archive.results.other"));
            Assert.Equal(expected, ToolBar.GetOverflowMode(later));
            Assert.False(ActionToolBar.GetOmitted(columns));
        }
        // R6: a non-default state brings an overflowed item back onto the bar.
        WorkViewPresenter.ApplyToolBarItem(columns, WorkView.Barebones, nonDefaultState: true);
        Assert.Equal(OverflowMode.AsNeeded, ToolBar.GetOverflowMode(columns));
    }

    [Fact]
    public void HiddenToolbarItemsAreOmittedFromBarAndOverflowAndReturnWhenShown()
    {
        Button item = new();
        WorkViewElement.SetId(item, "shell.header.disk-graph"); // Hidden, Hidden, Direct
        WorkViewPresenter.ApplyToolBarItem(item, WorkView.Simple);
        Assert.True(ActionToolBar.GetOmitted(item));
        Assert.Equal(OverflowMode.Always, ToolBar.GetOverflowMode(item));
        WorkViewPresenter.ApplyToolBarItem(item, WorkView.Advanced);
        Assert.False(ActionToolBar.GetOmitted(item));
        Assert.Equal(OverflowMode.AsNeeded, ToolBar.GetOverflowMode(item));
    }

    [Fact]
    public void RequiredToolbarItemsNeverOverflow()
    {
        ToggleButton list = new();
        WorkViewElement.SetId(list, "archive.results.layout-list");
        foreach (WorkView view in WorkViewSetting.All)
        {
            WorkViewPresenter.ApplyToolBarItem(list, view);
            Assert.Equal(OverflowMode.Never, ToolBar.GetOverflowMode(list));
        }
    }

    [Fact]
    public void GateCombinesStateAndViewAndRemembersTheState()
    {
        TextBlock hint = new();
        WorkViewElement.SetId(hint, "downloads.intake.url-hint"); // Hidden, Hidden, Direct
        WorkViewGate.Apply(hint, WorkView.Advanced);
        Assert.True(hint.IsVisible);
        WorkViewGate.Apply(hint, WorkView.Simple);
        Assert.False(hint.IsVisible);
        Assert.True(WorkViewGate.HiddenByView(hint)); // its state wants it: a shortcut on it stays reachable
        WorkViewGate.Apply(hint, WorkView.Advanced, stateVisible: false);
        Assert.False(hint.IsVisible);
        Assert.False(WorkViewGate.HiddenByView(hint)); // hidden by its own state, not by the view
        WorkViewGate.Apply(hint, WorkView.Simple, nonDefaultState: true);
        Assert.True(hint.IsVisible);
        Assert.Throws<InvalidOperationException>(() => WorkViewGate.Visible(new TextBlock(), WorkView.Simple));
    }

    // A registered control that no gate drives (the page shows or hides it by its own state) is never "hidden by the view":
    // otherwise a shortcut could run, say, a retry button the state keeps away.
    [Fact]
    public void ARegisteredControlWithoutAGateIsNeverHiddenByTheView()
    {
        Button retry = new() { IsVisible = false };
        WorkViewElement.SetId(retry, "downloads.inspector.retry-media");
        Assert.False(WorkViewGate.HiddenByView(retry));
        WorkViewGate.Apply(retry, WorkView.Barebones, stateVisible: true); // Required: the view never hides it
        Assert.True(retry.IsVisible);
        Assert.False(WorkViewGate.HiddenByView(retry));
    }

    [Fact]
    public void ExpandersResetOnlyWhenTheViewChanges()
    {
        Expander section = new();
        WorkViewElement.SetId(section, "downloads.header.heading"); // any row: Hidden counts as not collapsed
        section.IsExpanded = false;
        WorkViewPresenter.ApplyExpander(section, WorkView.Simple, viewChanged: false);
        Assert.False(section.IsExpanded); // the user's choice stays for the session (R13)
        WorkViewPresenter.ApplyExpander(section, WorkView.Simple, viewChanged: true);
        Assert.True(section.IsExpanded);
        Assert.Throws<InvalidOperationException>(() => WorkViewPresenter.ApplyExpander(new Expander(), WorkView.Simple, true));
    }

    [Fact]
    public void ElementsRegisterByIdWithoutATreeWalk()
    {
        Button button = new();
        WorkViewElement.SetId(button, "archive.results.gallery");
        Assert.Contains(button, WorkViewElement.All());
        Assert.Contains(button, WorkViewElement.For(WorkViewPage.Archive));
        Assert.DoesNotContain(button, WorkViewElement.For(WorkViewPage.Downloads));
        WorkViewElement.SetId(button, null);
        Assert.DoesNotContain(button, WorkViewElement.All());
    }

    [Fact]
    public void StateRaisesChangedForLaterViewsOnly()
    {
        WorkViewState state = new();
        List<(WorkView, WorkView)> changes = new();
        state.Changed += (previous, current) => changes.Add((previous, current));
        state.Set(WorkView.Simple);
        Assert.Equal(WorkView.Simple, state.Current);
        Assert.Empty(changes);
        state.Set(WorkView.Simple);
        state.Set(WorkView.Advanced);
        Assert.Equal([(WorkView.Simple, WorkView.Advanced)], changes);
    }
}
