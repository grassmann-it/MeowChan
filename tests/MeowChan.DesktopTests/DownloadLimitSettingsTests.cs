using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Downloader;

namespace MeowChan.DesktopTests;

/// <summary>
/// The three download limits that had no maximum (bandwidth, requested parallel downloads, free-space reserve) now have
/// one, like every other limit: the field, the validation and the range sentence agree, the sentence is readable, a
/// settings file an older version saved with a larger value stays loadable, and the first-run parser uses the same
/// bounds.
/// </summary>
[Collection("Localization")]
public sealed class DownloadLimitSettingsTests
{
    private static readonly string[] Languages = { "en", "de", "es", "fr", "ru" };

    [Fact]
    public void TheMaximaAreTheEffectiveOrSensibleBoundsAndValidationUsesThem()
    {
        Assert.Equal(DownloadQueue.MaximumParallelDownloadsSafetyLimit, Config.MaxParallelDownloadsMaximum);
        Assert.Equal(256, Config.MaxParallelDownloadsMaximum);
        Assert.Equal(100_000, Config.BandwidthLimiterMaximumMiB);
        Assert.Equal(100_000L * 1024 * 1024, Config.BandwidthLimiterMaximumBytes);
        Assert.Equal((0, 65_536), (Config.MinimumFreeSpaceGiBMinimum, Config.MinimumFreeSpaceGiBMaximum));

        Config defaults = new();
        defaults.Validate();
        foreach ((Action<Config> valid, Action<Config> invalid) in new (Action<Config>, Action<Config>)[]
        {
            (c => c.MaxParallelDownloads = Config.MaxParallelDownloadsMaximum, c => c.MaxParallelDownloads = Config.MaxParallelDownloadsMaximum + 1),
            (c => c.MaxParallelDownloads = 1, c => c.MaxParallelDownloads = 0),
            (c => c.BandwidthLimiter = Config.BandwidthLimiterMaximumBytes, c => c.BandwidthLimiter = Config.BandwidthLimiterMaximumBytes + 1),
            (c => c.BandwidthLimiter = 1, c => c.BandwidthLimiter = 0),
            (c => c.MinimumFreeSpaceGiB = Config.MinimumFreeSpaceGiBMaximum, c => c.MinimumFreeSpaceGiB = Config.MinimumFreeSpaceGiBMaximum + 1),
            (c => c.MinimumFreeSpaceGiB = 0, c => c.MinimumFreeSpaceGiB = -1),
        })
        {
            Config accepted = defaults.Clone();
            valid(accepted);
            accepted.Validate();
            new Config().CopySettingsFieldsFrom(accepted);
            Config rejected = defaults.Clone();
            invalid(rejected);
            Assert.Throws<ArgumentOutOfRangeException>(() => rejected.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopySettingsFieldsFrom(rejected));
        }
    }

    [Fact]
    public void ASettingsFileSavedWithFormerlyUnboundedValuesStaysLoadableAndIsReducedToTheMaximum()
    {
        using TempDirectory directory = new();
        new Config { AutomaticUpdateChecks = false, DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        string path = Path.Combine(directory.Path, "settings.json");
        string json = File.ReadAllText(path);
        json = Regex.Replace(json, "\"MaxParallelDownloads\":\\s*\\d+", "\"MaxParallelDownloads\": 1000");
        json = Regex.Replace(json, "\"BandwidthLimiter\":\\s*\\d+", "\"BandwidthLimiter\": 9223372036854775807");
        json = Regex.Replace(json, "\"MinimumFreeSpaceGiB\":\\s*\\d+", "\"MinimumFreeSpaceGiB\": 2147483647");
        Assert.Contains("1000", json);
        File.WriteAllText(path, json);

        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(Config.MaxParallelDownloadsMaximum, loaded.MaxParallelDownloads);
        Assert.Equal(Config.BandwidthLimiterMaximumBytes, loaded.BandwidthLimiter);
        Assert.Equal(Config.MinimumFreeSpaceGiBMaximum, loaded.MinimumFreeSpaceGiB);
        loaded.Validate();
    }

    [Theory]
    [InlineData("1", "256", true)]
    [InlineData("100000", "1", true)]
    [InlineData("1", "257", false)]
    [InlineData("1", "1000", false)]
    [InlineData("100000.5", "1", false)]
    [InlineData("99999999", "1", false)]
    public void TheFirstRunParserUsesTheSameBounds(string bandwidth, string parallel, bool accepted)
    {
        Assert.Equal(accepted, FirstRunSetupCoordinator.TryParseLimits(bandwidth, parallel, CultureInfo.InvariantCulture, out long bytes, out int count));
        if (!accepted) return;
        Assert.InRange(bytes, 1, Config.BandwidthLimiterMaximumBytes);
        Assert.InRange(count, Config.MaxParallelDownloadsMinimum, Config.MaxParallelDownloadsMaximum);
    }

    [Fact]
    public void TheRangeSentencesAreReadableAndNameTheBoundsInEveryLanguage()
    {
        Texts.Initialize(); // With the bundled packs, whatever an earlier test of the collection left loaded.
        string previous = Texts.Language;
        try
        {
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                string bandwidthRange = Texts.T("settings.bandwidthRange", Texts.T("settings.bandwidth"), Config.BandwidthLimiterMaximumMiB);
                Assert.Contains("100000", bandwidthRange);
                Assert.DoesNotMatch("[0-9]{7,}", bandwidthRange);
                Assert.Contains("100000", Texts.T("settings.bandwidthHint"));
                Assert.Contains("256", Texts.T("settings.parallelHint"));
                Assert.Contains("65536", Texts.T("budgets.reserveHint"));
                Assert.Contains("256", Texts.T("setup.positiveLimits"));
                Assert.Contains("100000", Texts.T("setup.positiveLimits"));
                foreach (string key in new[] { "settings.bandwidthHint", "settings.parallelHint" })
                    Assert.NotEqual(key, Texts.T(key));
            }
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void TheMarkupBoundsMatchTheConfigurationAndTheHintsAreShown()
    {
        XDocument markup = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Field(string name) => Assert.Single(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == name);
        Assert.Equal(Config.BandwidthLimiterMaximumMiB.ToString(), (string?)Field("BandwidthInput").Attribute("Maximum"));
        Assert.Equal(Config.MaxParallelDownloadsMaximum.ToString(), (string?)Field("ParallelDownloadsInput").Attribute("Maximum"));
        Assert.Equal(Config.MaxParallelDownloadsMinimum.ToString(), (string?)Field("ParallelDownloadsInput").Attribute("Minimum"));
        Assert.Equal(Config.MinimumFreeSpaceGiBMaximum.ToString(), (string?)Field("ReserveInput").Attribute("Maximum"));
        Assert.Equal(Config.MinimumFreeSpaceGiBMinimum.ToString(), (string?)Field("ReserveInput").Attribute("Minimum"));
        Assert.Contains(markup.Descendants(), e => (string?)e.Attribute("Text") == "{Binding BandwidthHint}");
        Assert.Contains(markup.Descendants(), e => (string?)e.Attribute("Text") == "{Binding ParallelHint}");
        Assert.DoesNotContain(markup.Descendants(), e => (string?)e.Attribute("Maximum") == int.MaxValue.ToString());
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
