using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

// The media conflict list reloads after every decision. A rejected Keep/Promote/Discard must stay
// visible afterwards: the reload is not a new user decision and never erases what the last action
// reported. Separate, composable slots keep that honest without guessing precedence per call site.
internal enum MediaConflictStatusTone { None = 0, Information = 1, Failure = 2 }

internal readonly record struct MediaConflictStatus(MediaConflictStatusTone Tone, string Key, object?[] Args)
{
    internal bool IsEmpty => Tone == MediaConflictStatusTone.None;
    internal string Resolve() => IsEmpty ? string.Empty : Texts.T(Key, Args);

    internal static MediaConflictStatus Failure(string key, params object?[] args) =>
        new(MediaConflictStatusTone.Failure, key, args);

    internal static MediaConflictStatus Information(string key, params object?[] args) =>
        new(MediaConflictStatusTone.Information, key, args);
}

internal sealed class MediaConflictStatusBoard
{
    // Set by the last decision/reclaim outcome; only a new action outcome replaces it.
    private MediaConflictStatus _action;
    // Set by the last list read outcome; a successful read clears it, a failed read does not.
    private MediaConflictStatus _read;
    private bool _emptyList;

    /// <summary>What the user must see now: the last action outcome wins over a list-read outcome.</summary>
    internal MediaConflictStatus Display
    {
        get
        {
            if (!_action.IsEmpty) return _action;
            if (!_read.IsEmpty) return _read;
            return _emptyList
                ? MediaConflictStatus.Information("transfer.conflicts.empty")
                : default;
        }
    }

    internal void DecisionRecorded() => _action = default;

    internal void DecisionFailed(MediaConflictStatus status) => _action = status;

    internal void ListRead(int loadedCount)
    {
        _read = default;
        _emptyList = loadedCount == 0;
    }

    internal void ListReadFailed() => _read = MediaConflictStatus.Failure("transfer.conflicts.error.read");

    internal void Reclaim(MediaConflictStatus status) => _action = status;
}
