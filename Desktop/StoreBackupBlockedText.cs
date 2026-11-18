using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

/// <summary>The window text of a save that was refused because its backup may not be rotated away (names the file, not the path).</summary>
internal static class StoreBackupBlockedText
{
    internal static string For(ProfileBackupBlockedException blocked) => Texts.T(blocked.Reason == ProfileBackupBlockReason.NewerVersion
        ? "store.backupBlocked.newer" : "store.backupBlocked.unreadable", blocked.FileName);
}
