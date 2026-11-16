using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;

namespace MeowChan.DesktopTests;

/// <summary>
/// The Sources focus handoff (P3-6): a target that never becomes reachable must not leave focus on a hidden control or on nothing.
/// A page without a TopLevel cannot take real keyboard focus, so the page's focus seam records what the handoff asks to focus; the real
/// focus movement stays with the native smokes.
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class SourcesFocusFallbackTests
{
    private sealed class TokenApplication : Application
    {
        public override void Initialize() =>
            Styles.Add(new StyleInclude(new Uri("avares://MeowChan/")) { Source = new Uri("avares://MeowChan/Desktop/WorkspaceStyles.axaml") });
    }

    private static void WithPage(Action<SourcesPage, List<Control>> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication app = new();
        AvaloniaLocator.CurrentMutable.Bind<Application>().ToConstant(app);
        app.Initialize();
        SourcesPage page = new();
        List<Control> focused = new();
        page.FocusControl = control => { focused.Add(control); return true; };
        try { test(page, focused); }
        finally { (page as IDisposable)?.Dispose(); }
    });

    [Fact]
    public void ATargetThatNeverBecomesVisibleEndsWithFocusInsideThePageOnTheFinalPass() => WithPage((page, focused) =>
    {
        TextBlock heading = page.FindControl<TextBlock>("SourceHeading")!;
        heading.IsVisible = false;

        Assert.False(page.SettleSourceFocus(heading, 0));
        Assert.False(page.SettleSourceFocus(heading, 1));
        Assert.Empty(focused);

        Assert.True(page.SettleSourceFocus(heading, 2));
        Control landed = Assert.Single(focused);
        Assert.NotSame(heading, landed);
        Assert.True(landed.IsEffectivelyVisible && landed.IsEffectivelyEnabled && landed.Focusable && landed.IsTabStop);
        Assert.True(page.IsLogicalAncestorOf(landed), landed.GetType().Name + " " + landed.Name + " is not inside the page");
    });

    [Fact]
    public void AReachableTargetIsFocusedAndNeverReplacedByTheFallback() => WithPage((page, focused) =>
    {
        TextBlock heading = page.FindControl<TextBlock>("SourceHeading")!;
        Assert.False(page.SettleSourceFocus(heading, 0));
        Assert.All(focused, control => Assert.Same(heading, control));
    });

    [Fact]
    public void TheFallbackSkipsHiddenAndDisabledSubtrees() => WithPage((page, _) =>
    {
        Button Fresh() => new();
        StackPanel Hidden() => new() { IsVisible = false, Children = { Fresh() } };
        StackPanel Disabled() => new() { IsEnabled = false, Children = { Fresh() } };
        Button reachable = new() { Name = "reachable" };
        StackPanel root = new() { Children = { Hidden(), Disabled(), reachable } };
        Assert.Same(reachable, SourcesPage.FirstReachableControl(root));
        Assert.Null(SourcesPage.FirstReachableControl(new StackPanel { Children = { Hidden(), Disabled() } }));
        Assert.Null(SourcesPage.FirstReachableControl(root, exclude: reachable));
    });
}
