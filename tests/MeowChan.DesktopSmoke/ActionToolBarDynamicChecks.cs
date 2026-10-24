using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

// New lifecycle cases only. Existing size/language/mode matrices remain prior evidence.
internal static class ActionToolBarDynamicChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        var failures = new List<Exception>();
        foreach (string scenario in new[] { "disable-focused", "remove-focused", "remove-last", "remove-last-explicit-focus" })
        {
            try { await CheckOverflowAsync(owner, scenario); }
            catch (Exception error) { failures.Add(error); }
        }
        try { await CheckOpenToolsResizeAsync(owner); }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) throw new AggregateException("Dynamic action lifecycle checks failed.", failures);
        Console.WriteLine("SMOKE dynamic actions: three real overflow mutations, explicit focus preservation and open Tools language/resize focus transition passed; in-process input, not native keyboard/AT.");
    }

    private static async Task CheckOverflowAsync(MainWindow owner, string scenario)
    {
        bool enabled = true;
        int executions = 0;
        var command = new DelegateCommand(() => executions++, () => enabled);
        var anchor = new Button { Name = "DynamicAnchor", Content = "Direct", Width = 80 };
        var subject = new Button { Name = "DynamicSubject", Content = "Mutable action", Width = 150, Command = command };
        var peer = new Button { Name = "DynamicPeer", Content = "Remaining action", Width = 150 };
        ToolBar.SetOverflowMode(anchor, OverflowMode.Never);
        ToolBar.SetOverflowMode(subject, OverflowMode.Always);
        ToolBar.SetOverflowMode(peer, OverflowMode.Always);
        var toolbar = new ActionToolBar { Width = 250, Items = { anchor, subject } };
        bool last = scenario.StartsWith("remove-last", StringComparison.Ordinal);
        bool explicitFocus = scenario == "remove-last-explicit-focus";
        if (!last) toolbar.Items.Add(peer);
        var outside = new Button { Name = "ExplicitOutsideTarget", Content = "Outside toolbar" };
        Control content = explicitFocus ? new StackPanel { Children = { toolbar, outside } } : toolbar;
        var fixture = new Window { Width = 460, Height = 220, Content = content, Title = scenario };
        fixture.Show(owner);
        try
        {
            // Native activation selects the Window focus scope. Complete it before
            // assigning popup focus so a late activation cannot clear the fixture.
            await Program.WaitUntilAsync(() => fixture.IsActive);
            await Program.WaitUntilAsync(() => toolbar.More?.IsEffectivelyVisible == true
                && toolbar.OverflowHost?.Children.Contains(subject) == true);
            toolbar.More!.IsChecked = true;
            await Program.WaitUntilAsync(() => TopLevel.GetTopLevel(subject) is { IsVisible: true }
                && subject.Bounds.Width > 0);
            // Opening posts its initial focus at Loaded. Finish that setup callback
            // before selecting the subject and testing only the subsequent mutation.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(subject.Focus(NavigationMethod.Tab), "Cannot focus mutable original overflow action.");
            await Program.WaitUntilAsync(() => ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), subject));
            Trace("before-mutation");
            if (scenario == "disable-focused") { enabled = false; command.RaiseCanExecuteChanged(); }
            else toolbar.Items.Remove(subject);
            if (explicitFocus)
            {
                Require(outside.Focus(NavigationMethod.Tab), "Cannot set explicit outside focus before queued repair.");
                Require(ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), outside), "Explicit focus did not reach outside control.");
            }
            fixture.UpdateLayout();
            // This explicit-focus regression must observe queued Loaded repairs,
            // even when the immediate state already satisfies the final predicate.
            if (explicitFocus)
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            try
            {
                await Program.WaitUntilAsync(() =>
                {
                    fixture.UpdateLayout();
                    Control? focused = fixture.FocusManager?.GetFocusedElement() as Control;
                    bool focusOnMore = ReferenceEquals(focused, toolbar.More)
                        && ReferenceEquals(TopLevel.GetTopLevel(focused!), fixture);
                    bool focusOnPeer = ReferenceEquals(focused, peer)
                        && toolbar.OverflowHost?.Children.Contains(peer) == true
                        && ReferenceEquals(TopLevel.GetTopLevel(peer), TopLevel.GetTopLevel(toolbar.OverflowHost));
                    bool safeFocus = focused is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }
                        && focused.Bounds.Width > 0 && focused.Bounds.Height > 0
                        && TopLevel.GetTopLevel(focused) is { IsVisible: true } && (focusOnMore || focusOnPeer);
                    if (scenario == "disable-focused") return !subject.IsEffectivelyEnabled && safeFocus;
                    bool removed = !toolbar.Items.Contains(subject)
                        && toolbar.OverflowHost?.Children.Contains(subject) != true
                        && !subject.GetVisualAncestors().Contains(toolbar) && TopLevel.GetTopLevel(subject) == null;
                    Control expected = explicitFocus ? outside : anchor;
                    return removed && (last
                        ? toolbar.More!.IsChecked != true && !toolbar.More.IsVisible && ReferenceEquals(focused, expected)
                            && expected.IsEffectivelyVisible && expected.IsEffectivelyEnabled
                            && expected.Bounds.Width > 0 && expected.Bounds.Height > 0
                            && ReferenceEquals(TopLevel.GetTopLevel(expected), fixture)
                        : safeFocus);
                });
                Require(ReferenceEquals(subject.Command, command), "A mutation replaced the original command instance.");
                Require(executions == 0, "A lifecycle/focus change executed the removed or disabled command.");
                Trace("ready");
            }
            catch { Trace("failure"); throw; }
        }
        catch { Trace("failure"); throw; }
        finally { fixture.Close(); }

        void Trace(string stage)
        {
            Write("overflow-" + scenario + "-" + stage, new {
                stage, scenario, executions, command_enabled = command.CanExecute(null),
                window = fixture.Bounds.ToString(), fixture.IsActive, scale = fixture.RenderScaling,
                focus = Describe(fixture.FocusManager?.GetFocusedElement() as Control, fixture),
                subject = Describe(subject, fixture), anchor = Describe(anchor, fixture), peer = Describe(peer, fixture),
                explicit_outside = explicitFocus ? Describe(outside, fixture) : null,
                more = Describe(toolbar.More, fixture), more_open = toolbar.More?.IsChecked,
                items = toolbar.Items.OfType<Control>().Select(control => control.Name).ToArray(),
                overflow = toolbar.OverflowHost?.Children.Select(control => Describe(control, fixture)).ToArray()
            });
            Capture(fixture, "overflow-" + scenario + "-" + stage + ".png");
            if (toolbar.OverflowHost != null && TopLevel.GetTopLevel(toolbar.OverflowHost) is { IsVisible: true } popup)
                Capture(popup, "overflow-" + scenario + "-" + stage + "-popup.png");
        }
    }

    private static async Task CheckOpenToolsResizeAsync(MainWindow window)
    {
        string mode = window.SessionHost.Config.WorkspaceMode, language = Texts.Language;
        double width = window.Width, height = window.Height;
        Button tools = window.FindControl<Button>("ToolsNavigationButton")!;
        MenuFlyout menu = (MenuFlyout)tools.Flyout!;
        Button destination = window.FindControl<Button>("DiagnosticsNavigationButton")!;
        MenuItem item = menu.Items.OfType<MenuItem>().Single(value => value.Tag as string == "nav.diagnostics");
        try
        {
            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            window.Width = 760; window.Height = 500;
            await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - 760) <= 1 && tools.IsEffectivelyVisible && item.IsVisible);
            menu.ShowAt(tools);
            await Program.WaitUntilAsync(() => menu.IsOpen && TopLevel.GetTopLevel(item) is { IsVisible: true } && item.Bounds.Width > 0);
            Require(item.Focus(NavigationMethod.Tab), "Cannot focus actual Tools destination.");
            await Program.WaitUntilAsync(() => ReferenceEquals(window.FocusManager?.GetFocusedElement(), item));
            Texts.SetLanguage(language == "de" ? "en" : "de");
            window.UpdateLayout();
            Require(item.Header as string == Texts.T("nav.diagnostics"), "Open Tools retained a stale localized destination.");
            Require(ReferenceEquals(window.FocusManager?.GetFocusedElement(), item), "Open Tools language refresh lost its focused destination.");
            Trace("focused-open-after-language");
            window.Width = 1400; window.Height = 600;
            await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - 1400) <= 1 && !tools.IsVisible && destination.IsEffectivelyVisible);
            try
            {
                await Program.WaitUntilAsync(() => !menu.IsOpen
                    && ReferenceEquals(window.FocusManager?.GetFocusedElement(), destination)
                    && destination.IsEffectivelyEnabled && destination.Bounds.Width > 0 && destination.Bounds.Height > 0
                    && ReferenceEquals(TopLevel.GetTopLevel(destination), window));
                Trace("ready");
            }
            catch { Trace("failure"); throw; }
        }
        catch { Trace("failure"); throw; }
        finally
        {
            menu.Hide(); Texts.SetLanguage(language);
            await PresentationChecks.SwitchModeAsync(window, mode);
            window.Width = width; window.Height = height;
        }

        void Trace(string stage)
        {
            Write("footer-tools-" + stage, new { stage, width = window.Width, bounds = window.Bounds.ToString(),
                language = Texts.Language, mode = window.SessionHost.Config.WorkspaceMode, menu_open = menu.IsOpen,
                focus = Describe(window.FocusManager?.GetFocusedElement() as Control, window),
                tools = Describe(tools, window), destination = Describe(destination, window), item = Describe(item, window),
                direct = window.FindControl<StackPanel>("WorkspaceNavigationButtons")!.Children.Select(control => Describe(control, window)).ToArray(),
                visible_menu_items = menu.Items.OfType<MenuItem>().Where(value => value.IsVisible).Select(value => value.Tag).ToArray() });
            Capture(window, "footer-tools-" + stage + ".png");
            if (TopLevel.GetTopLevel(item) is { IsVisible: true } popup && !ReferenceEquals(popup, window))
                Capture(popup, "footer-tools-" + stage + "-popup.png");
        }
    }

    private static object? Describe(Control? control, Window window) => control == null ? null : new {
        type = control.GetType().Name, control.Name, control.IsVisible, control.IsEffectivelyVisible,
        control.IsEnabled, control.IsEffectivelyEnabled, control.Focusable, control.IsFocused, bounds = control.Bounds.ToString(),
        origin = control.TranslatePoint(default, window)?.ToString(), root = TopLevel.GetTopLevel(control)?.GetType().Name,
        root_visible = TopLevel.GetTopLevel(control)?.IsVisible,
        ancestors = control.GetVisualAncestors().OfType<Control>().Select(parent => parent.GetType().Name + ":" + parent.Name).ToArray() };

    private static void Write(string name, object value)
    {
        try
        {
            string json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
            Console.WriteLine("SMOKE dynamic actions " + json);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-dynamic-" + name + ".json"), json);
        }
        catch (Exception error) { Console.WriteLine("SMOKE dynamic diagnostic serialization failed: " + error); }
    }

    private static void Capture(Control control, string file)
    {
        try
        {
            double scale = (control as TopLevel)?.RenderScaling ?? TopLevel.GetTopLevel(control)?.RenderScaling ?? 1;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(control.Bounds.Width * scale),
                (int)Math.Ceiling(control.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
            bitmap.Render(control); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "smoke-dynamic-" + file), PngBitmapEncoderOptions.Default);
        }
        catch (Exception error) { Console.WriteLine("SMOKE dynamic diagnostic render failed: " + error); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
