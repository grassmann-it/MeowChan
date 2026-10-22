using System;
using System.IO;
using MeowChan.Boards.Base;
using MeowChan.Interfaces;

namespace MeowChan.Storage;

/// <summary>Creates portable paths from stable source identifiers, never from original upload names.</summary>
public static class StorageLayout
{
    internal static string GetBoardDirectory(string root, IBoard board) =>
        board.ImageBoard is BaseImageBoard { UsesOpaqueStorageIdentifiers: true }
            ? CombineInsideRoot(root, EncodePluginSegment(board.ImageBoard.Name), EncodePluginSegment(board.BoardCode))
            : GetBoardDirectory(root, board.ImageBoard.Name, board.BoardCode);

    internal static string GetThreadDirectory(string root, IThread thread) =>
        thread.Board.ImageBoard is BaseImageBoard { UsesOpaqueStorageIdentifiers: true }
            ? CombineInsideRoot(root, EncodePluginSegment(thread.Board.ImageBoard.Name),
                EncodePluginSegment(thread.Board.BoardCode), EncodePluginSegment(thread.ThreadId))
            : GetThreadDirectory(root, thread.Board.ImageBoard.Name, thread.Board.BoardCode, thread.ThreadId);

    internal static string GetPluginMediaPath(string root, string source, string board, string thread,
        string mediaId, string extension, string variant)
    {
        MeowChan.Rules.MediaRuleScope.ValidateIdentity(variant, nameof(variant));
        string suffix = variant == "original" ? string.Empty
            : "~" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(variant))).ToLowerInvariant();
        string fileName = EncodePluginSegment(mediaId) + suffix + ValidateExtension(extension, 32);
        return CombineInsideRoot(root, EncodePluginSegment(source), EncodePluginSegment(board), EncodePluginSegment(thread), fileName);
    }

    private static string EncodePluginSegment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        try { return ValidateSegment(value, nameof(value)); }
        catch (ArgumentException)
        {
            // '~' is outside the legacy ASCII namespace. Raw identities never become filesystem paths.
            return "~opaque-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        }
    }

    public static string GetBoardDirectory(string root, string source, string board)
    {
        return CombineInsideRoot(root, ValidateSegment(source, nameof(source)), ValidateSegment(board, nameof(board)));
    }

    public static string GetThreadDirectory(string root, string source, string board, string thread)
    {
        return CombineInsideRoot(root, ValidateSegment(source, nameof(source)),
            ValidateSegment(board, nameof(board)), ValidateSegment(thread, nameof(thread)));
    }

    public static string GetMediaPath(string root, string source, string board, string thread, string mediaId, string ext)
    {
        string fileName = ValidateSegment(mediaId, nameof(mediaId)) + ValidateExtension(ext);
        return CombineInsideRoot(root, ValidateSegment(source, nameof(source)),
            ValidateSegment(board, nameof(board)), ValidateSegment(thread, nameof(thread)), fileName);
    }

    /// <summary>Original paths remain unchanged; other variants use a separate filename namespace.</summary>
    public static string GetMediaPath(string root, string source, string board, string thread, string mediaId, string ext, string variant)
    {
        MeowChan.Rules.MediaRuleScope.ValidateIdentity(variant, nameof(variant));
        if (variant == "original") return GetMediaPath(root, source, board, thread, mediaId, ext);
        string variantHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(variant))).ToLowerInvariant();
        string fileName = ValidateSegment(mediaId, nameof(mediaId)) + "~" + variantHash + ValidateExtension(ext);
        return CombineInsideRoot(root, ValidateSegment(source, nameof(source)), ValidateSegment(board, nameof(board)), ValidateSegment(thread, nameof(thread)), fileName);
    }

    private static string ValidateSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new ArgumentException("A storage identifier must contain between 1 and 128 characters.", parameterName);
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_' && character != '-')
            {
                throw new ArgumentException("Storage identifiers may contain only ASCII letters, digits, underscores and hyphens.", parameterName);
            }
        }

        string upper = value.ToUpperInvariant();
        bool reservedDevice = upper is "CON" or "PRN" or "AUX" or "NUL"
            || (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal)
                || upper.StartsWith("LPT", StringComparison.Ordinal)) && upper[3] >= '1' && upper[3] <= '9');
        if (reservedDevice)
        {
            throw new ArgumentException("Storage identifiers cannot use reserved Windows device names.", parameterName);
        }

        return value;
    }

    private static string ValidateExtension(string extension, int maximumLength = 12)
    {
        if (string.IsNullOrEmpty(extension) || extension.Length < 2 || extension.Length > maximumLength + 1 || extension[0] != '.')
        {
            throw new ArgumentException($"A media extension must be a dot followed by 1 to {maximumLength} ASCII letters or digits.", nameof(extension));
        }

        for (int index = 1; index < extension.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(extension[index]))
            {
                throw new ArgumentException("A media extension must contain only ASCII letters or digits.", nameof(extension));
            }
        }

        return extension.ToLowerInvariant();
    }

    private static string CombineInsideRoot(string root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A download root is required.", nameof(root));
        }

        string absoluteRoot = Path.GetFullPath(root);
        string candidate = absoluteRoot;
        foreach (string segment in segments)
        {
            candidate = Path.Combine(candidate, segment);
        }

        candidate = Path.GetFullPath(candidate);
        string rootPrefix = Path.EndsInDirectorySeparator(absoluteRoot)
            ? absoluteRoot : absoluteRoot + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootPrefix, comparison))
        {
            throw new ArgumentException("The media path must remain inside the download root.", nameof(root));
        }

        return candidate;
    }
}
