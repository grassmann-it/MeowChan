using System.Text.Json;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruGallerySettingsTests
{
    [Theory]
    [InlineData(nameof(Config.BooruGalleryMaximumPixels), 40_000_000, 1_000_000, 80_000_000)]
    [InlineData(nameof(Config.BooruGalleryMaximumWidth), 2048, 256, 4096)]
    [InlineData(nameof(Config.BooruGalleryTimeoutSeconds), 60, 1, 90)]
    public void NewLimitsValidateAndSurviveCopyAndSettingsTransfer(string name, int defaultValue, int min, int max)
    {
        var setting = typeof(Config).GetProperty(name)!;
        Config oldProfile = JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Config)!;
        Assert.Equal(defaultValue, setting.GetValue(oldProfile));
        Config source = new();
        foreach (int invalid in new[] { min - 1, max + 1 })
        {
            setting.SetValue(source, invalid);
            Assert.Equal(name, Assert.Throws<ArgumentOutOfRangeException>(source.Validate).ParamName);
        }
        foreach (int valid in new[] { min, max })
        {
            setting.SetValue(source, valid);
            source.Validate();
        }

        Config copy = new();
        copy.CopyFrom(source);
        Assert.Equal(max, setting.GetValue(copy));
        Assert.Equal(max, setting.GetValue(source.Clone()));
        Config imported = new();
        Config exported = SettingsTransfer.Export(source).Deserialize(ConfigJsonContext.Default.Config)!;
        imported.CopySettingsFieldsFrom(exported);
        Assert.Equal(max, setting.GetValue(imported));
    }

    [Theory]
    [InlineData("BooruSearchTabs", 5, 8)]
    [InlineData("BooruGallerySampleMiB", 3, 8)]
    [InlineData("BooruGalleryCacheMiB", 16, 32)]
    [InlineData("BooruGalleryMaximumPixels", 20_000_000, 40_000_000)]
    [InlineData("BooruGalleryMaximumWidth", 1024, 2048)]
    [InlineData("BooruGalleryTimeoutSeconds", 15, 60)]
    public void EachBooruNumberCanBeResetFromItsOwnRow(string name, int edited, int defaultValue) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        SettingsEditor editor = new(new Config());
        try
        {
            NumericUpDown field = editor.FindControl<NumericUpDown>(name + "Input")!;
            var draft = typeof(SettingsEditor).GetProperty(name)!;
            draft.SetValue(editor, (decimal)edited);
            field.Value = edited;

            ContextMenuEntry ResetEntry() => editor.ComposeRowMenu(field)!.ToModel().Entries.Single(entry => entry.CommandId == "settings.reset-default");
            ContextMenuEntry reset = ResetEntry();
            Assert.True(reset.Available, reset.Reason);
            reset.Invoke!();
            Assert.Equal((decimal?)defaultValue, field.Value);
            Assert.Equal((decimal)defaultValue, draft.GetValue(editor));
            Assert.False(ResetEntry().Available);
            Assert.Equal(Texts.T("ctxmenu.setting.atDefault"), ResetEntry().Reason);

            draft.SetValue(editor, (decimal)edited);
            field.Value = edited;
            editor.ResetDraft(SettingsEditor.SettingsResetScope.WorkerBudgets);
            Assert.Equal((decimal?)defaultValue, field.Value);
        }
        finally { editor.EndLifetime(); }
    });
}
