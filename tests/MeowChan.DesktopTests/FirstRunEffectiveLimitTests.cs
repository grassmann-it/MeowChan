using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Pure presentation/parser and source-wiring coverage. No native Window, host,
// filesystem probe, queue or commit is constructed. Root must integrate the keys
// before running these tests; missing catalog/pack integration deliberately fails.
[Collection("Localization")]
public sealed class FirstRunEffectiveLimitTests : IDisposable
{
    public FirstRunEffectiveLimitTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    [Theory]
    [InlineData("5", 32, 5, 5)]
    [InlineData("32", 32, 32, 32)]
    [InlineData("48", 32, 48, 32)]
    [InlineData("64", 32, 64, 32)]
    [InlineData("48", 12, 48, 12)]
    [InlineData("48", 64, 48, 48)]
    [InlineData("48", 1, 48, 1)]
    [InlineData("200", 256, 200, 200)]
    [InlineData("256", 256, 256, 256)]
    public void PreviewShowsRequestedSavedCapAndTheirMinimum(string raw, int cap, int requested, int effective)
    {
        Assert.Equal(Expected("setup.parallelEffective", "en", effective, requested, cap),
            FirstRunSetupWindow.FormatParallelLimitPreview(raw, cap, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void DefaultConfigPreviewUsesExistingDefaultWithoutChangingIt()
    {
        Config baseline = new();
        Assert.Equal(32, baseline.ParallelDownloadsSafetyLimit);
        Assert.Equal(Expected("setup.parallelEffective", "en", 32, 64, baseline.ParallelDownloadsSafetyLimit),
            FirstRunSetupWindow.FormatParallelLimitPreview("64", baseline.ParallelDownloadsSafetyLimit,
                CultureInfo.InvariantCulture));
        Assert.Equal(5, baseline.MaxParallelDownloads);
        Assert.Equal(32, baseline.ParallelDownloadsSafetyLimit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.5")]
    [InlineData("32.5")]
    [InlineData("257")]
    [InlineData("1000")]
    [InlineData("2147483647")]
    [InlineData("2147483648")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("1e2")]
    [InlineData("not a number")]
    public void InvalidDraftDoesNotShowAStaleOrClampedEffectiveCount(string? raw)
    {
        _ = FirstRunSetupWindow.FormatParallelLimitPreview("64", 12, CultureInfo.InvariantCulture);
        string invalid = FirstRunSetupWindow.FormatParallelLimitPreview(raw, 12, CultureInfo.InvariantCulture);
        Assert.Equal(Expected("setup.parallelEffectiveInvalid", "en", 12), invalid);
        Assert.Equal(Expected("setup.parallelEffective", "en", 8, 8, 12),
            FirstRunSetupWindow.FormatParallelLimitPreview("8", 12, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("en-US", "48.0", 48)]
    [InlineData("de-DE", "48,0", 48)]
    [InlineData("fr-FR", "48,0", 48)]
    [InlineData("en-US", " +64 ", 64)]
    [InlineData("de-DE", "256", 256)]
    public void PreviewUsesAcceptParserAndInputCultureRatherThanDisplayLanguage(string culture, string raw, int requested)
    {
        CultureInfo inputCulture = CultureInfo.GetCultureInfo(culture);
        Texts.SetLanguage("de");
        Assert.True(FirstRunSetupCoordinator.TryParseLimits("1", raw, inputCulture, out _, out int parsed));
        Assert.Equal(requested, parsed);
        Assert.Equal(Expected("setup.parallelEffective", "de", Math.Min(requested, 32), requested, 32),
            FirstRunSetupWindow.FormatParallelLimitPreview(raw, 32, inputCulture));
    }

    [Fact]
    public void FormattingAndLanguageRefreshLeaveRawDraftAndRevisionAlone()
    {
        FirstRunSetupRawDraft raw = new("   ", "invalid bandwidth", " 048,0 ");
        FirstRunSetupDraftState draft = new(raw);
        foreach (string code in new[] { "en", "de", "en" })
        {
            Texts.SetLanguage(code);
            Assert.Equal(Expected("setup.parallelEffective", code, 32, 48, 32),
                FirstRunSetupWindow.FormatParallelLimitPreview(draft.Current.Parallel, 32,
                    CultureInfo.GetCultureInfo("de-DE")));
            Assert.Equal(raw, draft.Current);
            Assert.Equal(0, draft.Revision);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void EveryNewKeyIsIntegratedAndPreviewUsesActiveLanguage(string code)
    {
        Texts.SetLanguage(code);
        Assert.Equal(code, Texts.Language);
        Dictionary<string, string> pack = new();
        if (code is "es" or "fr" or "ru")
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(
                "MeowChan.Desktop.Localization.Packs." + code + ".json")!;
            Assert.NotNull(stream);
            using JsonDocument document = JsonDocument.Parse(stream);
            pack = document.RootElement.GetProperty("strings").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString()!);
        }
        foreach (var entry in FirstRunLimitCatalog.Entries)
        {
            string format = code switch
            {
                "en" => entry.English,
                "de" => entry.German,
                _ => pack[entry.Key] // Do not accept an English fallback as a translated pack.
            };
            Assert.False(string.IsNullOrWhiteSpace(format));
            Assert.Equal(CompositeFormat.Parse(entry.English).MinimumArgumentCount,
                CompositeFormat.Parse(format).MinimumArgumentCount);
            Assert.Equal(format, Texts.T(entry.Key));
            Assert.NotEqual(entry.Key, Texts.T(entry.Key));
        }
        string expectedFormat = code switch
        {
            "en" => FirstRunLimitCatalog.Entries.Single(e => e.Key == "setup.parallelEffective").English,
            "de" => FirstRunLimitCatalog.Entries.Single(e => e.Key == "setup.parallelEffective").German,
            _ => pack["setup.parallelEffective"]
        };
        Assert.Equal(string.Format(Texts.Culture, expectedFormat, 32, 64, 32),
            FirstRunSetupWindow.FormatParallelLimitPreview("64", 32, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void LiveSummaryAndExplanationStayNextToRequestedInputInsideScrollableFields()
    {
        XElement root = XDocument.Load(Source("Desktop/Views/Settings/FirstRunSetupWindow.axaml")).Root!;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement fields = root.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "SetupFields");
        Assert.Contains(fields.Ancestors(), e => e.Name.LocalName == "ScrollViewer");
        XElement[] controls = fields.Elements().ToArray();
        int input = Array.FindIndex(controls, e => (string?)e.Attribute(x + "Name") == "SetupParallelInput");
        Assert.True(input >= 0);
        Assert.Equal("OnDraftChanged", (string?)controls[input].Attribute("TextChanged"));
        Assert.Equal("SetupEffectiveParallelLimit", (string?)controls[input + 1].Attribute(x + "Name"));
        Assert.Equal("Polite", (string?)controls[input + 1].Attribute("AutomationProperties.LiveSetting"));
        Assert.Equal("SetupParallelSafetyHint", (string?)controls[input + 2].Attribute(x + "Name"));
        Assert.Equal("SetupLimitsError", (string?)controls[input + 3].Attribute(x + "Name"));
    }

    [Fact]
    public void ExistingDraftAndLanguageRefreshPathsRefreshPresentationWithoutChangingAcceptance()
    {
        string source = File.ReadAllText(Source("Desktop/Views/Settings/FirstRunSetupWindow.axaml.cs"));
        string synchronize = Between(source, "private FirstRunSetupRawDraft SynchronizeDraft()", "private void OnDraftChanged(");
        Assert.Contains("RefreshPreview();", synchronize);
        string preview = Between(source, "private void RefreshPreview()", "private void RefreshLabels()");
        Assert.Contains("RefreshParallelLimitPreview();", preview);
        Assert.Contains("_baseline.ParallelDownloadsSafetyLimit, CultureInfo.CurrentCulture", preview);
        Assert.Contains("TryParseLimits(\"1\", parallel, inputCulture", preview);
        Assert.Contains("Math.Min(requested, safetyLimit)", preview);
        Assert.Contains("AutomationProperties.SetHelpText(SetupParallelInput", preview);
        foreach (string forbidden in new[] { "SetupParallelInput.Text =", "SetupBandwidthInput.Text =",
            "SetupFolderInput.Text =", "_accept(", "CheckAsync(", "SynchronizeDraft(", "SaveConfig(" })
            Assert.DoesNotContain(forbidden, preview);
        string labels = Between(source, "private void RefreshLabels()", "private void OnLanguageChanged(");
        Assert.Contains("SetupParallelLabel.Text = Texts.T(\"setup.parallelRequested\");", labels);
        Assert.Contains("SetupParallelSafetyHint.Text = Texts.T(\"setup.parallelSafetyHint\");", labels);
        string language = Between(source, "private void OnLanguageChanged(", "private void OnCleanupFailure(");
        Assert.Contains("if (_closed) return;", language);
        Assert.Contains("RefreshPreview();", language);
        string read = Between(source, "private bool TryReadDraft(", "private void Reveal(");
        Assert.Contains("TryParseLimits(raw.Bandwidth, raw.Parallel", read);
        Assert.Contains("values = new(syntax.NormalizedPath, rate, parallel);", read);
        Assert.DoesNotContain("Math.Min", read);
        Assert.DoesNotContain("ParallelDownloadsSafetyLimit", read);
    }

    private static string Expected(string key, string code, params object[] arguments)
    {
        var entry = FirstRunLimitCatalog.Entries.Single(e => e.Key == key);
        return string.Format(CultureInfo.GetCultureInfo(code), code == "de" ? entry.German : entry.English, arguments);
    }

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, "Missing source anchor: " + start);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, "Missing source anchor: " + end);
        return source[first..last];
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
