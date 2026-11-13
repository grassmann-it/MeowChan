using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Admission;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// The two clipboard-monitoring bounds (URLs checked per read, text size searched) are settings on "Storage and limits"
/// with the former fixed values as defaults, and a poll that had to leave something out says so with a sentence that
/// names the setting, instead of dropping it silently.
/// </summary>
[Collection("Localization")]
public sealed class ClipboardLimitSettingsTests
{
    private const string Url = "https://source.invalid/b/thread/";
    private static readonly string[] Hosts = ["source.invalid"];
    private static Task<BoardAdmissionTarget?> Classify(string url) => Task.FromResult<BoardAdmissionTarget?>(new(url, false, false));

    [Fact]
    public void TheDefaultsAreTheFormerFixedValuesAndTheMonitorRejectsValuesOutsideTheRange()
    {
        Config config = new();
        Assert.Equal((32, 64), (config.ClipboardUrlLimit, config.ClipboardTextLimitKiB));
        Assert.Equal((ClipboardUrlMonitor.MaximumUrls, ClipboardUrlMonitor.MaximumTextLength), (Config.ClipboardUrlLimitDefault, Config.ClipboardTextLimitKiBDefault * 1024));
        ClipboardUrlMonitor monitor = new();
        Assert.Equal((32, 64 * 1024), (monitor.UrlLimit, monitor.TextLimit));
        Assert.Throws<ArgumentOutOfRangeException>(() => monitor.Configure(Config.ClipboardUrlLimitMinimum - 1, 64 * 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => monitor.Configure(Config.ClipboardUrlLimitMaximum + 1, 64 * 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => monitor.Configure(32, (Config.ClipboardTextLimitKiBMinimum - 1) * 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => monitor.Configure(32, (Config.ClipboardTextLimitKiBMaximum + 1) * 1024));
        monitor.Configure(Config.ClipboardUrlLimitMaximum, Config.ClipboardTextLimitKiBMaximum * 1024);
        Assert.Equal((500, 4096 * 1024), (monitor.UrlLimit, monitor.TextLimit));
    }

    [Fact]
    public async Task MoreUrlsThanTheLimitAreCutWithANoticeThatGoesAwayWhenDismissedOrWhenTheClipboardChanges()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        monitor.Configure(2, 4 * 1024);
        string five = string.Join('\n', Enumerable.Range(1, 5).Select(index => Url + index));
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(five), Hosts, Classify);
        Assert.Equal(new[] { Url + "1", Url + "2" }, monitor.Pending);
        Assert.Equal(ClipboardNotice.UrlsCut, monitor.Notice);
        Assert.NotNull(monitor.NoticeText());

        monitor.Dismiss();
        Assert.Equal(ClipboardNotice.None, monitor.Notice);
        Assert.Null(monitor.NoticeText());
        Assert.Empty(monitor.Pending);

        // Exactly the limit is not a cut.
        string two = Url + "7\n" + Url + "8";
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(two), Hosts, Classify);
        Assert.Equal(2, monitor.Pending.Count);
        Assert.Equal(ClipboardNotice.None, monitor.Notice);

        // Skipped URLs are reported even when none of the checked ones was supported.
        string foreign = "https://other.invalid/1 https://other.invalid/2 " + Url + "9";
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(foreign), Hosts, Classify);
        Assert.Empty(monitor.Pending);
        Assert.Equal(ClipboardNotice.UrlsCut, monitor.Notice);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>("plain text"), Hosts, _ => throw new Exception("Nothing to classify"));
        Assert.Equal(ClipboardNotice.None, monitor.Notice);
    }

    [Fact]
    public async Task AnOversizedTextIsNotSearchedAndSaysSoOncePerText()
    {
        ClipboardUrlMonitor monitor = new();
        monitor.SetEnabled(true);
        monitor.Configure(32, 4 * 1024);
        string oversized = Url + "1 " + new string('x', 4 * 1024);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(oversized), Hosts, _ => throw new Exception("Must not classify"));
        Assert.Empty(monitor.Pending);
        Assert.Equal(ClipboardNotice.TextTooLong, monitor.Notice);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(oversized), Hosts, _ => throw new Exception("Must not classify"));
        Assert.Equal(ClipboardNotice.TextTooLong, monitor.Notice);
        monitor.Dismiss();
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(oversized), Hosts, _ => throw new Exception("Must not classify"));
        Assert.Equal(ClipboardNotice.None, monitor.Notice);
        // A smaller text ends the episode; the same oversized text afterwards is a new one.
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(Url + "1"), Hosts, Classify);
        Assert.Single(monitor.Pending);
        Assert.Equal(ClipboardNotice.None, monitor.Notice);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(oversized), Hosts, _ => throw new Exception("Must not classify"));
        Assert.Empty(monitor.Pending);
        Assert.Equal(ClipboardNotice.TextTooLong, monitor.Notice);
        // Exactly the limit is searched.
        monitor.Dismiss();
        string atLimit = Url + "2" + new string(' ', 4 * 1024 - (Url + "2").Length);
        Assert.Equal(4 * 1024, atLimit.Length);
        await monitor.PollAsync(() => true, () => Task.FromResult<string?>(atLimit), Hosts, Classify);
        Assert.Equal(Url + "2", Assert.Single(monitor.Pending));
        Assert.Equal(ClipboardNotice.None, monitor.Notice);
    }

    [Fact]
    public void ExtractionReportsTruncationWithTheGivenBounds()
    {
        string batch = string.Join('\n', Enumerable.Range(0, 10).Select(index => Url + index));
        Assert.Equal(3, ClipboardUrlMonitor.ExtractCandidates(batch, Hosts, 3, 64 * 1024, out bool truncated).Count);
        Assert.True(truncated);
        Assert.Equal(10, ClipboardUrlMonitor.ExtractCandidates(batch, Hosts, 10, 64 * 1024, out truncated).Count);
        Assert.False(truncated);
        Assert.Empty(ClipboardUrlMonitor.ExtractCandidates(batch, Hosts, 10, batch.Length - 1, out truncated));
        Assert.False(truncated);
        // The legacy overload keeps the defaults.
        Assert.Equal(ClipboardUrlMonitor.MaximumUrls, ClipboardUrlMonitor.ExtractCandidates(string.Join('\n', Enumerable.Range(0, 100).Select(index => Url + index)), Hosts).Count);
    }

    [Fact]
    public void TheNoticesNameTheSettingAndThePageInEveryLanguage()
    {
        Texts.Initialize(); // With the bundled packs, whatever an earlier test of the collection left loaded.
        string previous = Texts.Language;
        try
        {
            ClipboardUrlMonitor cut = new();
            cut.SetEnabled(true);
            cut.Configure(7, 12 * 1024);
            foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
            {
                Texts.SetLanguage(language);
                string urls = Texts.T("clipboard.monitor.cutUrls", 7, Texts.T("budgets.clipboardUrls"));
                string text = Texts.T("clipboard.monitor.cutText", 12, Texts.T("budgets.clipboardText"));
                foreach (string sentence in new[] { urls, text })
                {
                    Assert.Contains(Texts.T("settings.storageLimits"), sentence);
                    Assert.Contains(Texts.T("settings.title"), sentence);
                    Assert.DoesNotContain("clipboard.monitor", sentence);
                    Assert.DoesNotContain("{", sentence);
                }
                Assert.Contains(Texts.T("budgets.clipboardUrls"), urls);
                Assert.Contains("7", urls);
                Assert.Contains(Texts.T("budgets.clipboardText"), text);
                Assert.Contains("12", text);
                Assert.Contains(Texts.T("settings.storageLimits"), Texts.T("settings.monitorClipboardHint"));
                Assert.Contains("500", Texts.T("budgets.clipboardUrlsHint"));
                Assert.Contains("4096", Texts.T("budgets.clipboardTextHint"));
                Assert.NotEqual("settings.unitKiB", Texts.T("settings.unitKiB"));
            }
            Texts.SetLanguage("de");
            foreach (string key in new[] { "clipboard.monitor.cutUrls", "clipboard.monitor.cutText", "budgets.clipboardUrlsHint", "budgets.clipboardTextHint" })
                Assert.DoesNotContain("Sie ", Texts.T(key));
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void TheSettingsRowsSitOnTheStorageAndLimitsPageWithTheConfiguredBounds()
    {
        XDocument markup = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach ((string name, int minimum, int maximum) in new[]
        {
            ("ClipboardUrlLimit", Config.ClipboardUrlLimitMinimum, Config.ClipboardUrlLimitMaximum),
            ("ClipboardTextLimitKiB", Config.ClipboardTextLimitKiBMinimum, Config.ClipboardTextLimitKiBMaximum)
        })
        {
            XElement field = Assert.Single(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == name + "Input");
            Assert.Contains(field.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsWorkerBudgetsPage");
            Assert.Equal("{Binding " + name + ", Mode=TwoWay}", (string?)field.Attribute("Value"));
            Assert.Equal(minimum.ToString(), (string?)field.Attribute("Minimum"));
            Assert.Equal(maximum.ToString(), (string?)field.Attribute("Maximum"));
            Assert.Contains(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == name + "InputError");
            Assert.Contains(markup.Descendants(), e => (string?)e.Attribute("Text") == "{Binding " + name + "Hint}");
        }
        XDocument shell = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Shell/MainWindow.axaml"));
        Assert.Single(shell.Descendants(), e => (string?)e.Attribute(x + "Name") == "ClipboardSuggestionNotice");
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
