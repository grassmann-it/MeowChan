using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace MeowChan.DesktopTests;

// Source-contract regressions only: no native Window, dispatcher or provider is started.
public sealed class FirstRunLanguageRefreshTests
{
    [Theory]
    [InlineData("settings.pickerUnavailable")]
    [InlineData("settings.pickerLocalOnly")]
    [InlineData("setup.pickerFailed")]
    [InlineData("setup.saveUnconfirmed")]
    public void ActionErrorsRetainTheirHostKeyAndRefreshOnlyWhileVisible(string key)
    {
        string source = ReadSource();
        Assert.Contains($"ShowError(\"{key}\")", source);
        string show = Between(source, "private void ShowError(", "private void OnCancelCheckClick(");
        Assert.Contains("_errorKey = key;", show);
        Assert.Contains("SetupError.Text = Texts.T(key);", show);
        string refresh = Between(source, "private void RefreshLabels()", "private void OnLanguageChanged(");
        Assert.Contains("if (SetupError.IsVisible && _errorKey != null) SetupError.Text = Texts.T(_errorKey);", refresh);
        Assert.DoesNotContain("ShowError(Texts.T(", source);
    }

    [Fact]
    public void PathErrorRefreshUsesItsOwnOriginRatherThanAnEarlierCheckStatus()
    {
        string source = ReadSource();
        Assert.Contains("ShowPathError(\"settings.pathInvalid\");", source);
        Assert.Contains("ShowPathError(\"destination.\" + result.Status);", source);
        string show = Between(source, "private void ShowPathError(", "private void ShowError(");
        Assert.Contains("_pathErrorKey = key;", show);
        Assert.Contains("SetupPathError.Text = Texts.T(key);", show);
        string refresh = Between(source, "private void RefreshLabels()", "private void OnLanguageChanged(");
        Assert.Contains("if (SetupPathError.IsVisible && _pathErrorKey != null) SetupPathError.Text = Texts.T(_pathErrorKey);", refresh);
        Assert.DoesNotContain("SetupPathError.Text = _checkStatus", refresh);
    }

    [Fact]
    public void LanguageCallbackDispatchesAndGuardsQueuedWorkAfterCloseWithoutResubmittingDraft()
    {
        string source = ReadSource();
        Assert.Contains("Texts.Changed += OnLanguageChanged;", source);
        Assert.Contains("Texts.Changed -= OnLanguageChanged;", source);
        string callback = Between(source, "private void OnLanguageChanged(", "private void OnCleanupFailure(");
        Assert.Contains("Dispatcher.UIThread.Post(() =>", callback);
        Assert.True(callback.IndexOf("if (_closed) return;", StringComparison.Ordinal)
            < callback.IndexOf("RefreshLabels();", StringComparison.Ordinal));
        Assert.Contains("RefreshPreview();", callback);
        foreach (string forbidden in new[] { "SynchronizeDraft(", "TryReadDraft(", "CheckAsync(", "_accept(", ".Text =", "Close(" })
            Assert.DoesNotContain(forbidden, callback);
        string refresh = Between(source, "private void RefreshPreview()", "private void OnLanguageChanged(");
        foreach (string input in new[] { "SetupFolderInput", "SetupBandwidthInput", "SetupParallelInput" })
            Assert.DoesNotContain(input + ".Text =", refresh);
    }

    [Fact]
    public void StatusRefreshCoversHostStatesAndPreservesRawWarningAndCleanupPathArguments()
    {
        string source = ReadSource();
        string refresh = Between(source, "private void RefreshLabels()", "private void OnLanguageChanged(");
        foreach (string key in new[] { "setup.saving", "setup.checking", "setup.positiveLimits" })
            Assert.Contains($"Texts.T(\"{key}\")", refresh);
        Assert.Contains("Texts.T(\"destination.\" + (_checkStatus?.ToString() ?? \"NotChecked\"))", refresh);
        Assert.Contains("Texts.T(\"setup.savedWithWarning\", _result.Message)", refresh);
        Assert.Contains("RefreshCleanupWarning();", refresh);
        Assert.Contains("Texts.T(\"destination.cleanupWarning\", DownloadDestinationValidator.Shared.LastCleanupFailurePath", source);
        foreach (string raw in new[] { "_result.Message", "completion.ActivationWarning", "exception.Message", "result.Path" })
            Assert.DoesNotContain("Texts.T(" + raw, source);
    }

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, "Missing source anchor: " + start);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, "Missing source anchor: " + end);
        return source[first..last];
    }

    private static string ReadSource([CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!,
            "../../Desktop/Views/Settings/FirstRunSetupWindow.axaml.cs")));
}
