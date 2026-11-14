using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// FP1 (smoke run wave 5): <c>new GalleryViewer()</c> threw "GalleryPlaybackTools already has a visual parent GalleryOverflowMenu" because the
/// first PlaceSlideshowToggle (slideshow not on the bar: the Barebones start) gave back the never-lent settings panel into "⋯" and the
/// constructor then added it a second time. The viewer is built without a window here, in every work view, and the settings panel must
/// have exactly one owner: the "⋯" menu, once, between its separators.
/// </summary>
[CollectionDefinition("Gallery viewer construction", DisableParallelization = true)]
public sealed class GalleryViewerConstructionCollection;

[Collection("Gallery viewer construction")]
public sealed class GalleryViewerConstructionTests
{

    [Theory]
    [InlineData(WorkView.Barebones)]
    [InlineData(WorkView.Simple)]
    [InlineData(WorkView.Advanced)]
    public void TheViewerIsBuiltInEveryWorkViewAndTheSlideshowSettingsHaveOneOwner(WorkView view) => BooruComponentDispatcher.Run(() =>
    {
        // No window: the filmstrip asks for a cursor, so a cursor factory that hands out inert cursors stands in for the platform.
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(Inert<ICursorFactory>());
        GalleryViewer viewer = new();
        AssertSettingsOnceInMenu(viewer);
        viewer.ApplyWorkView(view);
        AssertSettingsOnceInMenu(viewer);
        // Back and forth between the views (the toggle moves between the bar and the menu): the panel never doubles or gets lost.
        foreach (WorkView next in new[] { WorkView.Advanced, WorkView.Barebones, WorkView.Simple, view })
        {
            viewer.ApplyWorkView(next);
            AssertSettingsOnceInMenu(viewer);
        }
        viewer.SetSlideshowMenuOpen(false); // closing a menu that never opened gives nothing back twice
        AssertSettingsOnceInMenu(viewer);
    });

    private static T Inert<T>() where T : class => DispatchProxy.Create<T, InertProxy>();

    // Answers every call with null, an inert cursor (ICursorImpl) or the default value.
    public class InertProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Type? result = targetMethod?.ReturnType;
            if (result == null || result == typeof(void)) return null;
            if (result.IsInterface)
                return typeof(DispatchProxy).GetMethod(nameof(DispatchProxy.Create), 2, Type.EmptyTypes)!
                    .MakeGenericMethod(result, typeof(InertProxy)).Invoke(null, null);
            return result.IsValueType ? Activator.CreateInstance(result) : null;
        }
    }

    private static void AssertSettingsOnceInMenu(GalleryViewer viewer)
    {
        Panel menu = Assert.IsAssignableFrom<Panel>(viewer.OverflowMenuContent);
        Panel settings = viewer.SlideshowSettingsPanel;
        Assert.Same(menu, settings.Parent);
        Assert.Single(menu.Children, child => ReferenceEquals(child, settings));
        Assert.Single(menu.GetLogicalDescendants(), child => ReferenceEquals(child, settings));
    }

    // ---- the loan itself: a panel that was never lent is not the loan's to give back ----

    [Fact]
    public void ReturningAPanelThatWasNeverLentDoesNotPlaceIt()
    {
        StackPanel home = new(), panel = new();
        PanelLoan loan = new(home, panel, new ScrollViewer());
        loan.Return(); // the owner has not placed the panel yet (the viewer's constructor)
        Assert.Null(panel.Parent);
        Assert.Empty(home.Children);
        loan.PlaceAtHome(); // the owner places it: once
        loan.PlaceAtHome();
        Assert.Single(home.Children, child => ReferenceEquals(child, panel));
        loan.Return();
        Assert.Single(home.Children, child => ReferenceEquals(child, panel));
    }

    [Fact]
    public void PlacingAtHomeDetachesThePanelFromAnotherParentFirst()
    {
        StackPanel home = new(), elsewhere = new(), panel = new();
        elsewhere.Children.Add(panel);
        ScrollViewer host = new();
        PanelLoan loan = new(home, panel, host);
        loan.PlaceAtHome();
        Assert.Same(home, panel.Parent);
        Assert.Empty(elsewhere.Children);
        loan.Lend();
        loan.PlaceAtHome(); // the owner takes it back while lent: the host lets go first
        Assert.Null(host.Content);
        Assert.False(loan.IsLent);
        Assert.Single(home.Children, child => ReferenceEquals(child, panel));
        loan.Return(); // nothing is lent any more
        Assert.Single(home.Children, child => ReferenceEquals(child, panel));
    }
}
