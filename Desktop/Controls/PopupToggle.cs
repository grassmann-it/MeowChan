using Avalonia;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>Opt-in state and dismissal for the button that owns a transient popup.</summary>
internal static class PopupToggle
{
    private static readonly ConditionalWeakTable<Button, ButtonMenu> Menus = new();

    internal static void Prepare(Button button, string? automationId = null)
    {
        button.Focusable = false;
        if ((automationId ?? button.Name) is { } id) AutomationProperties.SetAutomationId(button, id);
        Present(button, false);
    }

    internal static void Present(Button button, bool open)
    {
        button.Classes.Set("popup-open", open);
        AutomationProperties.SetItemStatus(button, Texts.T(open ? "shortcuts.group.expanded" : "shortcuts.group.collapsed"));
    }

    internal static void BindFlyout(Button button, FlyoutBase flyout, string? automationId = null)
    {
        Prepare(button, automationId);
        button.Flyout = flyout;
        flyout.Opened += (_, _) => Present(button, ReferenceEquals(flyout.Target, button));
        flyout.Closed += (_, _) => Present(button, false);
    }

    internal static bool Close(Button button)
    {
        if (!Menus.TryGetValue(button, out ButtonMenu? state) || state.Menu is not { IsOpen: true } menu) return false;
        menu.Close();
        return true;
    }

    internal static bool CanToggleMenuFromKey(ContextMenu menu, KeyEventArgs e) => !e.Handled && !ShortcutService.IsCapturing
        && !menu.Items.OfType<MenuItem>().Any(item => item.IsSubMenuOpen);

    internal static void Track(Button button, ContextMenu menu)
    {
        ButtonMenu state = Menus.GetValue(button, static anchor => new ButtonMenu(anchor));
        state.Menu = menu;
        menu.Opened += (_, _) => Present(button, true);
        menu.Closed += (_, _) =>
        {
            if (!ReferenceEquals(state.Menu, menu)) return;
            state.Menu = null;
            Present(button, false);
        };
    }

    private sealed class ButtonMenu
    {
        internal ContextMenu? Menu { get; set; }

        internal ButtonMenu(Button button)
        {
            // Avalonia forwards the outside press before light-dismissing a ContextMenu. Consume
            // a press on its own anchor while it is still open, so the release can't reopen it.
            button.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                if (e.GetCurrentPoint(button).Properties.IsLeftButtonPressed && Close(button)) e.Handled = true;
            }, RoutingStrategies.Tunnel);
            button.DetachedFromVisualTree += (_, _) => Close(button);
            button.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty && !button.IsVisible) Close(button);
            };
        }
    }
}
