using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// The macOS menu bar, generated from MacMenuModel (command catalog + main-page order). Entries never own a key equivalent
// (NativeMenuItem.Gesture stays null, as for the tray): the window's routing owns every gesture because only it knows
// the page, the focus and the open dialogs. A menu click therefore presses the entry's gesture on the focused element and
// lets that routing decide; commands without a gesture run directly. Guarded by OperatingSystem.IsMacOS().
public partial class MainWindow
{
    private NativeMenu? _macMenu;

    internal NativeMenu? MacMenuForTests => _macMenu;

    private void InstallMacMenu()
    {
        if (!OperatingSystem.IsMacOS() || _macMenu != null) return;
        try
        {
            _macMenu = BuildMacMenu();
            NativeMenu.SetMenu(this, _macMenu);
        }
        catch (Exception error)
        {
            // A broken menu bar must never keep the application from starting; the window shortcuts still work.
            Serilog.Log.Warning(error, "Could not create the macOS menu bar.");
            _macMenu = null;
        }
    }

    private NativeMenu BuildMacMenu()
    {
        NativeMenu bar = new();
        foreach (MacTopMenu top in MacMenuModel.Build())
        {
            NativeMenuItem title = new(top.TitleKey is { } key ? Texts.T(key) : "MeowChan") { Menu = BuildMacMenuItems(top.Items) };
            if (top.TitleKey is { } titleKey)
            {
                Action refresh = () => title.Header = Texts.T(titleKey);
                Texts.Changed += (_, _) => refresh();
            }
            bar.Items.Add(title);
        }
        return bar;
    }

    private NativeMenu BuildMacMenuItems(IReadOnlyList<MacMenuNode> nodes)
    {
        NativeMenu menu = new();
        foreach (MacMenuNode node in nodes)
        {
            if (node.Kind == MacMenuKind.Separator) { menu.Items.Add(new NativeMenuItemSeparator()); continue; }
            if (node.Kind == MacMenuKind.Submenu)
            {
                string submenuKey = node.TitleKey!;
                NativeMenuItem submenu = new(Texts.T(submenuKey)) { Menu = BuildMacMenuItems(node.Items!) };
                Texts.Changed += (_, _) => submenu.Header = Texts.T(submenuKey);
                menu.Items.Add(submenu);
                continue;
            }
            MacMenuNode captured = node;
            NativeMenuItem item = CommandBindings.CreateNativeMenuItem(node.CommandId!, () => RunMacMenuNode(captured),
                () => CanRunMacMenuNode(captured), () => MacMenuModel.Title(captured),
                () => MacMenuModel.Shortcut(ShortcutService.Current, captured));
            menu.Items.Add(item);
        }
        return menu;
    }

    private bool CanRunMacMenuNode(MacMenuNode node) => _workspaceInitialized && !_isClosing && IsEnabled && !HasVisibleShellDialog()
        && (node.CommandId != "navigation.continue-reading" || CanResumeReading);

    private void RunMacMenuNode(MacMenuNode node)
    {
        if (!CanRunMacMenuNode(node)) return;
        switch (node.CommandId)
        {
            case "navigation.about": WorkspaceControl<MenuItem>("AboutMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); return;
            case "app.quit": _ = CloseApplicationAsync(); return;
            case "navigation.continue-reading": _ = ResumeLastReadingAsync(); return;
        }
        foreach (string id in node.Commands)
            if (PressMenuGesture(id)) return;
    }

    // Presses the first chord of a command on the focused element, like the keyboard would; false when it has none
    // (for example after the user removed it) or nothing handled it.
    private bool PressMenuGesture(string id)
    {
        ShortcutGesture? gesture = ShortcutService.Current.For(id).FirstOrDefault(MacMenuModel.IsMenuGesture);
        if (gesture == null) return false;
        KeyGesture resolved = gesture.Resolve(ShortcutService.Current.Platform);
        InputElement target = FocusManager?.GetFocusedElement() as InputElement ?? this;
        KeyEventArgs press = new()
        {
            RoutedEvent = InputElement.KeyDownEvent, Source = target, Key = resolved.Key, KeyModifiers = resolved.KeyModifiers,
            KeySymbol = gesture.Symbol
        };
        target.RaiseEvent(press);
        return press.Handled;
    }
}
