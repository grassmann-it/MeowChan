using System;

namespace MeowChan.Desktop.Stats;

/// <summary>
/// The nine statistics settings as one immutable value. Values outside the documented range are clamped so that a
/// hand-built value can never produce a zero-length timer or an unbounded buffer.
/// </summary>
internal sealed record IoStatsOptions
{
    internal static IoStatsOptions Default { get; } = new();

    internal int SampleIntervalMs { get; init; } = Config.StatsSampleIntervalMsDefault;
    internal int HistorySamples { get; init; } = Config.StatsHistorySamplesDefault;
    internal int BackgroundIntervalMs { get; init; } = Config.StatsBackgroundIntervalMsDefault;
    internal bool TrayTooltip { get; init; } = true;
    internal bool TrayMenu { get; init; } = true;
    internal bool Persist { get; init; } = true;
    internal int RetentionDays { get; init; } = Config.StatsRetentionDaysDefault;
    internal int PersistIntervalMinutes { get; init; } = Config.StatsPersistIntervalMinutesDefault;
    internal int MaxSources { get; init; } = Config.StatsMaxSourcesDefault;

    internal static IoStatsOptions FromConfig(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new IoStatsOptions
        {
            SampleIntervalMs = config.StatsSampleIntervalMs, HistorySamples = config.StatsHistorySamples,
            BackgroundIntervalMs = config.StatsBackgroundIntervalMs, TrayTooltip = config.StatsTrayTooltip,
            TrayMenu = config.StatsTrayMenu, Persist = config.StatsPersist, RetentionDays = config.StatsRetentionDays,
            PersistIntervalMinutes = config.StatsPersistIntervalMinutes, MaxSources = config.StatsMaxSources
        }.Normalized();
    }

    internal IoStatsOptions Normalized() => this with
    {
        SampleIntervalMs = Math.Clamp(SampleIntervalMs, Config.StatsSampleIntervalMsMinimum, Config.StatsSampleIntervalMsMaximum),
        HistorySamples = Math.Clamp(HistorySamples, Config.StatsHistorySamplesMinimum, Config.StatsHistorySamplesMaximum),
        BackgroundIntervalMs = Math.Clamp(BackgroundIntervalMs, Config.StatsBackgroundIntervalMsMinimum, Config.StatsBackgroundIntervalMsMaximum),
        RetentionDays = Math.Clamp(RetentionDays, Config.StatsRetentionDaysMinimum, Config.StatsRetentionDaysMaximum),
        PersistIntervalMinutes = Math.Clamp(PersistIntervalMinutes, Config.StatsPersistIntervalMinutesMinimum, Config.StatsPersistIntervalMinutesMaximum),
        MaxSources = Math.Clamp(MaxSources, Config.StatsMaxSourcesMinimum, Config.StatsMaxSourcesMaximum)
    };
}
