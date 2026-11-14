using System;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// The quick tagging limits of the profile (<see cref="Config.QuickTagSuggestionCount"/> and its six siblings, TP5) as the option
/// objects the quick tag types take. This is the one place that turns settings into options, so the gallery tag panel (TP2),
/// the tag popover (TP3) and the gallery marking (TP4) read the same values. The settings are already inside their range
/// (<see cref="Config.Validate"/>); the option types clamp once more, so a stale or hand-built <see cref="Config"/> cannot
/// produce a value outside it.
/// </summary>
internal static class QuickTagSettings
{
    /// <summary>The ranking limits: suggestions, recent tags, neighbour scan, file name words, source tags per medium.</summary>
    internal static TagContextOptions ContextOptions(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new TagContextOptions(config.QuickTagSuggestionCount, config.QuickTagRecentCount, config.QuickTagNeighbourScan,
            config.QuickTagFilenameTokens, config.QuickTagSourceTagsPerMedium, config.SlashTagSuggestionCount);
    }

    /// <summary>The surface limits: suggestion chips shown and undo steps (pass to <see cref="Controls.QuickTagSurface"/>).</summary>
    internal static QuickTagOptions SurfaceOptions(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new QuickTagOptions { UndoDepth = config.QuickTagUndoDepth, SuggestionCount = config.QuickTagSuggestionCount }.Normalized();
    }

    /// <summary>A recently used list of the configured size (0 keeps nothing, which switches the source off).</summary>
    internal static TagRecentList NewRecentList(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new TagRecentList(config.QuickTagRecentCount);
    }

    /// <summary>Applies a saved recent-tag limit without losing the newest entries; zero also forgets the old list.</summary>
    internal static void ApplyRecentLimit(TagRecentList recent, Config config)
    {
        ArgumentNullException.ThrowIfNull(recent);
        ArgumentNullException.ThrowIfNull(config);
        recent.Capacity = config.QuickTagRecentCount; // TagRecentList clamps and trims atomically.
    }

    /// <summary>A session over <paramref name="target"/> that keeps the configured number of undo steps.</summary>
    internal static QuickTagSession NewSession(IQuickTagTarget target, Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new QuickTagSession(target, SurfaceOptions(config).UndoDepth);
    }

    /// <summary>
    /// Media the gallery lets the user mark for one tag change: the setting, never above what the tag store accepts as
    /// assignments (<see cref="PrivateTagStore.MaximumMediaAssignments"/>).
    /// </summary>
    internal static int MarkedMediaLimit(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Math.Clamp(config.QuickTagMarkedMediaLimit, Config.QuickTagMarkedMediaLimitMinimum,
            Math.Min(Config.QuickTagMarkedMediaLimitMaximum, PrivateTagStore.MaximumMediaAssignments));
    }
}
