using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Commands;

public enum MacMenuKind { Command, Separator, Submenu }

/// <summary>One entry of the macOS menu bar. A command entry runs its command; an entry that lists <see cref="Resolves"/>
/// is the one menu entry for several context-dependent commands that share a gesture (Find, Back, page navigation): it
/// presses that gesture and the window's routing picks the command that applies where the focus is.</summary>
public sealed record MacMenuNode(MacMenuKind Kind, string? CommandId = null, string? TitleKey = null,
    IReadOnlyList<string>? Resolves = null, IReadOnlyList<MacMenuNode>? Items = null)
{
    public static readonly MacMenuNode Separator = new(MacMenuKind.Separator);
    public static MacMenuNode Command(string id) => new(MacMenuKind.Command, id);
    public static MacMenuNode Resolving(string titleKey, params string[] ids) => new(MacMenuKind.Command, ids[0], titleKey, ids);
    public static MacMenuNode Submenu(string titleKey, params MacMenuNode[] items) => new(MacMenuKind.Submenu, null, titleKey, null, items);
    /// <summary>The commands whose gestures this entry stands for.</summary>
    public IReadOnlyList<string> Commands => Resolves ?? (CommandId is null ? Array.Empty<string>() : new[] { CommandId });
}

public sealed record MacTopMenu(string? TitleKey, string Id, IReadOnlyList<MacMenuNode> Items);

/// <summary>The menu bar of the macOS build, generated from the command catalog and the main-page order. It carries no
/// gesture of its own that a context could not answer: single keys never appear, and a command that means different things
/// per page is one entry, not one per meaning.</summary>
public static class MacMenuModel
{
    public static IReadOnlyList<MacTopMenu> Build() => new[]
    {
        new MacTopMenu(null, "app", new[]
        {
            MacMenuNode.Command("navigation.about"), MacMenuNode.Separator,
            MacMenuNode.Command("navigation.settings"), MacMenuNode.Separator,
            MacMenuNode.Command("app.quit")
        }),
        new MacTopMenu("macmenu.file", "file", new[]
        {
            MacMenuNode.Command("location.open-folder"), MacMenuNode.Command("location.open-source"), MacMenuNode.Separator,
            MacMenuNode.Command("export.open-html-dialog"), MacMenuNode.Separator,
            MacMenuNode.Command("session.pause")
        }),
        new MacTopMenu("macmenu.edit", "edit", new[]
        {
            MacMenuNode.Command("selection.select-all"), MacMenuNode.Command("selection.copy-links"),
            MacMenuNode.Command("watch.import-clipboard"), MacMenuNode.Separator,
            MacMenuNode.Submenu("macmenu.find", new[]
            {
                MacMenuNode.Resolving("macmenu.find.entry", "input.focus-search", "input.focus-media-search",
                    "input.focus-download-search", "input.focus-settings-search", "input.focus-diagnostics-search", "reader.find"),
                MacMenuNode.Command("reader.find-next"), MacMenuNode.Command("reader.find-previous")
            })
        }),
        new MacTopMenu("macmenu.view", "view", MainPageOrder.CommandIds.Select(MacMenuNode.Command)
            .Append(MacMenuNode.Separator).Append(MacMenuNode.Command("window.fullscreen")).ToArray()),
        new MacTopMenu("macmenu.go", "go", new[]
        {
            MacMenuNode.Resolving("macmenu.back", "media.return", "reader.history-back", "gallery.return"),
            MacMenuNode.Resolving("macmenu.pagePrevious", "archive.previous-page", "pagination.previous"),
            MacMenuNode.Resolving("macmenu.pageNext", "archive.next-page", "pagination.next"), MacMenuNode.Separator,
            MacMenuNode.Command("navigation.continue-reading"), MacMenuNode.Command("input.focus-url")
        }),
        new MacTopMenu("macmenu.window", "window", new[] { MacMenuNode.Command("window.minimize") }),
        new MacTopMenu("macmenu.help", "help", new[] { MacMenuNode.Command("viewer.shortcut-help") })
    };

    /// <summary>Every command id the menu refers to (entries and what they resolve).</summary>
    public static IEnumerable<string> CommandIds(IEnumerable<MacMenuNode>? nodes = null) =>
        (nodes ?? Build().SelectMany(menu => menu.Items)).SelectMany(node => node.Kind == MacMenuKind.Submenu
            ? CommandIds(node.Items!) : node.Commands).Distinct(StringComparer.Ordinal);

    public static string Title(MacMenuNode node) => node.TitleKey is { } key ? Texts.T(key)
        : node.CommandId is { } id ? CommandCatalog.Label(id) : string.Empty;

    /// <summary>A gesture the menu may show and press: it needs Ctrl, Alt or the Command key. Bare keys (letters, F-keys,
    /// Delete, arrows) and Shift-only keys belong to the page that owns them and are never advertised in the menu bar.</summary>
    public static bool IsMenuGesture(ShortcutGesture gesture) =>
        gesture.Modifiers.Any(modifier => modifier is "Primary" or "Control" or "Ctrl" or "Meta" or "Alt");

    public static ShortcutGesture? Gesture(EffectiveShortcutBindings bindings, MacMenuNode node) =>
        node.Commands.SelectMany(bindings.For).FirstOrDefault(IsMenuGesture);

    /// <summary>The shortcut text of an entry: the chords of its first command that has any, in the platform's notation.</summary>
    public static string? Shortcut(EffectiveShortcutBindings bindings, MacMenuNode node)
    {
        foreach (string id in node.Commands)
        {
            string[] chords = bindings.For(id).Where(IsMenuGesture).Select(g => g.Display(bindings.Platform)).ToArray();
            if (chords.Length > 0) return string.Join(" / ", chords);
        }
        return null;
    }
}
