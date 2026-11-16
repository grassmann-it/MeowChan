using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

/// <summary>
/// The rule behind the gallery's keys, applied to the whole catalog (Tom's decision #133 of 16.11.2026, sweep S3): a default of the form Shift + one letter
/// shows or hides a panel or switches a mode. An action on the shown medium that uses the same form (Shift+M mutes, Shift+R rotates) is an exception, and
/// every exception is a conscious entry in <see cref="GalleryShortcutScheme.ShiftLetterActionIds"/> or <see cref="GalleryShortcutScheme.ShiftLetterOtherPanelIds"/>,
/// so a new Shift+letter default cannot drift in unnoticed. Plain objects; no window. (The gallery-only view of the same rule is in GalleryShortcutSchemeTests.)
/// </summary>
public sealed class ShiftLetterSchemeTests
{
    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    /// <summary>Every command with a default of the form Shift + one letter on a platform, with that letter (the rating keys Shift+1 to Shift+5 and Shift+0 are digits and no letters).</summary>
    private static SortedDictionary<string, string> ShiftLetterDefaults(ShortcutPlatform platform)
    {
        SortedDictionary<string, string> found = new(StringComparer.Ordinal);
        foreach (CommandDefinition command in CommandCatalog.All)
            foreach (ShortcutGesture gesture in EffectiveShortcutBindings.Defaults(command, platform).Where(GalleryShortcutScheme.IsShiftLetter))
                found.Add(command.Id, gesture.Key);
        return found;
    }

    [Theory, MemberData(nameof(Platforms))]
    public void EveryShiftLetterDefaultIsAPanelSwitchOrAnExplicitException(ShortcutPlatform platform)
    {
        SortedDictionary<string, string> found = ShiftLetterDefaults(platform);
        HashSet<string> allowed = GalleryShortcutScheme.PanelToggleIds
            .Concat(GalleryShortcutScheme.ShiftLetterActionIds).Concat(GalleryShortcutScheme.ShiftLetterOtherPanelIds).ToHashSet(StringComparer.Ordinal);
        string[] drifted = found.Keys.Where(id => !allowed.Contains(id)).ToArray();
        Assert.True(drifted.Length == 0, "Shift+letter default that is neither a panel switch nor a listed exception: "
            + string.Join(", ", drifted.Select(id => id + " (Shift+" + found[id] + ")")));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void TheExceptionListIsExactlyShiftMMuteShiftRRotateAndTheMediaFocusMode(ShortcutPlatform platform)
    {
        SortedDictionary<string, string> found = ShiftLetterDefaults(platform);
        // The actions on the shown medium (Tom's decision #133): mute and rotate, nothing else.
        Assert.Equal(new[] { "gallery.rotate", "video.mute" }, GalleryShortcutScheme.ShiftLetterActionIds.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("M", found["video.mute"]);
        Assert.Equal("R", found["gallery.rotate"]);
        // The one switch of another page in the same form: the Media page's focus mode.
        Assert.Equal(new[] { "media.focus-mode" }, GalleryShortcutScheme.ShiftLetterOtherPanelIds.ToArray());
        Assert.Equal("F", found["media.focus-mode"]);
        // No stale entry: every listed exception really has the form on this platform, and a panel switch has it or none at all.
        foreach (string id in GalleryShortcutScheme.ShiftLetterActionIds.Concat(GalleryShortcutScheme.ShiftLetterOtherPanelIds))
            Assert.True(found.ContainsKey(id), id);
        // An exception is no panel switch, and a panel switch is no exception.
        Assert.Empty(GalleryShortcutScheme.ShiftLetterActionIds.Intersect(GalleryShortcutScheme.PanelToggleIds));
        Assert.Empty(GalleryShortcutScheme.ShiftLetterOtherPanelIds.Intersect(GalleryShortcutScheme.PanelToggleIds));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void TheShiftLettersAreUniqueWhereTheirContextsMeetAndPlainMStaysTheFavouriteKey(ShortcutPlatform platform)
    {
        SortedDictionary<string, string> found = ShiftLetterDefaults(platform);
        foreach (var first in found)
            foreach (var second in found.Where(other => string.CompareOrdinal(other.Key, first.Key) > 0))
                if (first.Value == second.Value)
                    Assert.False(EffectiveShortcutBindings.ContextsOverlap(first.Key, second.Key), first.Key + " and " + second.Key + " share Shift+" + first.Value);
        // Plain M adds a favourite (revision 20), so Shift+M, the old mute key, is the exception that keeps the mute where players expect it.
        Assert.Equal(new[] { "M" }, EffectiveShortcutBindings.Defaults(CommandCatalog.Get("favorites.add"), platform).Select(gesture => gesture.Key).ToArray());
    }
}
