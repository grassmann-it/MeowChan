using System;

namespace MeowChan.Tags;

/// <summary>One user setting with its default and inclusive bounds.</summary>
internal readonly record struct TagSuggestionSettingRange(int Default, int Minimum, int Maximum)
{
    internal bool Contains(int value) => value >= Minimum && value <= Maximum;

    internal int Clamp(int value) => Math.Clamp(value, Minimum, Maximum);
}

/// <summary>
/// User settings of the tagger contract (tag-suggestions-v1). Every limit is a setting with default, minimum and maximum;
/// SDK-facing maxima respect the tagger contract (MeowChan.PluginSdk.TaggerLimits). Sizes are binary units (MiB). Not yet wired to the desktop settings page (see
/// docs/development/tagger-contract-1.2.0.md); until then the host uses <see cref="Default"/>.
/// </summary>
internal sealed record TagSuggestionSettings
{
    /// <summary>Display and preselection threshold in percent. Raw scores are stored; the threshold only filters the view.</summary>
    internal static readonly TagSuggestionSettingRange ConfidenceThresholdRange = new(35, 5, 95);
    /// <summary>Media per tagger call.</summary>
    internal static readonly TagSuggestionSettingRange BatchSizeRange = new(8, 1, 64);
    /// <summary>Largest medium sent to a tagger, MiB. Images are additionally bounded by the SDK image budget (32 MiB).</summary>
    internal static readonly TagSuggestionSettingRange MaximumInputMiBRange = new(32, 1, 256);
    /// <summary>Most suggestions kept per medium and run.</summary>
    internal static readonly TagSuggestionSettingRange MaximumSuggestionsPerItemRange = new(64, 1, 512);
    /// <summary>Time limit of one tagger call, seconds.</summary>
    internal static readonly TagSuggestionSettingRange BatchTimeoutSecondsRange = new(120, 10, 3600);
    /// <summary>Size limit of tag-suggestions.json, MiB. Lowering it never deletes; it only stops further growth.</summary>
    internal static readonly TagSuggestionSettingRange StoreLimitMiBRange = new(32, 4, 256);

    internal static readonly TagSuggestionSettingRange ReviewReservePercentRange = new(10, 5, 50);
    internal static readonly TagSuggestionSettingRange BatchReadMiBRange = new(256, 1, 1024);
    internal static readonly TagSuggestionSettingRange BatchReadCountRange = new(8192, 1, 65536);
    internal static readonly TagSuggestionSettingRange ConcurrentReadsRange = new(4, 1, 16);
    internal static readonly TagSuggestionSettingRange BatchHttpRequestsRange = new(16, 1, 128);

    internal static TagSuggestionSettings Default { get; } = new();

    /// <summary>The confidence floor sent to taggers: the lowest selectable threshold, so raising or lowering the threshold needs no re-run.</summary>
    internal static double ConfidenceFloor => ConfidenceThresholdRange.Minimum / 100.0;

    internal int ConfidenceThresholdPercent { get; init; } = ConfidenceThresholdRange.Default;
    internal int BatchSize { get; init; } = BatchSizeRange.Default;
    internal int MaximumInputMiB { get; init; } = MaximumInputMiBRange.Default;
    internal int MaximumSuggestionsPerItem { get; init; } = MaximumSuggestionsPerItemRange.Default;
    internal int BatchTimeoutSeconds { get; init; } = BatchTimeoutSecondsRange.Default;
    internal int StoreLimitMiB { get; init; } = StoreLimitMiBRange.Default;

    internal int ReviewReservePercent { get; init; } = ReviewReservePercentRange.Default;
    internal int BatchReadMiB { get; init; } = BatchReadMiBRange.Default;
    internal int BatchReadCount { get; init; } = BatchReadCountRange.Default;
    internal int ConcurrentReads { get; init; } = ConcurrentReadsRange.Default;
    internal int BatchHttpRequests { get; init; } = BatchHttpRequestsRange.Default;

    internal long BatchReadBytes => (long)BatchReadMiB * 1024 * 1024;
    internal double ConfidenceThreshold => ConfidenceThresholdPercent / 100.0;
    internal long MaximumInputBytes => (long)MaximumInputMiB * 1024 * 1024;
    internal long StoreLimitBytes => (long)StoreLimitMiB * 1024 * 1024;
    internal TimeSpan BatchTimeout => TimeSpan.FromSeconds(BatchTimeoutSeconds);

    /// <summary>True when every value is inside its range.</summary>
    internal bool IsValid => ConfidenceThresholdRange.Contains(ConfidenceThresholdPercent) && BatchSizeRange.Contains(BatchSize)
        && MaximumInputMiBRange.Contains(MaximumInputMiB) && MaximumSuggestionsPerItemRange.Contains(MaximumSuggestionsPerItem)
        && BatchTimeoutSecondsRange.Contains(BatchTimeoutSeconds) && StoreLimitMiBRange.Contains(StoreLimitMiB)
        && ReviewReservePercentRange.Contains(ReviewReservePercent) && BatchReadMiBRange.Contains(BatchReadMiB) && BatchReadCountRange.Contains(BatchReadCount)
        && ConcurrentReadsRange.Contains(ConcurrentReads) && BatchHttpRequestsRange.Contains(BatchHttpRequests);

    /// <summary>Every value moved into its range (for imported or hand-edited settings).</summary>
    internal TagSuggestionSettings Clamped() => new()
    {
        ConfidenceThresholdPercent = ConfidenceThresholdRange.Clamp(ConfidenceThresholdPercent),
        BatchSize = BatchSizeRange.Clamp(BatchSize),
        MaximumInputMiB = MaximumInputMiBRange.Clamp(MaximumInputMiB),
        MaximumSuggestionsPerItem = MaximumSuggestionsPerItemRange.Clamp(MaximumSuggestionsPerItem),
        BatchTimeoutSeconds = BatchTimeoutSecondsRange.Clamp(BatchTimeoutSeconds),
        StoreLimitMiB = StoreLimitMiBRange.Clamp(StoreLimitMiB),
        ReviewReservePercent = ReviewReservePercentRange.Clamp(ReviewReservePercent),
        BatchReadMiB = BatchReadMiBRange.Clamp(BatchReadMiB),
        BatchReadCount = BatchReadCountRange.Clamp(BatchReadCount),
        ConcurrentReads = ConcurrentReadsRange.Clamp(ConcurrentReads),
        BatchHttpRequests = BatchHttpRequestsRange.Clamp(BatchHttpRequests)
    };
}
