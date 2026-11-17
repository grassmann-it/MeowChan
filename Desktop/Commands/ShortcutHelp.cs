using System;
using System.Linq;

namespace MeowChan.Desktop.Commands;

/// <summary>Point-of-use help from the same snapshot and execution context as routing.</summary>
internal static class ShortcutHelp
{
    internal static string Commands(string context, params string[] ids) =>
        Commands(context, OperatingSystem.IsMacOS(), ShortcutService.Current, ids);

    internal static string Commands(string context, bool macOS, EffectiveShortcutBindings bindings, params string[] ids) =>
        string.Join("\n", ids.Select(id => (Id: id, Keys: CommandCatalog.ShortcutTooltipDisplay(id, macOS, context, bindings)))
            .Where(item => item.Keys != null)
            .Select(item => CommandCatalog.Label(item.Id) + " · " + item.Keys));

    internal static bool ActsHere(string id, ShortcutGesture gesture, string context, EffectiveShortcutBindings bindings)
    {
        if (bindings.YieldedContexts(id, gesture).Contains(context, StringComparer.Ordinal)) return false;
        if (!id.StartsWith("video.", StringComparison.Ordinal) || context is not ("reader" or "media")) return true;
        // These surfaces deliberately route video after their own commands, not through
        // the catalogue's conflict map. Ask those very ownership policies as routing does.
        if (!ReaderVideoKeys.IsVideoCommand(id)) return false;
        var key = gesture.Resolve(bindings.Platform);
        var press = new Avalonia.Input.KeyEventArgs { Key = key.Key, KeyModifiers = key.KeyModifiers, KeySymbol = gesture.Symbol };
        bool Matches(string command) => bindings.Matches(press, command, context);
        return !(context == "reader" ? ReaderVideoKeys.ReaderClaims(Matches) : MediaPanelKeys.PageClaims(Matches));
    }

    internal static string Append(string text, string help) => help.Length == 0 ? text : text + "\n" + help;
}
