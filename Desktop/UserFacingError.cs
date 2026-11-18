using System;
using System.IO;
using MeowChan.Desktop.Localization;
using Serilog;

namespace MeowChan.Desktop;

/// <summary>
/// The cause of a failure as the window shows it (W3-15): a sentence in the user's language, never <see cref="Exception.Message"/>.
/// Framework and library messages are English, differ per platform and can name file paths, which diagnostics must not show; the
/// exception itself goes to the log, once, where it can be inspected. A refusal of a private list keeps its own localized
/// sentence (<see cref="StoreLimitText"/>), which names the setting to raise, and a folder catalog refusal its own
/// (<see cref="FolderSourceErrorText"/>). This is the one helper for window texts; folder source code only adds its path-free log.
/// </summary>
internal static class UserFacingError
{
    /// <summary>The localized cause of <paramref name="error"/> for a message template's detail slot, without logging.</summary>
    internal static string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (StoreLimitText.Describe(error) is { } limit) return limit;
        if (error is InvalidDataException && error.InnerException != null && StoreLimitText.Describe(error.InnerException) != null)
            return error.Message; // StoreLimitText.Localize already made this message the localized sentence.
        // A refusal of the folder catalog keeps its own sentence (the catalog's code, a damaged or newer catalog).
        if (FolderSourceErrorText.Describe(error) is { } folder) return folder;
        return error switch
        {
            UnauthorizedAccessException => Texts.T("error.cause.access"),
            IOException => Texts.T("error.cause.io"),
            _ => Texts.T("error.cause.other")
        };
    }

    /// <summary>
    /// <see cref="Describe"/> and the exception to the log under <paramref name="context"/> (a fixed English phrase, never a path).
    /// Call it once per failure, where the failure is handled.
    /// </summary>
    internal static string Report(Exception error, string context)
    {
        string cause = Describe(error);
        Log.Warning(error, "{Context} failed", context);
        return cause;
    }
}
