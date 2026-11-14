using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

// Windowless real controls and menu commands. No icon assets, native decoder, GUI or profile.
[Collection("Work views")]
public sealed class WorkViewActionStateGuardTests
{
    [Theory]
    [InlineData("parent")]
    [InlineData("busy")]
    [InlineData("state")]
    [InlineData("visibility")]
    public void MenuBuiltWhileEnabledCannotForwardAfterTheTargetBecomesUnavailable(string change)
    {
        using TargetFixture fixture = new();
        WorkViewMoreSurface.Entry entry = fixture.Entry();
        MenuItem item = MenuItemFor(entry);
        Assert.True(item.IsEnabled);
        Assert.True(item.Command!.CanExecute(null));
        switch (change)
        {
            case "parent": fixture.Surface.IsEnabled = false; break;
            case "busy": fixture.Button.IsEnabled = false; break;
            case "state": WorkViewGate.Apply(fixture.Button, WorkView.Barebones, stateVisible: false); break;
            case "visibility":
                // A plain state-owned hidden button is not a work-view exception.
                WorkViewElement.SetGated(fixture.Button, false);
                fixture.Button.IsVisible = false;
                break;
        }
        Assert.False(entry.CanRun());
        Assert.False(item.Command.CanExecute(null));
        // A stale menu's stored command and a directly held entry must both be safe, even without a UI refresh.
        item.Command.Execute(null);
        entry.Run();
        Assert.Equal(0, fixture.Clicks);
    }

    [Fact]
    public void AncestorDisabledBeforeOpeningProducesADisabledEntryEvenWithLocallyEnabledButton()
    {
        using TargetFixture fixture = new();
        fixture.Surface.IsEnabled = false;
        Assert.True(fixture.Button.IsEnabled);
        Assert.False(fixture.Button.IsEffectivelyEnabled);
        WorkViewMoreSurface.Entry entry = fixture.Entry();
        Assert.False(entry.CanRun());
        MenuItem item = MenuItemFor(entry);
        Assert.False(item.IsEnabled);
        item.Command!.Execute(null);
        Assert.Equal(0, fixture.Clicks);
    }

    [Theory]
    [InlineData(WorkView.Barebones)]
    [InlineData(WorkView.Simple)]
    public void AButtonHiddenOnlyByTheViewStillRunsItsExistingClickHandlerExactlyOnce(WorkView view)
    {
        using TargetFixture fixture = new();
        WorkViewGate.Apply(fixture.Button, view);
        Assert.False(fixture.Button.IsVisible);
        Assert.True(WorkViewGate.HiddenByView(fixture.Button));
        WorkViewMoreSurface.Entry entry = Assert.Single(WorkViewMoreSurface.ForButtons([fixture.Button], view));
        MenuItem item = MenuItemFor(entry);
        Assert.True(item.Command!.CanExecute(null));
        item.Command.Execute(null);
        Assert.Equal(1, fixture.Clicks);
    }

    [Fact]
    public void AStateHiddenButtonIsNotListedAndAnAlreadyCapturedEntryCannotReviveIt()
    {
        using TargetFixture fixture = new();
        WorkViewMoreSurface.Entry captured = fixture.Entry();
        WorkViewGate.Apply(fixture.Button, WorkView.Barebones, stateVisible: false);
        Assert.Empty(WorkViewMoreSurface.ForButtons([fixture.Button], WorkView.Barebones));
        captured.Run();
        Assert.Equal(0, fixture.Clicks);
        WorkViewGate.Apply(fixture.Button, WorkView.Barebones, stateVisible: true);
        captured.Run();
        Assert.Equal(1, fixture.Clicks);
    }

    [Fact]
    public void BusyCommandIsRecheckedWithoutCanExecuteChangedAndTheCurrentParameterIsPreserved()
    {
        using TargetFixture fixture = new();
        MutableCommand command = new();
        fixture.Button.Command = command;
        fixture.Button.CommandParameter = "old";
        WorkViewMoreSurface.Entry entry = fixture.Entry();
        MenuItem item = MenuItemFor(entry);
        Assert.True(item.Command!.CanExecute(null));
        command.Allowed = false; // Deliberately no CanExecuteChanged notification.
        fixture.Button.CommandParameter = "current";
        Assert.False(item.Command.CanExecute(null));
        item.Command.Execute(null);
        entry.Run();
        Assert.Equal(0, command.Executions);
        command.Allowed = true;
        item.Command.Execute(null);
        Assert.Equal(1, command.Executions);
        Assert.Equal("current", command.LastParameter);
        Assert.Equal(0, fixture.Clicks); // The existing command path must not also raise Click.
    }

    [Fact]
    public void TheLiveCommandRatherThanTheCommandCapturedWhenOpeningIsInvokedOnce()
    {
        using TargetFixture fixture = new();
        MutableCommand before = new(), after = new();
        fixture.Button.Command = before;
        MenuItem item = MenuItemFor(fixture.Entry());
        fixture.Button.Command = after;
        item.Command!.Execute(null);
        Assert.Equal(0, before.Executions);
        Assert.Equal(1, after.Executions);
        Assert.Equal(0, fixture.Clicks);
    }

    [Fact]
    public void CustomMoreEntryChecksItsOwnersCurrentEnabledStateAsWellAsItsOwnPredicate()
    {
        Button owner = new();
        StackPanel page = new() { Children = { owner } };
        bool ready = true;
        int calls = 0;
        WorkViewMoreSurface.Entry custom = new(0, null, () => "Custom", () => calls++, () => ready);
        MenuItem item = Assert.IsType<MenuItem>(Assert.Single(WorkViewMoreSurface.CreateMenu([custom], owner).Items));
        Assert.True(item.Command!.CanExecute(null));
        page.IsEnabled = false;
        Assert.True(owner.IsEnabled);
        Assert.False(item.Command.CanExecute(null));
        item.Command.Execute(null);
        page.IsEnabled = true;
        ready = false;
        item.Command.Execute(null);
        Assert.Equal(0, calls);
        ready = true;
        item.Command.Execute(null);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DetachedToolbarItemAlsoChecksTheLogicalToolbarSurface()
    {
        Button button = new();
        ActionToolBar toolbar = new();
        toolbar.Items.Add(button);
        Assert.Same(toolbar, button.FindLogicalAncestorOfType<ActionToolBar>());
        int calls = 0;
        button.Click += (_, _) => calls++;
        Assert.True(WorkViewActionStateGuard.CanInvoke(button));
        toolbar.IsEnabled = false;
        Assert.False(WorkViewActionStateGuard.CanInvoke(button));
        WorkViewActionStateGuard.Invoke(button);
        Assert.Equal(0, calls);
        toolbar.IsEnabled = true;
        WorkViewActionStateGuard.Invoke(button);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void GuardedMenuHandOffChecksEnabledStateAndCanExecuteAndRunsExactlyOnce()
    {
        MutableCommand command = new();
        MenuItem target = new() { Command = command, CommandParameter = "menu parameter" };
        StackPanel surface = new() { Children = { target } };
        Assert.True(WorkViewActionStateGuard.CanInvoke(target));
        surface.IsEnabled = false;
        WorkViewActionStateGuard.Invoke(target);
        surface.IsEnabled = true;
        command.Allowed = false;
        WorkViewActionStateGuard.Invoke(target);
        Assert.Equal(0, command.Executions);
        command.Allowed = true;
        WorkViewActionStateGuard.Invoke(target);
        Assert.Equal(1, command.Executions);
        Assert.Equal("menu parameter", command.LastParameter);
    }

    [Fact]
    public void GuardedToggleIsNotToggledWhileBusyAndTogglesOnlyOnceWhenEnabled()
    {
        ToggleButton target = new();
        int clicks = 0;
        target.Click += (_, _) => clicks++;
        target.IsEnabled = false;
        WorkViewActionStateGuard.Invoke(target);
        Assert.NotEqual(true, target.IsChecked);
        Assert.Equal(0, clicks);
        target.IsEnabled = true;
        WorkViewActionStateGuard.Invoke(target);
        Assert.True(target.IsChecked);
        Assert.Equal(1, clicks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReducedShortcutConsumesADisabledMatchAndExecutesAnEnabledMatchOnce(bool busyCommand)
    {
        using TargetFixture fixture = new();
        MutableCommand command = new();
        fixture.Button.Command = command;
        CommandBindings.BindExisting(fixture.Button, "window.fullscreen");
        Assert.Contains(fixture.Button, WorkViewShortcuts.ReducedControls(fixture.Surface));
        if (busyCommand) command.Allowed = false;
        else fixture.Surface.IsEnabled = false;
        KeyEventArgs disabled = new() { Key = Key.F11, Source = fixture.Button };
        Assert.True(WorkViewShortcuts.RouteReduced(fixture.Surface, disabled));
        Assert.True(disabled.Handled);
        Assert.Equal(0, command.Executions);
        command.Allowed = true;
        fixture.Surface.IsEnabled = true;
        KeyEventArgs enabled = new() { Key = Key.F11, Source = fixture.Button };
        Assert.True(WorkViewShortcuts.RouteReduced(fixture.Surface, enabled));
        Assert.True(enabled.Handled);
        Assert.Equal(1, command.Executions);
        Assert.Equal(0, fixture.Clicks);
    }

    private static MenuItem MenuItemFor(WorkViewMoreSurface.Entry entry) =>
        // The fixture's policy row has no catalog id, so this is the real icon-free production menu path.
        Assert.IsType<MenuItem>(Assert.Single(WorkViewMoreSurface.CreateMenu([entry]).Items));

    private sealed class TargetFixture : IDisposable
    {
        internal readonly Button Button = new() { Content = "Action" };
        internal readonly StackPanel Surface = new();
        internal int Clicks;

        internal TargetFixture()
        {
            Surface.Children.Add(Button);
            WorkViewElement.SetId(Button, "plugins.header.add");
            WorkViewGate.Apply(Button, WorkView.Barebones);
            Button.Click += (_, _) => Clicks++;
        }

        internal WorkViewMoreSurface.Entry Entry() => Assert.Single(WorkViewMoreSurface.ForButtons([Button], WorkView.Barebones));
        public void Dispose() => WorkViewElement.SetId(Button, null);
    }

    private sealed class MutableCommand : ICommand
    {
        public bool Allowed = true;
        public int Executions;
        public object? LastParameter;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => Allowed;
        public void Execute(object? parameter) { Executions++; LastParameter = parameter; }
    }
}
