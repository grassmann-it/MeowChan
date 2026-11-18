using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Real controls + production handler/refresh methods, not source-string assertions.
// Like the existing gallery control fixtures, bypass the native Window constructor.
// This does not exercise InitializeComponent, routed TextChanged delivery, the
// dispatcher language callback, native layout, filesystem probing or acceptance.
[Collection("Localization")]
public sealed class FirstRunEffectiveLimitInteractionRegressionTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly string _language = Texts.Language;

    public FirstRunEffectiveLimitInteractionRegressionTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
        Texts.Initialize();
        Texts.SetLanguage(_language);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a count")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("3.5")]
    [InlineData("2147483648")]
    public void InvalidToValidEntryReplacesStaleSummaryAndHelpWithoutRewritingInputs(string invalid)
    {
        var fixture = new ControlsFixture(new Config { MaxParallelDownloads = 48, ParallelDownloadsSafetyLimit = 12 });
        fixture.RefreshLimit();
        fixture.AssertSummary(12, 48, 12);
        long initialRevision = fixture.Window.DraftRevision;

        fixture.EnterParallel(invalid);
        Assert.Equal(initialRevision + 1, fixture.Window.DraftRevision);
        Assert.Equal(invalid, fixture.Parallel.Text);
        Assert.Equal(Expected("setup.parallelEffectiveInvalid", 12), fixture.Summary.Text);
        fixture.AssertHelpMatchesSummary();
        Assert.Equal(new FirstRunSetupRawDraft(fixture.Folder.Text, fixture.Bandwidth.Text, invalid), fixture.Draft.Current);

        fixture.EnterParallel(" 008.0 ");
        Assert.Equal(initialRevision + 2, fixture.Window.DraftRevision);
        fixture.AssertSummary(8, 8, 12);
        Assert.Equal(" 008.0 ", fixture.Parallel.Text);
        Assert.Equal(" 008.0 ", fixture.Draft.Current.Parallel);
        Assert.Equal("invalid bandwidth", fixture.Bandwidth.Text); // preview is independent of bandwidth validity
        Assert.Equal("   ", fixture.Folder.Text); // and independent of path validity
        Assert.Equal(48, fixture.Baseline.MaxParallelDownloads);
        Assert.Equal(12, fixture.Baseline.ParallelDownloadsSafetyLimit);

        // A duplicate notification must not invalidate a later result/revision.
        long revision = fixture.Window.DraftRevision;
        fixture.DeliverDraftChanged();
        Assert.Equal(revision, fixture.Window.DraftRevision);
        fixture.AssertSummary(8, 8, 12);
    }

    [Theory]
    [InlineData(1, 48, 1)]
    [InlineData(12, 48, 12)]
    [InlineData(64, 48, 48)]
    [InlineData(256, 256, 256)] // the largest parallel-download setting is 256 (Config.MaxParallelDownloadsMaximum)
    public void SavedCustomCapDrivesControlPreviewForRequestsAboveAndBelowCap(int cap, int requested, int effective)
    {
        using TempDirectory directory = new();
        new Config { MaxParallelDownloads = requested, ParallelDownloadsSafetyLimit = cap,
            DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        byte[] saved = File.ReadAllBytes(settingsPath);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        var fixture = new ControlsFixture(loaded);
        fixture.RefreshLimit();
        fixture.AssertSummary(effective, requested, cap);

        // The same control target transitions to the other side of the cap;
        // never clamp the requested input or silently restore the default 32.
        int otherRequest = cap == 1 ? 1 : cap - 1;
        fixture.EnterParallel(otherRequest.ToString(CultureInfo.InvariantCulture));
        fixture.AssertSummary(otherRequest, otherRequest, cap);
        // The setting itself is capped at Config.MaxParallelDownloadsMaximum (256); a request above that is refused, not previewed.
        int above = Math.Min(cap + 7, Config.MaxParallelDownloadsMaximum);
        fixture.EnterParallel(above.ToString(CultureInfo.InvariantCulture));
        fixture.AssertSummary(cap, above, cap);
        Assert.Equal(above.ToString(CultureInfo.InvariantCulture), fixture.Parallel.Text);
        Assert.Equal(cap, fixture.Baseline.ParallelDownloadsSafetyLimit);
        Assert.Equal(requested, fixture.Baseline.MaxParallelDownloads);
        Assert.Equal(cap, loaded.ParallelDownloadsSafetyLimit);
        Assert.Equal(requested, loaded.MaxParallelDownloads);
        Assert.Equal(saved, File.ReadAllBytes(settingsPath)); // presentation never writes the saved config
    }

    [Theory]
    [InlineData(" 048,0 ", true)]
    [InlineData("invalid", false)]
    public void LimitLabelRefreshPreservesRawControlsDraftRevisionPendingWaitAndValidation(string raw, bool valid)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        var fixture = new ControlsFixture(new Config { ParallelDownloadsSafetyLimit = 12 });
        fixture.EnterParallel(raw);
        fixture.Parallel.SelectionStart = 1;
        fixture.Parallel.SelectionEnd = Math.Min(4, raw.Length);
        int selectionStart = fixture.Parallel.SelectionStart;
        int selectionEnd = fixture.Parallel.SelectionEnd;
        FirstRunSetupRawDraft draft = fixture.Draft.Current;
        long revision = fixture.Window.DraftRevision;
        using var wait = new CancellationTokenSource();
        Set(fixture.Window, "_checkWait", wait);
        Set(fixture.Window, "_checkStatus", DownloadDestinationStatus.BelowReserve);
        Set(fixture.Window, "_busy", true);
        Set(fixture.Window, "_accepting", true);
        Set(fixture.Window, "_errorKey", "setup.saveUnconfirmed");
        fixture.LimitsError.Text = "existing validation";
        fixture.LimitsError.IsVisible = true;
        fixture.Status.Text = "existing check status";
        string? english = fixture.Summary.Text;

        foreach (string language in new[] { "de", "en", "en", "de" })
        {
            Texts.SetLanguage(language);
            // The actual limit-label refresh used by RefreshPreview; deliberately
            // no full RefreshLabels/Window.Title or dispatcher on a constructor-free Window.
            fixture.RefreshLimit();
            Assert.Equal(valid ? Expected("setup.parallelEffective", 12, 48, 12)
                : Expected("setup.parallelEffectiveInvalid", 12), fixture.Summary.Text);
            if (language == "de") Assert.NotEqual(english, fixture.Summary.Text);
            fixture.AssertHelpMatchesSummary();
            Assert.Equal("de-DE", CultureInfo.CurrentCulture.Name); // display language is not input parsing culture
            Assert.Equal(raw, fixture.Parallel.Text);
            Assert.Equal("invalid bandwidth", fixture.Bandwidth.Text);
            Assert.Equal("   ", fixture.Folder.Text);
            Assert.Equal(selectionStart, fixture.Parallel.SelectionStart);
            Assert.Equal(selectionEnd, fixture.Parallel.SelectionEnd);
            Assert.Same(fixture.Draft, Get<FirstRunSetupDraftState>(fixture.Window, "_draft"));
            Assert.Equal(draft, fixture.Draft.Current);
            Assert.Equal(revision, fixture.Window.DraftRevision);
            Assert.Same(wait, Get<CancellationTokenSource>(fixture.Window, "_checkWait"));
            Assert.False(wait.IsCancellationRequested);
            Assert.Equal(DownloadDestinationStatus.BelowReserve,
                Get<DownloadDestinationStatus?>(fixture.Window, "_checkStatus"));
            Assert.True(fixture.Window.IsWorking);
            Assert.True(Get<bool>(fixture.Window, "_accepting"));
            Assert.Equal("setup.saveUnconfirmed", Get<string>(fixture.Window, "_errorKey"));
            Assert.True(fixture.LimitsError.IsVisible);
            Assert.Equal("existing validation", fixture.LimitsError.Text);
            Assert.Equal("existing check status", fixture.Status.Text);
            Assert.Equal(12, fixture.Baseline.ParallelDownloadsSafetyLimit);
        }

        // Refresh must not disable genuine later edits: one edit invalidates
        // the old check and cancels its wait through the production handler.
        fixture.EnterParallel("7");
        fixture.AssertSummary(7, 7, 12);
        Assert.Equal(revision + 1, fixture.Window.DraftRevision);
        Assert.True(wait.IsCancellationRequested);
        Assert.Null(Get<DownloadDestinationStatus?>(fixture.Window, "_checkStatus"));
        Assert.False(fixture.LimitsError.IsVisible);
    }

    private static string Expected(string key, params object[] args)
    {
        var entry = FirstRunLimitCatalog.Entries.Single(entry => entry.Key == key);
        // A count of exactly one selects the singular text of the plural-aware path.
        var singular = args.Length > 0 && Convert.ToInt64(args[0], CultureInfo.InvariantCulture) == 1
            ? MeowChan.Desktop.Localization.CountCatalog.Entries.Cast<(string Key, string English, string German)?>().FirstOrDefault(e => e!.Value.Key == key + ".one")
            : null;
        if (singular is { } one) entry = one;
        return string.Format(Texts.Culture, Texts.Language == "de" ? entry.German : entry.English, args);
    }

    private sealed class ControlsFixture
    {
        internal FirstRunSetupWindow Window { get; } =
            (FirstRunSetupWindow)RuntimeHelpers.GetUninitializedObject(typeof(FirstRunSetupWindow));
        internal Config Baseline { get; }
        internal TextBox Folder { get; } = new() { Text = "   " };
        internal TextBox Bandwidth { get; } = new() { Text = "invalid bandwidth" };
        internal TextBox Parallel { get; } = new();
        internal TextBlock Summary { get; } = new();
        internal TextBlock LimitsError { get; } = new();
        internal TextBlock Status { get; } = new();
        internal FirstRunSetupDraftState Draft { get; }

        internal ControlsFixture(Config baseline)
        {
            Baseline = baseline.Clone();
            Parallel.Text = baseline.MaxParallelDownloads.ToString(CultureInfo.InvariantCulture);
            Draft = new(new(Folder.Text, Bandwidth.Text, Parallel.Text));
            Set(Window, "_baseline", Baseline);
            Set(Window, "_draft", Draft);
            Set(Window, "SetupFolderInput", Folder);
            Set(Window, "SetupBandwidthInput", Bandwidth);
            Set(Window, "SetupParallelInput", Parallel);
            Set(Window, "SetupEffectiveParallelLimit", Summary);
            Set(Window, "SetupNormalizedPath", new TextBlock());
            Set(Window, "SetupPathError", new TextBlock());
            Set(Window, "SetupLimitsError", LimitsError);
            Set(Window, "SetupError", new TextBlock());
            Set(Window, "SetupStatus", Status);
        }

        internal void RefreshLimit() => Invoke(Window, "RefreshParallelLimitPreview");
        internal void DeliverDraftChanged() => Invoke(Window, "OnDraftChanged", Parallel, null);
        internal void EnterParallel(string raw)
        {
            Parallel.Text = raw;
            // Deliver the production handler explicitly; no dispatcher pump or
            // claim about XAML event hookup/deferred TextChanged ordering.
            DeliverDraftChanged();
        }
        internal void AssertSummary(int effective, int requested, int cap)
        {
            Assert.Equal(Expected("setup.parallelEffective", effective, requested, cap), Summary.Text);
            AssertHelpMatchesSummary();
        }
        internal void AssertHelpMatchesSummary() => Assert.Equal(Texts.T("setup.positiveLimits") + " "
            + Summary.Text + " " + Texts.T("setup.parallelSafetyHint"), AutomationProperties.GetHelpText(Parallel));
    }

    private static void Set(FirstRunSetupWindow window, string name, object? value) =>
        typeof(FirstRunSetupWindow).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(window, value);
    private static T Get<T>(FirstRunSetupWindow window, string name) =>
        (T)typeof(FirstRunSetupWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Invoke(FirstRunSetupWindow window, string name, params object?[] args) =>
        typeof(FirstRunSetupWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
}
