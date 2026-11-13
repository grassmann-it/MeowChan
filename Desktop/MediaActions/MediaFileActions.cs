using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop.MediaActions;

/// <summary>
/// Pure decisions behind the actions on a saved file that has no built-in viewer: what the status plate says and
/// whether the operating system's default program may be asked to open it.
/// </summary>
internal static class MediaFileActions
{
    // Types a downloaded file could run as a program or script when the desktop opens it. They are never handed to the
    // default program from here; "Show in folder" stays available for them.
    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".msi", ".msp", ".bat", ".cmd", ".lnk", ".url", ".reg", ".cpl", ".hta", ".dll",
        ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".jar", ".py", ".pyw", ".pl", ".rb", ".php",
        ".sh", ".bash", ".zsh", ".run", ".bin", ".appimage", ".desktop", ".command", ".app", ".workflow", ".apk",
        ".dmg", ".pkg", ".deb", ".rpm"
    };

    /// <summary>True when the file's type is one the default program may open (anything not runnable by itself).</summary>
    internal static bool CanOpenWithDefaultProgram(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !Runnable.Contains(Path.GetExtension(path));

    /// <summary>"PDF file" for a plain extension; "file" when there is none or it is not a plain extension.</summary>
    internal static string TypeLabel(string? path)
    {
        string extension = string.IsNullOrEmpty(path) ? string.Empty : Path.GetExtension(path).TrimStart('.');
        return extension.Length is > 0 and <= 8 && extension.All(char.IsAsciiLetterOrDigit)
            ? Texts.T("gallery.fileOnly.type", extension.ToUpperInvariant())
            : Texts.T("gallery.fileOnly.typeUnknown");
    }

    /// <summary>The sentence shown in place of a viewer: the file's type and size.</summary>
    internal static string FileOnlyText(GalleryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Texts.T("gallery.fileOnly.body", TypeLabel(entry.Asset.Path), SizeText.Format(entry.Asset.Length));
    }
}
