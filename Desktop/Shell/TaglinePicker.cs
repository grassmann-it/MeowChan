using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MeowChan.Desktop;

/// <summary>What the picker chose: the quip (null for "show none") and whether it is the release highlight of a new version.</summary>
internal readonly record struct TaglinePick(TaglineEntry? Entry, bool ReleaseHighlight);

/// <summary>
/// Chooses the header quip. Pure: the date, the random source and the versions are passed in, so every rule is deterministic in a test.
/// Rules (Tom, 14.11.2026): Off shows nothing; Fixed shows the stored quip; Daily one per calendar day (a stable hash of the date, never
/// <c>string.GetHashCode</c>, which differs from process to process); PerStart a random one at every start; Interval picks like PerStart at the start and the window then replaces it every N minutes (<see cref="PickAnother"/>). The first start after an
/// update shows the quip of the new release, once, in Daily, PerStart and Interval; Fixed is the user's explicit choice and Off the explicit
/// "none", so neither is overridden.
/// </summary>
internal static class TaglinePicker
{
    internal const string ModeOff = "Off", ModeFixed = "Fixed", ModeDaily = "Daily", ModePerStart = "PerStart", ModeInterval = "Interval";

    internal static IReadOnlyList<string> Modes { get; } = [ModeOff, ModeFixed, ModeDaily, ModePerStart, ModeInterval];

    /// <summary>A stored mode as the setting keeps it (case-insensitive); anything else is the default, a new quip at every start.</summary>
    internal static string NormalizeMode(string? value)
    {
        string? trimmed = value?.Trim();
        foreach (string mode in Modes)
            if (string.Equals(mode, trimmed, StringComparison.OrdinalIgnoreCase)) return mode;
        return ModePerStart;
    }

    /// <summary>FNV-1a (32 bit) over the UTF-8 bytes of <c>yyyy-MM-dd</c>: the same value in every process on every machine.</summary>
    internal static uint DayHash(DateOnly day)
    {
        uint hash = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            hash = (hash ^ b) * 16777619;
        return hash;
    }

    /// <param name="mode">A <c>Config.HeaderTagline</c> value.</param>
    /// <param name="fixedId">The stored id for the Fixed mode.</param>
    /// <param name="pool">The quips available in the active language (<see cref="TaglineCatalog.Available"/>).</param>
    /// <param name="today">The local calendar day (Daily).</param>
    /// <param name="current">This version of MeowChan; null (unreadable) never produces a release highlight.</param>
    /// <param name="lastSeen">The version whose release quip the profile has already had its chance to see; null when none.</param>
    /// <param name="atStart">True only for the pick made at application start; the release highlight belongs to the start.</param>
    /// <param name="random">The random source of PerStart.</param>
    internal static TaglinePick Pick(string mode, string fixedId, IReadOnlyList<TaglineEntry> pool, DateOnly today,
        Version? current, Version? lastSeen, bool atStart, Random random)
    {
        mode = NormalizeMode(mode);
        if (mode == ModeOff || pool.Count == 0) return default;
        TaglineEntry[] ordered = pool.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
        if (mode == ModeFixed)
            return new TaglinePick(ordered.FirstOrDefault(entry => entry.Id == fixedId)
                ?? ordered.FirstOrDefault(entry => entry.Id == TaglineCatalog.DefaultId) ?? ordered[0], false);

        if (atStart && ReleaseCandidates(ordered, current, lastSeen) is { Length: > 0 } release)
            return new TaglinePick(Choose(release, mode, today, random), true);
        return new TaglinePick(Choose(ordered, mode, today, random), false);
    }

    /// <summary>
    /// The release quips of the newest release the profile has not seen: those introduced after <paramref name="lastSeen"/> (none seen: ever) and not
    /// after <paramref name="current"/>, restricted to the highest such version. Empty when the profile is up to date or the version is unknown.
    /// </summary>
    internal static TaglineEntry[] ReleaseCandidates(IReadOnlyList<TaglineEntry> pool, Version? current, Version? lastSeen)
    {
        if (current == null || lastSeen != null && lastSeen >= current) return [];
        Version floor = lastSeen ?? new Version(0, 0, 0);
        TaglineEntry[] newer = pool.Where(entry => entry.Category == TaglineCategory.Release && entry.Since > floor && entry.Since <= current).ToArray();
        if (newer.Length == 0) return [];
        Version latest = newer.Max(entry => entry.Since)!;
        return newer.Where(entry => entry.Since == latest).ToArray();
    }

    /// <summary>"Another tagline": a random quip other than <paramref name="currentId"/> (the same one when it is the only one).</summary>
    internal static TaglineEntry? PickAnother(IReadOnlyList<TaglineEntry> pool, string? currentId, Random random)
    {
        TaglineEntry[] others = pool.Where(entry => entry.Id != currentId).OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
        if (others.Length > 0) return others[random.Next(others.Length)];
        return pool.Count == 0 ? null : pool[0];
    }

    private static TaglineEntry Choose(TaglineEntry[] ordered, string mode, DateOnly today, Random random) =>
        mode == ModeDaily ? ordered[(int)(DayHash(today) % (uint)ordered.Length)] : ordered[random.Next(ordered.Length)];
}
