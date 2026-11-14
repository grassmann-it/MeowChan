using System;
using System.Globalization;
using System.IO;
using MeowChan.Desktop.Localization;
using MeowChan.FolderSources;

namespace MeowChan.Desktop;

/// <summary>
/// What the window says when a folder source action or view fails (W3-15): a localized sentence chosen by the kind of failure,
/// never an exception text (English .NET or SQLite wording that may name a path). The details go to the log as type and error
/// codes only, since folder source diagnostics never name paths or file names.
/// </summary>
internal static class FolderSourceErrorText
{
    /// <summary>The text key for a failure: the catalog's own code, a damaged catalog, or a passing read/write problem.</summary>
    internal static string Key(Exception error) => error switch
    {
        FolderSourceException folder => FolderSourceDialog.ErrorKey(folder.Error),
        InvalidDataException { InnerException: FolderSourceException folder } => FolderSourceDialog.ErrorKey(folder.Error),
        InvalidDataException or Microsoft.Data.Sqlite.SqliteException => "folderSource.error.damaged",
        _ => "folderSource.error.io"
    };

    internal static string Message(Exception error) => Texts.T(Key(error));

    /// <summary>The localized sentence when <paramref name="error"/> came from the folder catalog; null for any other failure.</summary>
    internal static string? Describe(Exception error) =>
        error is FolderSourceException or InvalidDataException { InnerException: FolderSourceException } ? Message(error) : null;

    /// <summary>Logs a failure with its type and codes (no message, no path).</summary>
    internal static void Log(Exception error, string operation) =>
        Serilog.Log.Warning("Folder source {Operation} failed: {Error}", operation, Code(error));

    /// <summary>The diagnostic code of a failure: the catalog code, the SQLite code or the exception type with its HRESULT.</summary>
    internal static string Code(Exception error) => error switch
    {
        FolderSourceException folder => "FolderSource." + folder.Error,
        InvalidDataException { InnerException: FolderSourceException folder } => "FolderSource." + folder.Error,
        Microsoft.Data.Sqlite.SqliteException sqlite => "SqliteException " + sqlite.SqliteErrorCode.ToString(CultureInfo.InvariantCulture)
            + "/" + sqlite.SqliteExtendedErrorCode.ToString(CultureInfo.InvariantCulture),
        _ => error.GetType().Name + " 0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture)
    };
}
