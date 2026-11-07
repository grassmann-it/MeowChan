using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class DiagnosticWaitingFilterTests
{
    [Fact]
    public void DefaultPreservesExistingVisibilityForEveryKind()
    {
        foreach (DiagnosticKind kind in Enum.GetValues<DiagnosticKind>())
        {
            DiagnosticEntry entry = Entry(kind, 1);
            Assert.True(DiagnosticRetryScope.Matches(entry, null, null, false));
            Assert.True(DiagnosticRetryScope.Matches(entry, null, null, false, includeWaiting: true));
            Assert.Equal(entry.IsProblem, DiagnosticRetryScope.Matches(entry, null, null, true));
            Assert.Equal(entry.IsProblem, DiagnosticRetryScope.Matches(entry, null, null, true, includeWaiting: false));
            Assert.Equal(entry.IsProblem || kind is DiagnosticKind.Request or DiagnosticKind.PendingWork,
                DiagnosticRetryScope.Matches(entry, null, null, true, includeWaiting: true));
        }
    }

    [Theory]
    [InlineData("Request")]
    [InlineData("PendingWork")]
    public void WaitingOptInKeepsSourceAndTextConstraintsAndNeverBecomesAProblem(string kindName)
    {
        DiagnosticEntry waiting = Entry(Enum.Parse<DiagnosticKind>(kindName), 7);
        Assert.False(waiting.IsProblem);
        Assert.False(DiagnosticRetryScope.Matches(waiting, null, null, true));
        Assert.True(DiagnosticRetryScope.Matches(waiting, "fixture", " COOLDOWN ", true, includeWaiting: true));
        Assert.True(DiagnosticRetryScope.Matches(waiting, "fixture", "fixture /b/ #7", true, includeWaiting: true));
        Assert.True(DiagnosticRetryScope.Matches(waiting, null, "resource", true, includeWaiting: true));
        Assert.False(DiagnosticRetryScope.Matches(waiting, "FIXTURE", null, true, includeWaiting: true));
        Assert.False(DiagnosticRetryScope.Matches(waiting, "other", null, true, includeWaiting: true));
        Assert.False(DiagnosticRetryScope.Matches(waiting, null, "unmatched", true, includeWaiting: true));
        // Do not silently broaden the established search into raw details.
        Assert.False(DiagnosticRetryScope.Matches(waiting, null, "details-only-sentinel", true, includeWaiting: true));
        Assert.False(waiting.IsProblem);
        Assert.Empty(DiagnosticRetryScope.Capture(new[] { waiting, waiting with { CheckAlso = true } }));
    }

    [Fact]
    public async Task VisibleWaitingOnlyRowsCannotSubmitBatchActionsEvenWithPermissiveAdmission()
    {
        DiagnosticEntry[] waiting = { Entry(DiagnosticKind.Request, 1), Entry(DiagnosticKind.PendingWork, 2) };
        DiagnosticEntry[] visible = waiting.Where(entry => DiagnosticRetryScope.Matches(entry,
            null, null, true, includeWaiting: true)).ToArray();
        Assert.Equal(2, visible.Length);
        int admissions = 0, checks = 0, media = 0;
        DiagnosticRetryResult result = await DiagnosticRetryScope.ExecuteAsync(DiagnosticRetryScope.Capture(visible),
            (_, _) => { admissions++; return true; },
            _ => { checks++; return Task.FromResult(CheckRequestStatus.Scheduled); },
            _ => { media++; return Task.FromResult(1); });
        Assert.Equal((0, 0, 0), (admissions, checks, media));
        Assert.Equal((0, 0, 0, 0, 0, 0),
            (result.Targets, result.Checks, result.AlreadyScheduled, result.Files, result.Skipped, result.Failures));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void MixedVisibleRowsKeepExactlyTheProblemRetryScope()
    {
        DiagnosticEntry[] entries = {
            Entry(DiagnosticKind.Media, 1), Entry(DiagnosticKind.Check, 2),
            Entry(DiagnosticKind.Request, 1) with { CheckAlso = true },
            Entry(DiagnosticKind.PendingWork, 3), Entry(DiagnosticKind.Request, 4),
            Entry(DiagnosticKind.Storage, 5), Entry(DiagnosticKind.Information, 6)
        };
        DiagnosticEntry[] strict = entries.Where(entry => DiagnosticRetryScope.Matches(entry, null, null, true)).ToArray();
        DiagnosticEntry[] expanded = entries.Where(entry => DiagnosticRetryScope.Matches(entry, null, null,
            true, includeWaiting: true)).ToArray();
        Assert.Equal(3, strict.Length);
        Assert.Equal(6, expanded.Length);
        DiagnosticRetryTarget[] scope = DiagnosticRetryScope.Capture(expanded);
        Assert.Equal(DiagnosticRetryScope.Capture(strict), scope);
        Assert.Equal(2, scope.Length);
        Assert.False(scope[0].Check); // A waiting row's CheckAlso is not authority.
        Assert.True(scope[0].Media);
        Assert.True(scope[1].Check);
        Assert.False(scope[1].Media);
    }

    [Fact]
    public async Task ChangingWaitingVisibilityDuringAwaitDoesNotRetargetFrozenBatch()
    {
        DiagnosticEntry[] entries = {
            Entry(DiagnosticKind.Media, 1), Entry(DiagnosticKind.Check, 2), Entry(DiagnosticKind.PendingWork, 3)
        };
        DiagnosticEntry[] visible = entries.Where(entry => DiagnosticRetryScope.Matches(entry, null, null,
            true, includeWaiting: true)).ToArray();
        TaskCompletionSource<int> held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> calls = new();
        Task<DiagnosticRetryResult> running = DiagnosticRetryScope.ExecuteAsync(DiagnosticRetryScope.Capture(visible),
            (_, _) => true,
            target => { calls.Add("check:" + target.WatchId); return Task.FromResult(CheckRequestStatus.Scheduled); },
            target => { calls.Add("media:" + target.WatchId); return held.Task; });
        Assert.Equal(new[] { "media:1" }, calls);
        // The filter and newly arrived problems are presentation state, not a new action intent.
        visible = new[] { Entry(DiagnosticKind.Check, 99) }.Where(entry =>
            DiagnosticRetryScope.Matches(entry, null, null, true, includeWaiting: false)).ToArray();
        Assert.Equal(99L, Assert.Single(visible).Target!.WatchId);
        held.SetResult(1);
        DiagnosticRetryResult result = await running;
        Assert.Equal(new[] { "media:1", "check:2" }, calls);
        Assert.Equal((2, 1, 1, 0), (result.Targets, result.Checks, result.Files, result.Failures));
    }

    [Fact]
    public void MarkupKeepsNeutralWrappingOptInAndVisibleCanonicalCopyNotice()
    {
        XElement root = XDocument.Load(Source("DiagnosticsPage.axaml")).Root!;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement waiting = root.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "DiagnosticIncludeWaiting");
        Assert.Equal("CheckBox", waiting.Name.LocalName);
        Assert.Equal("False", (string?)waiting.Attribute("IsChecked"));
        Assert.Equal("OnDiagnosticFilterChanged", (string?)waiting.Attribute("IsCheckedChanged"));
        Assert.NotNull(waiting.Attribute("MaxWidth"));
        XElement label = Assert.Single(waiting.Elements());
        Assert.Equal("diagnostics.includeWaiting", (string?)label.Attribute("Tag"));
        Assert.Equal("localized", (string?)label.Attribute("Classes"));
        Assert.Equal("Wrap", (string?)label.Attribute("TextWrapping"));
        XElement privacy = root.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "DiagnosticCopyPrivacy");
        Assert.Equal("diagnostics.copyPrivacy", (string?)privacy.Attribute("Tag"));
        Assert.Equal("muted localized", (string?)privacy.Attribute("Classes"));
        Assert.Equal("Wrap", (string?)privacy.Attribute("TextWrapping"));
        Assert.Null(privacy.Attribute("IsVisible"));
        Assert.Equal("1", (string?)privacy.Parent!.Attribute("Grid.Row")); // Always-visible action footer.
    }

    [Fact]
    public void SourceWiringResetsOptInAndUsesExistingLatestLanguageRefresh()
    {
        // Source contract only; not a native layout, binding or language-switch execution test.
        string bulk = File.ReadAllText(Source("DiagnosticsPage.BulkRetry.cs"));
        Assert.Contains("this.FindControl<CheckBox>(\"DiagnosticIncludeWaiting\")!.IsChecked = false;", bulk);
        Assert.Contains("DiagnosticRetryScope.Matches(entry, _diagnosticSourceId, query, problems, includeWaiting)", bulk);
        Assert.Contains("AutomationProperties.SetName(this.FindControl<CheckBox>(\"DiagnosticIncludeWaiting\")!, Texts.T(\"diagnostics.includeWaiting\"))", bulk);
        Assert.DoesNotContain("this.FindControl<CheckBox>(\"DiagnosticIncludeWaiting\")!.Content =", bulk);
        string page = File.ReadAllText(Source("DiagnosticsPage.axaml.cs"));
        Assert.Contains("DiagnosticsSnapshot.From(view, GetTargetWaitStates?.Invoke())", page);
        Assert.Contains("if (control is TextBlock text) text.Text = Texts.T(key);", page);
        Assert.Contains("RefreshDiagnosticFilterLabels();", page);
        Assert.Contains("AutomationProperties.SetHelpText(this.FindControl<Button>(\"CopyDiagnosticButton\")!, Texts.T(\"diagnostics.copyPrivacy\"))", page);
    }

    private static DiagnosticEntry Entry(DiagnosticKind kind, long watch) =>
        new(kind + ":" + watch, "neutral category", "same resource", "Cooldown", "details-only-sentinel",
            new(DiagnosticTargetKind.Thread, watch)) { Kind = kind, ResourceId = new("fixture", "b", watch.ToString()) };

    private static string Source(string name, [CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../Desktop/Views/Diagnostics", name));
}
