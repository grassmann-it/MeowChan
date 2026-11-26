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

    internal static bool ConsumeActivation(Button button)
    {
        return Close(button) || ConsumeAnchorPress(button);
    }

    /// <summary>
    /// True once when the pointer press that light-dismissed the button's menu landed on the button itself: the click that
    /// follows closes, it must not open the menu again. For owners that decide themselves whether an open menu closes.
    /// </summary>
    internal static bool ConsumeAnchorPress(Button button, object? target = null)
    {
        if (!Menus.TryGetValue(button, out ButtonMenu? state) || !state.AnchorPress || !Equals(state.Target, target)) return false;
        state.AnchorPress = false;
        return true;
    }

    internal static bool CanToggleMenuFromKey(ContextMenu menu, KeyEventArgs e) => !e.Handled && !ShortcutService.IsCapturing
        && !menu.Items.OfType<MenuItem>().Any(item => item.IsSubMenuOpen);

    internal static void Track(Button button, ContextMenu menu, object? target = null)
    {
        ButtonMenu state = Menus.GetValue(button, static anchor => new ButtonMenu(anchor));
        state.AnchorPress = false;
        state.Target = target;
        state.Menu = menu;
        TopLevel? root = TopLevel.GetTopLevel(button);
        menu.Opened += (_, _) => root?.AddHandler(InputElement.PointerPressedEvent,
            OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        root?.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        button.DetachedFromVisualTree += OnDetached;
        menu.Opened += (_, _) => Present(button, true);
        menu.Closed += (_, _) =>
        {
            if (!state.AnchorPress) RemoveHandlers();
            if (!ReferenceEquals(state.Menu, menu)) return;
            state.Menu = null;
            Present(button, false);
        };

        void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (ReferenceEquals(state.Menu, menu) && menu.IsOpen
                && e.GetCurrentPoint(button).Properties.IsLeftButtonPressed
                && new Rect(button.Bounds.Size).Contains(e.GetPosition(button)))
                state.AnchorPress = true;
        }

        void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // The button's Click runs before the release bubbles back to the root.
            state.AnchorPress = false;
            if (!menu.IsOpen) RemoveHandlers();
        }

        void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            state.AnchorPress = false;
            RemoveHandlers();
        }

        void RemoveHandlers()
        {
            root?.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            root?.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
            button.DetachedFromVisualTree -= OnDetached;
        }
    }

    private sealed class ButtonMenu
    {
        internal object? Target { get; set; }
        internal ContextMenu? Menu { get; set; }
        internal bool AnchorPress { get; set; }

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
