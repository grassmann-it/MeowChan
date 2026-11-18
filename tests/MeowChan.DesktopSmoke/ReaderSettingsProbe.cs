using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

// RL1: the reader's settings popover (the gear) as the smokes meet it. The former View menu is now the popover's session rows, so
// the checks that read the menu open the popover instead. A page without a shell gets a stand-in host (a private Config, nothing saved).
internal static class ReaderSettingsProbe
{
    internal static QuickSettingsHost StandIn(Config? config = null)
    {
        Config current = config ?? new Config();
        return new QuickSettingsHost
        {
            CurrentConfig = () => current,
            SaveAsync = change => { change(current); return Task.CompletedTask; },
            ShowAllSettings = _ => { }
        };
    }

    /// <summary>Opens the popover under the gear (or keeps it open) and waits until it has its size.</summary>
    internal static async Task<QuickSettingsSurface> OpenAsync(ArchivePage page)
    {
        page.QuickSettingsHost ??= StandIn();
        string State()
        {
            Button? gear = page.FindControl<Button>("ReaderSettingsOpen");
            ActionToolBar? bar = page.FindControl<ActionToolBar>("ReaderToolbar");
            Control? anchor = page.ReaderQuickSettings?.Flyout.Target;
            QuickSettingsSurface? surface = page.ReaderQuickSettings?.Surface;
            string ControlState(Control? control) => control == null ? "null" : $"{control.Name ?? control.GetType().Name}: visible={control.IsEffectivelyVisible}, bounds={control.Bounds}, root={TopLevel.GetTopLevel(control)?.GetType().Name ?? "null"}, parent={control.Parent?.GetType().Name ?? "null"}";
            return $"dialog={page.IsReaderDialogOpen}, open={page.IsQuickSettingsOpen}; gear=[{ControlState(gear)}]; more=[{ControlState(bar?.More)}]; anchor=[{ControlState(anchor)}]; surface=[{ControlState(surface)}]";
        }
        Console.WriteLine("SMOKE reader settings before open: " + State());
        if (!page.IsQuickSettingsOpen) page.OpenReaderQuickSettings();
        Console.WriteLine("SMOKE reader settings after open: " + State());
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Console.WriteLine("SMOKE reader settings after layout: " + State());
        await Program.WaitUntilAsync(() => page.IsQuickSettingsOpen && page.ReaderQuickSettings!.Surface.Bounds.Width > 0,
            () => "the reader settings popover did not open; " + State());
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        QuickSettingsSurface surface = page.ReaderQuickSettings!.Surface;
        surface.RefreshSession();
        return surface;
    }

    // A synthetic routed Button.Click on a shown gear: runs the XAML handler and the
    // toolbar's overflow bubble handler. This is GUI/routed-event coverage, not native input.
    internal static async Task CheckGearRoutesAsync(ArchivePage page, Window window)
    {
        string beforeView = page.ArchiveColumnModePreference;
        double beforeWidth = window.Width, beforeHeight = window.Height, beforeMinimum = window.MinWidth;
        ActionToolBar bar = page.FindControl<ActionToolBar>("ReaderToolbar")!;
        Button gear = page.FindControl<Button>("ReaderSettingsOpen")!;
        try
        {
            window.MinWidth = 0;
            foreach ((string view, double width, bool overflow) in new[]
            {
                // The large reader hides its Read button, so Simple still fits at 760.
                ("Advanced", 1600d, false), ("Simple", 760d, false),
                ("Barebones", 1600d, true), ("Barebones", 760d, true)
            })
            {
                await CloseAsync(page);
                page.ArchiveColumnModePreference = view;
                window.Width = width; window.Height = 960;
                await Program.WaitUntilAsync(() => { window.UpdateLayout(); return PresentationChecks.MatchesRequestedSize(window, width, 960); });
                for (int pass = 0; pass < 3; pass++)
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                Require(page.ReaderWorkView.ToString() == view && (bar.OverflowHost?.Children.Contains(gear) == true) == overflow,
                    $"Unexpected gear route in {view} at {width}: {page.ReaderWorkView}, overflow={bar.OverflowHost?.Children.Contains(gear)}.");
                if (overflow)
                {
                    bar.More!.IsChecked = true;
                    await Program.WaitUntilAsync(() => { window.UpdateLayout(); return gear.IsAttachedToVisualTree() && gear.Bounds.Width > 0 && gear.Bounds.Height > 0; },
                        () => "The gear was not measured in the open overflow.");
                }
                Require(gear.IsAttachedToVisualTree() && gear.IsEffectivelyVisible && gear.Bounds.Width > 0 && gear.IsEnabled,
                    "The clicked gear is not a shown, measured, enabled control.");
                gear.Focus();
                gear.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => page.IsQuickSettingsOpen && page.ReaderQuickSettings!.Surface.Bounds.Width > 0
                    && page.ReaderQuickSettings.Surface.Bounds.Height > 0 && page.ReaderQuickSettings.Surface.IsAttachedToVisualTree(),
                    () => $"The routed gear click did not leave a hosted, measured popover in {view} at {width}; open={page.IsQuickSettingsOpen}.");
                QuickSettingsPopover popover = page.ReaderQuickSettings!;
                Require(ReferenceEquals(popover.Flyout.Target, overflow ? bar.More : gear), "The popover did not use the stable route-specific anchor.");
                Require(!overflow || bar.More!.IsChecked != true, "The gear click did not dismiss overflow.");
                // Let the dismissal and its queued focus/layout work finish; IsOpen alone
                // can be momentarily true even when anchored to a closing popup.
                for (int pass = 0; pass < 3; pass++)
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                await Task.Delay(100);
                Require(page.IsQuickSettingsOpen && popover.Surface.IsAttachedToVisualTree()
                    && popover.Surface.Bounds.Width > 0 && popover.Surface.Bounds.Height > 0,
                    "The measured popover closed after overflow dismissal.");
                Console.WriteLine($"SMOKE reader settings routed gear: {view} {width}, anchor={(overflow ? "More" : "gear")}, surface={popover.Surface.Bounds.Size}, overflow={(overflow ? "dismissed" : "not used")}, stays open.");
                await CloseAsync(page);
                Require(!popover.Surface.IsAttachedToVisualTree(), "The closed popover still has a visual root.");
                // Also cover the original direct helper with overflow closed. This must
                // not select an effectively-visible but unhosted gear.
                QuickSettingsSurface surface = await OpenAsync(page);
                Require(surface.IsAttachedToVisualTree() && ReferenceEquals(popover.Flyout.Target, overflow ? bar.More : gear),
                    "The direct helper did not use the live stable anchor.");
                await CloseAsync(page);
            }
        }
        finally
        {
            await CloseAsync(page);
            if (bar.More != null) bar.More.IsChecked = false;
            page.ArchiveColumnModePreference = beforeView;
            window.MinWidth = beforeMinimum;
            window.Width = beforeWidth; window.Height = beforeHeight;
            for (int pass = 0; pass < 3; pass++)
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        }
    }

    // A separate page owns this disposal check: ending the shared sidebar page here
    // would invalidate the rest of that scenario (and the harness's close checks).
    internal static async Task CheckDisposalAsync(Window owner)
    {
        ArchivePage page = new() { QuickSettingsHost = StandIn(), ArchiveColumnModePreference = "Barebones" };
        Window? window = null;
        try
        {
            window = new Window { Width = 760, Height = 960, Content = page.DetachReader(), Title = "Reader settings lifetime probe" };
            window.Show(owner);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            QuickSettingsSurface surface = await OpenAsync(page);
            QuickSettingsPopover popover = page.ReaderQuickSettings!;
            Require(surface.IsAttachedToVisualTree(), "The lifetime fixture never hosted its popover.");
            // A second open request is the production toggle's cancellation path.
            page.OpenReaderQuickSettings();
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(!page.IsQuickSettingsOpen && !surface.IsAttachedToVisualTree(), "Toggling the popover closed left a delayed open behind.");
            // Reopen and end in the same dispatcher turn, before the popover's queued
            // FocusFirst callback runs. The page must own/dispose it, not leave an orphan.
            page.OpenReaderQuickSettings();
            Require(page.IsQuickSettingsOpen && ReferenceEquals(page.ReaderQuickSettings, popover), "The lifetime fixture did not reopen the same owned popover.");
            page.EndLifetime();
            Require(page.ReaderQuickSettings == null && !popover.IsOpen && !surface.IsAttachedToVisualTree(),
                "Ending the page did not dispose and detach its owned popover.");
            page.OpenReaderQuickSettings();
            popover.Show(page.FindControl<ActionToolBar>("ReaderToolbar")!.More!);
            for (int pass = 0; pass < 3; pass++)
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(page.ReaderQuickSettings == null && !popover.IsOpen && !surface.IsAttachedToVisualTree(),
                "An ended page or disposed popover reopened after queued dispatcher work.");
            Console.WriteLine("SMOKE reader settings lifetime: toggle cancels open; EndLifetime disposes/detaches before queued focus; ended page and disposed popover cannot reopen after dispatcher drain.");
        }
        finally
        {
            page.EndLifetime();
            window?.Close();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task CloseAsync(ArchivePage page)
    {
        if (!page.IsQuickSettingsOpen) return;
        page.ReaderQuickSettings!.Hide();
        await Program.WaitUntilAsync(() => !page.IsQuickSettingsOpen, () => "the reader settings popover did not close");
    }

    /// <summary>The switch of a session row (view option), or the button of an action row.</summary>
    internal static Control Row(QuickSettingsSurface surface, string commandId) =>
        surface.SessionControlOf(commandId) ?? throw new InvalidOperationException("The popover has no row for " + commandId + "; it has " + string.Join(", ", surface.SessionCommandIds));

    internal static CheckBox Switch(QuickSettingsSurface surface, string commandId) =>
        Row(surface, commandId) as CheckBox ?? throw new InvalidOperationException(commandId + " is not a switch row.");

    /// <summary>Opens the popover, reads one session switch, closes it again.</summary>
    internal static async Task<CheckBox> SwitchAsync(ArchivePage page, string commandId)
    {
        QuickSettingsSurface surface = await OpenAsync(page);
        return Switch(surface, commandId);
    }
}
