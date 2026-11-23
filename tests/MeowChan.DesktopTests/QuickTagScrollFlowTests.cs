using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using Semi.Avalonia;
using Ursa.Themes.Semi;

namespace MeowChan.DesktopTests;

// Actual Avalonia measure/arrange and installed scroll template, without a native window.
// Fixed chip sizes make the row/reachability assertions independent of platform font metrics.
[Collection("Localization")]
public sealed class QuickTagScrollFlowTests
{
    [Theory]
    [InlineData("panel", 280)]
    [InlineData("panel", 416)]
    [InlineData("popover", 280)]
    [InlineData("popover", 440)]
    [InlineData("rate-and-tag", 280)]
    [InlineData("rate-and-tag", 440)]
    public void ManyAssignedTagsScrollAwayAndThreeSuggestionRowsRemainReachable(string host, double width)
    {
        using IDisposable platform = AvaloniaTestPlatform.EnterSkia();
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
            Application app = TokenApplication.Bind();
            app.Styles.Insert(0, new SemiTheme());
            app.Styles.Insert(1, new UrsaSemiTheme());
            Texts.Initialize(); Texts.SetLanguage("en");
            using TempDirectory temp = new();
            using ProfileLease lease = ProfileLease.Acquire(temp.Path);
            PrivateTagStore store = new(temp.Path, lease);
            DateTimeOffset day = DateTimeOffset.UnixEpoch;
            MediaLibraryEntry entry = new(new ResourceId("s", "b", "a"), "Ta", 1, day, day, "1", 1, null, 0,
                "att-v1:" + new string('a', 64), "/dir/a", "asset-a", new string('a', 64), 1, "m1", "original",
                "fa.png", SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);
            var target = MediaLibraryFilterEngine.TagTarget(entry)!;
            for (int index = 0; index < 60; index++) store.SetMedia(store.Define("assigned_" + index).Tag, target, true);
            using QuickTagSession session = new(QuickTagTarget.For(new MediaTagSession(new MediaTagSubject.Bound(target),
                () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
                (name, subject, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [subject], null, token)),
                (id, subject, assigned, token) => Task.FromResult(store.SetMediaSubjects(id, [subject], assigned, null, token)))));
            QuickTagOptions options = new() { SuggestionCount = 18, PanelSuggestionLimit = 18 };
            DelegateQuickTagSuggestions suggestions = new(request => Enumerable.Range(1, request.Limit).Select(index =>
            {
                Assert.True(PrivateTagName.TryCreate("offered_" + index, out PrivateTagName? name, out _));
                return new QuickTagSuggestion(name!, null, 100 - index, QuickTagReason.Frequent, 1);
            }).ToArray());
            QuickTagPopover? popover = host == "panel" ? null : QuickTagPopover.Create(new Button(), session, "many tags", new()
            {
                Quick = options, Suggestions = suggestions,
                Decision = host == "rate-and-tag" ? new QuickTagPopoverDecision
                {
                    View = () => GalleryDecisionView.Unavailable, SetRating = _ => Task.CompletedTask
                } : null
            });
            using QuickTagSurface surface = popover?.Surface ?? new QuickTagSurface(session, options) { FillSuggestions = true, Suggestions = suggestions };
            surface.FocusOnAttach = false;
            if (popover != null) popover.Frame.Width = width;
            GallerySidePanel? panel = host == "panel" ? new GallerySidePanel(new PanelCloseButton("Close"))
                { TagContent = surface, Segment = GallerySidePanelSegment.Tags } : null;
            Root root = new() { Child = popover?.Frame ?? (Control?)panel ?? surface };
            ScrollViewer scroll = Assert.IsType<ScrollViewer>(surface.Content);
            // The popover delegates its bounded viewport to the reusable surface, as does the gallery panel: never two active owners.
            scroll.Theme = (ControlTheme)app.FindResource(typeof(ScrollViewer))!;
            try
            {
                double height = host == "panel" ? 360 : QuickTagPopover.MaximumHeight;
                void Layout()
                {
                    for (int pass = 0; pass < 6; pass++)
                    {
                        foreach (Control control in root.GetVisualDescendants().OfType<Control>().ToArray())
                        {
                            control.ApplyStyling();
                            if (control is TemplatedControl templated) templated.ApplyTemplate();
                            if (control is ScrollViewer viewer)
                                foreach (ScrollContentPresenter part in viewer.GetVisualDescendants().OfType<ScrollContentPresenter>())
                                {
                                    // Content-presenter registration is normally completed on native visual-tree attachment.
                                    // This standalone logical root has no native attachment; wire its actual template part explicitly.
                                    typeof(ScrollViewer).GetMethod("RegisterContentPresenter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
                                        .Invoke(viewer, [part]);
                                    // Avalonia 12 connects content, scroll permissions, extent and offset on visual attachment.
                                    typeof(ScrollContentPresenter).GetMethod("AttachToScrollViewer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                                        .Invoke(part, null);
                                    part.UpdateChild();
                                }
                            if (control is ContentPresenter contentPresenter) contentPresenter.UpdateChild();
                        }
                        foreach (var assigned in surface.AssignedButtons)
                        {
                            assigned.Chip.Width = 120; assigned.Chip.Height = 28;
                            assigned.Remove.Width = 28; assigned.Remove.Height = 28;
                        }
                        foreach (Button button in surface.SuggestionButtons) { button.Width = 96; button.Height = 28; }
                        foreach (Layoutable child in root.GetVisualDescendants().OfType<Layoutable>().Reverse()) child.InvalidateMeasure();
                        root.InvalidateMeasure();
                        root.Measure(new Size(width, height));
                        root.Arrange(new Rect(0, 0, width, height));
                    }
                }
                Layout();
                ScrollContentPresenter presenter = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().Single();
                Assert.Equal(60, surface.AssignedButtons.Count);
                Assert.Equal(18, surface.Shown.Count);
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height * 2, $"extent={scroll.Extent}; viewport={scroll.Viewport}; scroll={scroll.Bounds}; presenter={presenter.Bounds}; presenterExtent={presenter.Extent}; presenterViewport={presenter.Viewport}; content={contentSize(scroll)}");
                // Ignore the input's closed autocomplete template; neither tag block has an inner viewport.
                Grid content = Assert.IsType<Grid>(scroll.Content);
                Border assignedBlock = content.Children.OfType<Border>().Single(child => child.Name!.EndsWith("Body"));
                Grid suggestionArea = content.Children.OfType<Grid>().Single(child => child.Name?.EndsWith("SuggestionArea") == true);
                Assert.Empty(assignedBlock.GetVisualDescendants().OfType<ScrollViewer>());
                Assert.Empty(suggestionArea.GetVisualDescendants().OfType<ScrollViewer>());
                Assert.Equal(new GridLength(32), content.RowDefinitions[0].Height);
                Assert.All(content.RowDefinitions.Skip(1), row => Assert.Equal(GridUnitType.Auto, row.Height.GridUnitType));
                Assert.False(scroll.Focusable);
                Assert.Equal(1, KeyboardNavigation.GetTabIndex(surface.Header));
                Assert.True(surface.AssignedButtons.Select(item => item.Chip.TranslatePoint(default, assignedBlock)!.Value.Y).Distinct().Count() > 3);
                Button first = surface.SuggestionButtons[0], last = surface.SuggestionButtons[^1];
                Assert.True(first.TranslatePoint(default, presenter)!.Value.Y > scroll.Viewport.Height, "suggestions initially below the fold");
                double firstY = first.TranslatePoint(default, content)!.Value.Y;
                scroll.Offset = new Vector(0, firstY - 4);
                Layout();
                var fullyVisibleRows = surface.SuggestionButtons.Select(button => (Y: button.TranslatePoint(default, presenter)!.Value.Y, button.Bounds.Height))
                    .Where(row => row.Y >= 0 && row.Y + row.Height <= scroll.Viewport.Height).Select(row => row.Y).Distinct().Count();
                Assert.True(fullyVisibleRows >= 3, $"visible suggestion rows={fullyVisibleRows}; viewport={scroll.Viewport}");
                Assert.True(surface.AssignedButtons[0].Chip.TranslatePoint(default, presenter)!.Value.Y < 0, "assigned tags scroll away in the same flow");
                // Exercise the routed focus-reveal path (managed event, not native focus/input acceptance).
                scroll.Offset = default;
                Layout();
                last.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent) { NavigationMethod = NavigationMethod.Directional });
                Layout();
                double revealedY = last.TranslatePoint(default, presenter)!.Value.Y;
                Assert.True(scroll.Offset.Y > 0);
                Assert.InRange(revealedY, 0, scroll.Viewport.Height - last.Bounds.Height);
                // A commit/rebuild grows assigned content, but suggestions retain the same single owner and minimum capacity.
                surface.ToggleSuggestionAsync(0).GetAwaiter().GetResult();
                surface.Refresh();
                Layout();
                Assert.Equal(61, surface.AssignedButtons.Count);
                Assert.Same(scroll, surface.Content);
                Assert.Equal(17, surface.SuggestionButtons.Count);
                Assert.True(((Control)surface.SuggestionButtons[0].Parent!).Parent is WrapPanel);
            }
            finally { root.Child = null; popover?.Close(); Texts.SetLanguage("en"); }
        });
    }

    private static string contentSize(ScrollViewer scroll) => ((Control)scroll.Content!).Bounds + " desired=" + ((Control)scroll.Content!).DesiredSize;

    private sealed class Root : Decorator, ILogicalRoot;
}
