using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Real control-property regression for the targeted presentation rebind, not a
// native-window/input test. Binding journeys live in BoardAdmissionLanguageRefreshChecks.
// No preview delegate, profile or network is involved.
[Collection("Localization")]
public sealed class BoardAdmissionLanguageRefreshTests
{
    [Theory]
    [InlineData("admission.add-rule")]
    [InlineData("admission.cancel")]
    [InlineData("admission.retry-catalog")]
    [InlineData("admission.reinclude")]
    [InlineData("admission.remove-rule")]
    public void ExistingAdapterBoundButtonRefreshesTooltipAndAutomationHelp(string id)
    {
        string previousLanguage = Texts.Language;
        int calls = 0;
        var command = new DelegateCommand(() => calls++);
        var button = new Button { Content = "unchanged host label", Command = command, IsEnabled = false };
        MethodInfo refresh = typeof(BoardAdmissionWindow).GetMethod("RefreshCommandHelp",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Board admission command-help refresh is missing.");
        try
        {
            Texts.SetLanguage("en");
            CommandBindings.BindExisting(button, id);
            string initialTip = CommandBindings.Tooltip(id);
            Assert.Equal(initialTip, ToolTip.GetTip(button));
            foreach (string language in new[] { "de", "en", "de", "en" })
            {
                Texts.SetLanguage(language);
                // Re-registering is intentionally insufficient for a tracked control.
                // Exercise the same local rebind invoked by the dialog's Localized callbacks.
                CommandBindings.BindExisting(button, id);
                refresh.Invoke(null, new object[] { button, id, false });
                string expected = CommandBindings.Tooltip(id);
                Assert.Equal(expected, ToolTip.GetTip(button));
                Assert.Equal(expected, AutomationProperties.GetHelpText(button));
                if (language == "de") Assert.NotEqual(initialTip, expected);
                Assert.Same(command, button.Command);
                Assert.Equal("unchanged host label", button.Content);
                Assert.False(button.IsEnabled);
                Assert.Equal(0, calls);
            }
            command.Execute(null);
            Assert.Equal(1, calls);
        }
        finally { Texts.SetLanguage(previousLanguage); }
    }
    [Fact]
    public void ApplyTextsPreservesHelpAtTheActualApplyAndMaterializedResetCallers()
    {
        string source = ReadSource();
        string apply = Between(source, "private void ApplyTexts()", "public void Dispose()");
        Assert.Contains("RefreshCommandHelp(_apply, \"admission.apply\", preserveHelp: true);", apply);
        Assert.Contains("foreach (var reset in this.GetVisualDescendants().OfType<Button>()", apply);
        Assert.Contains(".Where(button => button.Name == \"BoardAdmissionResetChoice\"))", apply);
        Assert.Contains("RefreshCommandHelp(reset, \"admission.reset-choice\", preserveHelp: true);", apply);

        string refresh = Between(source, "private static void RefreshCommandHelp(", "private void OnTextsChanged(");
        Assert.Contains("ToolTip.SetTip(control, tip);", refresh);
        Assert.Contains("if (!preserveHelp) AutomationProperties.SetHelpText(control, tip);", refresh);
        Assert.DoesNotContain("AutomationProperties.SetName(", refresh);
        Assert.DoesNotContain(".Bind(", refresh);
        Assert.DoesNotContain(".ClearValue(", refresh);
        string template = Between(source, "private static FuncDataTemplate<PreviewRow> MakeTemplate()", "private void AddRule(");
        Assert.Contains("CommandBindings.BindExisting(reset, \"admission.reset-choice\", preserveHelp: true);", template);
    }

    [Theory]
    [InlineData("admission.apply")]
    [InlineData("admission.reset-choice")]
    public void RichHelpCanBeRegisteredFirstOrUpgradedWithoutGenericReRegistrationOverwritingIt(string id)
    {
        foreach (bool genericFirst in new[] { false, true })
        {
            var button = new Button();
            if (genericFirst) CommandBindings.BindExisting(button, id);
            AutomationProperties.SetName(button, "current row name");
            AutomationProperties.SetHelpText(button, "current pending summary");
            CommandBindings.BindExisting(button, id, preserveHelp: true);
            Assert.Equal("current pending summary", AutomationProperties.GetHelpText(button));
            Assert.Equal(CommandBindings.Tooltip(id), ToolTip.GetTip(button));
            AutomationProperties.SetHelpText(button, "later row summary");
            CommandBindings.BindExisting(button, id);
            Assert.Equal("later row summary", AutomationProperties.GetHelpText(button));
            Assert.Equal("current row name", AutomationProperties.GetName(button));
        }
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
            "../../Desktop/Views/Admission/BoardAdmissionWindow.cs")));
}
