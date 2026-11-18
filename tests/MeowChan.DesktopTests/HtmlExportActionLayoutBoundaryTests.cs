using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeowChan.DesktopTests;

// Unit-safe presenter properties plus bounded source contracts.
// NOT measured layout or native QA; root must execute/reconcile.
// No application bootstrap, font service, window, profile or language mutation.
public sealed class HtmlExportActionLayoutBoundaryTests
{
    [Theory]
    [InlineData("ArchiveExportLegacyFind", "export.legacyFind")]
    [InlineData("ArchiveExportLegacyAssociate", "export.legacyAssociate")]
    [InlineData("ArchiveExportPrevious", "export.previous")]
    [InlineData("ArchiveExportAssociate", "export.associate")]
    public void LongDialogActionsHaveExplicitUntrimmedWrappingContent(string name, string key)
    {
        string source = Read("Desktop/Views/Archive/ArchivePage.Export.Dialog.cs");
        string localize = source[source.IndexOf("private void LocalizeExport()", StringComparison.Ordinal)..];
        Assert.Contains($"ApplyExportActionLabel(Control<Button>(\"{name}\"), Texts.T(\"{key}\"));", localize);
        Assert.DoesNotContain($"Control<Button>(\"{name}\").Content = Texts.T", localize);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void ProductionHelperPreservesFullLabelAccessibilityAndPresenterAcrossRefresh(string language)
    {
        using JsonDocument pack = JsonDocument.Parse(Read($"Desktop/Localization/Packs/{language}.json"));
        JsonElement strings = pack.RootElement.GetProperty("strings");
        Button button = new();
        foreach (string key in new[] { "export.legacyFind", "export.legacyAssociate", "export.previous", "export.associate" })
        {
            string label = strings.GetProperty(key).GetString()!;
            object? previous = button.Content;
            ArchivePage.ApplyExportActionLabel(button, label);
            TextBlock text = Assert.IsType<TextBlock>(button.Content);
            if (previous != null) Assert.Same(previous, text);
            Assert.Equal(label, text.Text);
            Assert.Equal(label, AutomationProperties.GetName(button));
            Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
            Assert.Equal(TextTrimming.None, text.TextTrimming);
            Assert.Equal(HorizontalAlignment.Stretch, button.HorizontalAlignment);
            Assert.Equal(HorizontalAlignment.Stretch, button.HorizontalContentAlignment);
            Assert.True(double.IsNaN(button.Width));
            Assert.True(double.IsNaN(text.Width));
            Assert.True(double.IsNaN(text.Height));
            Assert.Equal(0, text.MaxLines);
        }
    }

    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void BundledLongActionResourcesRemainFullAndReachTheirActualDialogControls(string language)
    {
        using JsonDocument pack = JsonDocument.Parse(Read($"Desktop/Localization/Packs/{language}.json"));
        JsonElement strings = pack.RootElement.GetProperty("strings");
        string dialog = Read("Desktop/Views/Archive/ArchivePage.Export.Dialog.cs");
        foreach (var (name, key) in new[]
        {
            ("ArchiveExportLegacyFind", "export.legacyFind"),
            ("ArchiveExportLegacyAssociate", "export.legacyAssociate")
        })
        {
            string label = strings.GetProperty(key).GetString()!;
            Assert.True(label.Length > 40, $"{language}/{key}: long-label boundary fixture was shortened.");
            Assert.DoesNotContain("…", label);
            Assert.DoesNotContain("...", label);
            Assert.Contains($"\"{name}\"", dialog);
            Assert.Contains($"Texts.T(\"{key}\")", dialog);
        }
    }

    [Fact]
    public void SingleCollectionAndResultsShareTheWidthConstrainedDialogRatherThanSeparateActionMarkup()
    {
        string core = Read("Desktop/Views/Archive/ArchivePage.Export.cs");
        string collection = Read("Desktop/Views/Archive/ArchivePage.Export.Collection.cs");
        string results = Read("Desktop/Views/Archive/ArchivePage.Export.Results.cs");
        string dialog = Read("Desktop/Views/Archive/ArchivePage.Export.Dialog.cs");
        Assert.Contains("OpenExportForThreadAsync(ResourceId id", core);
        Assert.Contains("EnsureExportDialog();", core);
        Assert.Contains("OpenExportCoreAsync(", collection);
        Assert.Contains("EnsureExportDialog();", results);
        Assert.Contains("MaxWidth = 680", dialog);
        Assert.Contains("HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled", dialog);
        Assert.Contains("historyActions.Children.Add(prior); historyActions.Children.Add(associate)", dialog);
        Assert.Contains("legacyContent.Children.Add(legacyFind)", dialog);
        Assert.Contains("legacyContent.Children.Add(legacyAssociate)", dialog);
        // Finite-width chain: disabled horizontal scrolling -> vertical stack
        // -> stretch button/presenter. The legacy expander must stretch content too.
        Assert.Contains("StackPanel historyActions = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch }", dialog);
        Assert.Contains("Content = legacyContent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch", dialog);
    }

    private static string Read(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../..", relative)));
}
