namespace MeowChan.Desktop.Localization;

/// <summary>
/// The statistics texts (network and disk graph, statistics page, tray, settings). Each work package keeps its own file
/// (<see cref="IoStatsPageTexts"/>, <see cref="IoStatsTrayTexts"/>, <see cref="IoStatsSettingsTexts"/>) so that
/// <see cref="BuiltInCatalog"/> is touched once, here.
/// </summary>
internal static class IoStatsCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
        [.. IoStatsPageTexts.Entries, .. IoStatsTrayTexts.Entries, .. IoStatsSettingsTexts.Entries];
}
