using Avalonia;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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
        TopLevel? root = TopLevel.GetTopLevel(button);
        root?.AddHandler(InputElement.PointerPressedEvent, DismissOnAnchor, RoutingStrategies.Tunnel, handledEventsToo: true);
        menu.Opened += (_, _) => Present(button, true);
        menu.Closed += (_, _) =>
        {
            root?.RemoveHandler(InputElement.PointerPressedEvent, DismissOnAnchor);
            if (!ReferenceEquals(state.Menu, menu)) return;
            state.Menu = null;
            Present(button, false);
        };

        void DismissOnAnchor(object? sender, PointerPressedEventArgs e)
        {
            if (!ReferenceEquals(state.Menu, menu) || !menu.IsOpen || e.Source is not Visual source
                || !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            // Before Avalonia's light-dismiss layer forwards the press, consume an activation on the anchor.
            bool onAnchor = source.GetSelfAndVisualAncestors().Contains(button)
                || root != null && new Rect(button.Bounds.Size).Contains(e.GetPosition(button))
                    && root.InputHitTest(e.GetPosition(root), visual => !ReferenceEquals(visual, source)) is Visual underlying
                    && underlying.GetSelfAndVisualAncestors().Contains(button);
            if (!onAnchor) return;
            e.Handled = true;
            menu.Close();
        }
    }

    private sealed class ButtonMenu
    {
        internal ContextMenu? Menu { get; set; }

        internal ButtonMenu(Button button)
        {
            button.DetachedFromVisualTree += (_, _) => Close(button);
            button.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty && !button.IsVisible) Close(button);
            };
        }
    }
}
