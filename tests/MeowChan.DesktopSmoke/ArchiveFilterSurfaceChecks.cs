using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Persistence;
using MeowChan.Testing;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

// UX103 P2: archive filter drafts as a local surface over the result list.
// Private host window and catalog, so sizes, languages and themes can change
// without touching the shared reader fixture. Keyboard input is routed, not OS input.
internal static class ArchiveFilterSurfaceChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static async Task RunAsync(MainWindow owner)
    {
        string directory = Path.Combine(TemporaryWorkspacePaths.TempRoot, "archive-filter-surface-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string language = Texts.Language;
        ArchivePage? page = null;
        Window? host = null;
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(directory);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory, lease);
            for (int i = 0; i < 60; i++)
                await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(new ResourceId("filter", "x", i.ToString()),
                    "https://filter-surface.fixture.invalid/" + i, "Filter surface " + i.ToString("D3"), directory, true));
            page = new ArchivePage(token => ArchiveReader.OpenAsync(directory, token), (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
            host = new Window { Title = "Archive filter surface — diagnostic", Width = 1520, Height = 920, Content = page };
            host.Show(owner);
            host.Activate();
            await Program.WaitUntilAsync(() => host.IsActive && page.IsEffectivelyVisible && page.Bounds.Width > 0);
            Require(await page.ApplySearchSpecAsync(new ArchiveSearchSpec(text: "Filter surface", sort: ArchiveThreadSort.Title)),
                "Filter surface setup query failed.");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (string code in new[] { "de", "en" })
            foreach ((double width, double height) in new[] { (1520d, 920d), (760d, 500d), (640d, 480d) })
                await CheckAsync(host, page, theme, code, width, height);
            Console.WriteLine("SMOKE archive filter surface: toggle, anchored geometry, focus in/out, Tab cycle, Escape order, apply/invalid draft, DE/EN, Light/Dark, 1520/760/640 passed.");
        }
        finally
        {
            Texts.SetLanguage(language);
            page?.EndLifetime();
            host?.Close();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private static async Task CheckAsync(Window host, ArchivePage page, ThemeVariant theme, string code, double width, double height)
    {
        string state = $"{theme}/{code}/{width}x{height}";
        host.RequestedThemeVariant = theme;
        Texts.SetLanguage(code);
        host.Width = width; host.Height = height;
        Expander filters = page.FindControl<Expander>("ArchiveFilters")!;
        Border surface = page.FindControl<Border>("ArchiveFilterSurface")!;
        Border results = page.FindControl<Border>("ArchiveResultsSurface")!;
        Grid listPane = page.FindControl<Grid>("ArchiveListPane")!;
        TextBox search = page.FindControl<TextBox>("ArchiveSearchText")!;
        Button apply = page.FindControl<Button>("ArchiveFilterApply")!, close = page.FindControl<Button>("ArchiveFilterClose")!;
        await Program.WaitUntilAsync(() =>
        {
            host.UpdateLayout();
            return PresentationChecks.MatchesRequestedSize(host, width, height) && !page.IsBusy && results.Bounds.Height > 0;
        });
        Rect Bounds(Control control) => new(control.TranslatePoint(default, host)
            ?? throw new InvalidOperationException(control.Name + " is detached."), control.Bounds.Size);
        bool Contains(Rect outer, Rect inner) => inner.Left >= outer.Left - 1 && inner.Top >= outer.Top - 1
            && inner.Right <= outer.Right + 1 && inner.Bottom <= outer.Bottom + 1;
        bool Same(Rect a, Rect b) => Math.Abs(a.Left - b.Left) <= 1 && Math.Abs(a.Top - b.Top) <= 1
            && Math.Abs(a.Right - b.Right) <= 1 && Math.Abs(a.Bottom - b.Bottom) <= 1;
        IInputElement? Focused() => host.FocusManager?.GetFocusedElement();
        bool FocusIn(Control owner) => Focused() is Visual visual && visual.GetSelfAndVisualAncestors().Contains(owner);
        void Key(Key key, KeyModifiers modifiers = KeyModifiers.None)
        {
            if (Focused() is not InputElement target) throw new InvalidOperationException(state + ": no focused element for " + key);
            target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        }
        Rect client = new(default, host.ClientSize);

        Require(!filters.IsExpanded && !surface.IsEffectivelyVisible, state + ": filter surface is open before the toggle.");
        Rect resultsClosed = Bounds(results);
        ToggleButton toggle = filters.GetVisualDescendants().OfType<ToggleButton>().First();
        Require(Bounds(filters).Height <= 48, state + ": the collapsed filter toggle still reserves field height.");

        // Deliberate open from the toggle moves focus into the surface.
        toggle.Focus(NavigationMethod.Tab);
        filters.IsExpanded = true;
        await Program.WaitUntilAsync(() => { host.UpdateLayout(); return surface.IsEffectivelyVisible && FocusIn(surface); });
        Rect area = Bounds(surface), list = Bounds(listPane);
        ScrollViewer scroll = page.FindControl<ScrollViewer>("ArchiveFilterSurfaceScroll")!;
        Console.WriteLine("SMOKE archive filter surface geometry: " + JsonSerializer.Serialize(new
        {
            state, scale = host.RenderScaling, surface = area.ToString(), list = list.ToString(), results = resultsClosed.ToString(),
            apply = Bounds(apply).ToString(), close = Bounds(close).ToString(), extent = scroll.Extent.Height, viewport = scroll.Viewport.Height
        }));
        Require(Contains(list, area) && Contains(client, area) && area.Top >= resultsClosed.Top - 1,
            state + ": filter surface leaves its list pane, the window or covers the search tools.");
        Require(Same(Bounds(results), resultsClosed), state + ": opening the filter surface pushed the result list.");
        Require(page.FindControl<TextBox>("ArchiveSearchText")!.IsEffectivelyVisible
            && page.FindControl<WrapPanel>("ArchiveAppliedFilters")!.IsEffectivelyVisible
            && Bounds(page.FindControl<WrapPanel>("ArchiveAppliedFilters")!).Bottom <= area.Top + 1,
            state + ": search text or applied chips are hidden by the open filter surface.");
        foreach (Button button in new[] { apply, close })
            Require(button.IsEffectivelyVisible && Contains(area, Bounds(button)) && Contains(client, Bounds(button))
                && button.DesiredSize.Width <= button.Bounds.Width + 1, state + ": " + button.Name + " is clipped.");
        // Long content scrolls inside the surface; the last field stays reachable.
        Control last = page.FindControl<TextBlock>("ArchiveDateHint")!;
        scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        await Program.WaitUntilAsync(() => { host.UpdateLayout(); return Contains(Bounds(scroll), Bounds(page.FindControl<Button>("ArchiveUseDeviceZone")!)); });
        scroll.Offset = default;
        Require(last.Parent != null, state + ": filter fields lost their date hint.");
        Program.CaptureWindow(host, $"smoke-archive-filter-surface-{(int)width}-{code}-{theme.ToString().ToLowerInvariant()}.png");

        // Tab stays inside the open surface; Escape closes it and returns to the toggle.
        close.Focus(NavigationMethod.Tab);
        Key(Avalonia.Input.Key.Tab);
        await Program.WaitUntilAsync(() => FocusIn(surface) && !ReferenceEquals(Focused(), close));
        ComboBox sort = page.FindControl<ComboBox>("ArchiveSort")!;
        sort.Focus(NavigationMethod.Tab);
        sort.IsDropDownOpen = true;
        await Program.WaitUntilAsync(() => sort.IsDropDownOpen);
        Key(Avalonia.Input.Key.Escape);
        await Program.WaitUntilAsync(() => !sort.IsDropDownOpen);
        Require(filters.IsExpanded, state + ": Escape on an open dropdown closed the whole filter surface.");
        sort.Focus(NavigationMethod.Tab);
        Key(Avalonia.Input.Key.Escape);
        await Program.WaitUntilAsync(() => !filters.IsExpanded && !surface.IsEffectivelyVisible && FocusIn(filters));

        // A programmatic open (command, invalid-draft reveal) keeps the current focus.
        search.Focus(NavigationMethod.Tab);
        filters.IsExpanded = true;
        await Program.WaitUntilAsync(() => surface.IsEffectivelyVisible);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(ReferenceEquals(Focused(), search) || FocusIn(search), state + ": a programmatic open stole focus from the search field.");

        // An invalid draft stays open and unapplied; a valid draft applies via the existing path and closes.
        ArchiveSearchSpec applied = page.AppliedSearchSpec!;
        ArchiveDateEditor start = page.FindControl<ArchiveDateEditor>("ArchiveStartDate")!;
        start.RawText = "not-a-date";
        apply.Focus(NavigationMethod.Tab);
        SmokeActionClick.Execute(apply);
        await Program.WaitUntilAsync(() => !page.IsBusy);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(filters.IsExpanded && ReferenceEquals(applied, page.AppliedSearchSpec),
            state + ": an invalid filter draft was applied or closed the surface.");
        start.RawText = string.Empty;
        ArchiveThreadSort target = applied.Sort == ArchiveThreadSort.Title ? ArchiveThreadSort.LatestCapture : ArchiveThreadSort.Title;
        sort.SelectedIndex = target == ArchiveThreadSort.Title ? 2 : 0;
        apply.Focus(NavigationMethod.Tab);
        SmokeActionClick.Execute(apply);
        await Program.WaitUntilAsync(() => !page.IsBusy && !filters.IsExpanded && page.AppliedSearchSpec?.Sort == target);
        await Program.WaitUntilAsync(() => FocusIn(filters));
        Require(Same(Bounds(results), resultsClosed), state + ": applying from the filter surface left the list layout shifted.");
    }
}
