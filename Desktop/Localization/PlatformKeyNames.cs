using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Localization;

/// <summary>
/// Names of modifier keys inside running texts (a hint that says "{0}+click"), where no gesture is shown by <see cref="ShortcutGesture.Display"/>.
/// The texts carry the modifier as a placeholder so that macOS shows the key it has (⌥, the same glyph as the shortcut labels) and not "Alt".
/// </summary>
internal static class PlatformKeyNames
{
    /// <summary>"Alt" on Windows and Linux, "⌥" on macOS.</summary>
    internal static string Alt(ShortcutPlatform platform) => platform == ShortcutPlatform.MacOS ? "⌥" : "Alt";

    /// <summary><see cref="Alt(ShortcutPlatform)"/> for the running system.</summary>
    internal static string Alt() => Alt(EffectiveShortcutBindings.CurrentPlatform);
}
