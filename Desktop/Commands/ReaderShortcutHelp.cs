using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Commands;

internal static class ReaderShortcutHelp
{
    private static string GestureOrLabel(string id, string context) =>
        CommandCatalog.ShortcutTooltipDisplay(id, context) ?? CommandCatalog.Label(id);

    internal static string PlayHint(string context = "reader") => Texts.T("reader.video.playHint",
        GestureOrLabel("reader.media.activate", context), GestureOrLabel("video.play-pause", context));

    internal static string ActivationNote()
    {
        string[] keys = new[] { "reader.media.activate", "video.play-pause" }
            .Select(id => CommandCatalog.ShortcutTooltipDisplay(id, "reader")).OfType<string>().ToArray();
        return Texts.T("help.reader.mediaActivation", keys.Length == 0
            ? CommandCatalog.Label("reader.media.activate") : string.Join(" / ", keys));
    }

    internal static string Note(string key) => key == "help.reader.mediaActivation" ? ActivationNote() : Texts.T(key);
}
