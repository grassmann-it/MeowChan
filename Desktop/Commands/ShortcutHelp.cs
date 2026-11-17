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

    internal static string Append(string text, string help) => help.Length == 0 ? text : text + "\n" + help;
}
