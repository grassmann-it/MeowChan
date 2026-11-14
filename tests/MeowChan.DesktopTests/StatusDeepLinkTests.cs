using System.Runtime.CompilerServices;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// Deep links into the Status page (SM5): the Sources page passes the canonical id of its source to Status › Diagnostics (the
/// diagnostics segment presets its source filter), the tray lines open Status › Statistics, and the texts that name the
/// destination use the new path in all twelve languages.
/// </summary>
[Collection("Localization")]
public sealed class StatusDeepLinkTests : IDisposable
{
    public static readonly TheoryData<string> Languages =
        ["en", "de", "es", "fr", "ru", "it", "pl", "cs", "pt-BR", "ja", "ko", "zh-Hans"];

    public StatusDeepLinkTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void TheSourcesPageHandsItsSourceIdToTheDiagnosticsSegment()
    {
        string page = File.ReadAllText(Source("Desktop/Views/Sources/SourcesPage.axaml.cs"));
        Assert.Contains("private readonly Action<string>? _openDiagnostics;", page);
        Assert.Contains("Action<string> openDiagnostics, Action addToDownloads", page);
        Assert.Contains("Selected is SourceListRow row) _openDiagnostics?.Invoke(row.Id);", page);
        Assert.Contains("_openDiagnostics?.Invoke(source.Id)", File.ReadAllText(Source("Desktop/Views/Sources/SourcesPage.ContextMenus.cs")));
        Assert.Contains("sourceId => ShowStatus(StatusSegment.Diagnostics, sourceId)", File.ReadAllText(Source("Desktop/Shell/MainWindow.Navigation.cs")));
    }

    [Fact]
    public void TheStatusPresetsTheFilterOnlyInTheDiagnosticsSegmentAndNeverRemembersTheRecoveryFallback()
    {
        string status = File.ReadAllText(Source("Desktop/Shell/MainWindow.Status.cs"));
        Assert.Contains("if (sourceId != null && target == StatusSegment.Diagnostics) _diagnosticsPage?.FilterSource(sourceId);", status);
        Assert.Contains("requested == StatusSegment.Statistics && !StatisticsSegmentAvailable ? StatusSegment.Diagnostics : requested;", status);
        Assert.Contains("if (StatisticsSegmentAvailable) RememberStatusSegment(target);", status);
        // The recovery route: the gate admits Status without a completed restore, only after a failed recovery.
        string navigation = File.ReadAllText(Source("Desktop/Shell/MainWindow.Navigation.cs"));
        Assert.Contains("page == WorkspacePage.Status && _session.Projection.View.State == DesktopSessionState.RecoveryFailed", navigation);
    }

    [Fact]
    public void TheTrayLinesOpenTheStatisticsSegment()
    {
        string tray = File.ReadAllText(Source("Desktop/Shell/MainWindow.StatisticsTray.cs"));
        Assert.Contains("ShowStatus(StatusSegment.Statistics);", tray);
        Assert.Contains("OpenStatistics: () => { RestoreWindow(); ShowStatisticsFromTray(); }", File.ReadAllText(Source("Desktop/Shell/MainWindow.TrayStats.cs")));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheFeedbackHintNamesStatusAndDiagnosticsInEveryLanguage(string language)
    {
        Texts.SetLanguage(language);
        string hint = Texts.T("feedback.hint");
        string status = Texts.T("nav.status"), diagnostics = Texts.T("nav.diagnostics");
        Assert.Contains(status + " › " + diagnostics, hint, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheTrayMenuHintNamesTheStatisticsSegmentOfStatusInEveryLanguage(string language)
    {
        Texts.SetLanguage(language);
        string hint = Texts.T("stats.settings.trayMenuHint");
        Assert.Contains(Texts.T("nav.status") + " › " + Texts.T("nav.statistics"), hint, StringComparison.Ordinal);
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
