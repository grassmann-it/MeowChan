using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class CatalogLimitSettingsTests
{
    [Fact]
    public void LegacyDefaultsAndPortableProfileRoundTrip()
    {
        Config legacy = JsonSerializer.Deserialize<Config>("{}")!;
        Assert.Equal((8, 60), Limits(legacy));

        using TempDirectory directory = new();
        Config original = new()
        {
            DownloadPath = Path.Combine(directory.Path, "media"), BoardArchiveCacheLimit = 64, BooruHiddenTagTooltipLimit = 1
        };
        original.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);

        Config imported = SettingsTransfer.Export(loaded, "1.1.2").Deserialize<Config>()!;
        Config applied = new();
        applied.CopySettingsFieldsFrom(imported);
        Config copied = new();
        copied.CopyFrom(applied);
        foreach (Config config in new[] { loaded, imported, applied, copied, copied.Clone() })
            Assert.Equal((64, 1), Limits(config));
    }

    [Theory]
    [InlineData(0, 60, nameof(Config.BoardArchiveCacheLimit), 1, 60)]
    [InlineData(65, 60, nameof(Config.BoardArchiveCacheLimit), 64, 60)]
    [InlineData(8, 0, nameof(Config.BooruHiddenTagTooltipLimit), 8, 1)]
    [InlineData(8, 201, nameof(Config.BooruHiddenTagTooltipLimit), 8, 200)]
    public void RejectsInvalidDraftsAndClampsPersistedValues(int boards, int tags, string field, int validBoards, int validTags)
    {
        Config draft = new() { BoardArchiveCacheLimit = boards, BooruHiddenTagTooltipLimit = tags };
        Assert.Equal(field, Assert.Throws<ArgumentOutOfRangeException>(draft.Validate).ParamName);
        Assert.Equal((boards, tags), Limits(draft));
        Config read = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(draft))!;
        Assert.Equal((validBoards, validTags), Limits(read));
        read.Validate();
    }

    [Fact]
    public void EditorValidatesAndResetsBothLimits() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory directory = new();
        Config? saved = null;
        Config config = new()
        {
            DownloadPath = Path.Combine(directory.Path, "media"), BoardArchiveCacheLimit = 1, BooruHiddenTagTooltipLimit = 200
        };
        SettingsEditor editor = new(config, candidate => { saved = candidate; return Task.CompletedTask; }) { IsAvailable = true };
        try
        {
            NumericUpDown boards = Input(editor, "BoardArchiveCacheLimitInput");
            NumericUpDown tags = Input(editor, "BooruHiddenTagTooltipLimitInput");
            Assert.Equal((1m, 64m, (decimal?)1m), (boards.Minimum, boards.Maximum, boards.Value));
            Assert.Equal((1m, 200m, (decimal?)200m), (tags.Minimum, tags.Maximum, tags.Value));
            Assert.Equal(Texts.T("budgets.boardArchiveCacheLimit"), AutomationProperties.GetName(boards));
            Assert.Equal(Texts.T("budgets.booruHiddenTagTooltipLimit"), AutomationProperties.GetName(tags));

            tags.Text = "201";
            ClickSave(editor);
            Assert.True(editor.HasError);
            Assert.Null(saved);

            editor.ComposeRowMenu(boards)!.ToModel().Entries.Single(entry => entry.CommandId == "settings.reset-default").Invoke!();
            Assert.Equal(8, boards.Value);
            editor.ResetDraft(SettingsEditor.SettingsResetScope.WorkerBudgets);
            Assert.Equal(60, tags.Value);
            ClickSave(editor);
            BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(() => !editor.IsBusy, "catalog limits settings save"));
            Assert.False(editor.HasError, editor.ErrorMessage);
            Assert.Equal((8, 60), Limits(Assert.IsType<Config>(saved)));
        }
        finally { editor.EndLifetime(); }
    });

    [Theory]
    [InlineData("BoardArchiveCacheLimitInput", 8)]
    [InlineData("BooruHiddenTagTooltipLimitInput", 60)]
    public void RowResetRestoresInvalidTextWhenTheValueIsAlreadyDefault(string id, int defaultValue) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory directory = new();
        Config? saved = null;
        Config config = new() { DownloadPath = Path.Combine(directory.Path, "media") };
        SettingsEditor editor = new(config, candidate => { saved = candidate; return Task.CompletedTask; }) { IsAvailable = true };
        try
        {
            NumericUpDown input = Input(editor, id);
            string defaultText = Assert.IsType<string>(input.Text);
            Assert.False(ResetEntry().Available);

            input.Text = "abc";
            Assert.Equal(defaultValue, input.Value);
            ClickSave(editor);
            Assert.True(editor.HasError);
            Assert.Null(saved);

            ContextMenuEntry reset = ResetEntry();
            Assert.True(reset.Available, reset.Reason);
            reset.Invoke!();
            Assert.Equal(defaultText, input.Text);
            Assert.False(ResetEntry().Available);

            ClickSave(editor);
            BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(() => !editor.IsBusy, "the reset draft to save"));
            Assert.False(editor.HasError, editor.ErrorMessage);
            Assert.Equal((8, 60), Limits(Assert.IsType<Config>(saved)));

            ContextMenuEntry ResetEntry() => Assert.Single(editor.ComposeRowMenu(input)!.ToModel().Entries,
                entry => entry.CommandId == SettingsRowMenu.Reset);
        }
        finally { editor.EndLifetime(); }
    });

    [Theory]
    [InlineData("BoardArchiveCacheLimitInput")]
    [InlineData("BooruHiddenTagTooltipLimitInput")]
    public void EditingALimitRetiresTheSavedStatus(string id) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory directory = new();
        Config config = new() { DownloadPath = Path.Combine(directory.Path, "media") };
        SettingsEditor editor = new(config, _ => Task.CompletedTask) { IsAvailable = true };
        try
        {
            NumericUpDown input = Input(editor, id);
            SaveAndCheckStatus();
            input.Value += input.Increment;
            Assert.Equal(Texts.T("settings.unsaved"), editor.StatusMessage);

            SaveAndCheckStatus();
            input.Text = "abc";
            Assert.Equal(Texts.T("settings.unsaved"), editor.StatusMessage);
        }
        finally { editor.EndLifetime(); }

        void SaveAndCheckStatus()
        {
            ClickSave(editor);
            BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(() => !editor.IsBusy, "the limit draft to save"));
            Assert.Equal(Texts.T("settings.saved"), editor.StatusMessage);
        }
    });

    private static NumericUpDown Input(SettingsEditor editor, string id) => Assert.Single(editor.GetLogicalDescendants().OfType<NumericUpDown>(),
        control => AutomationProperties.GetAutomationId(control) == id);

    private static void ClickSave(SettingsEditor editor)
    {
        Button save = Assert.Single(editor.GetLogicalDescendants().OfType<Button>(), button =>
            ControlAutomationPeer.CreatePeerForElement(button)?.GetAutomationId() == "SaveSettingsButton");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static (int, int) Limits(Config config) => (config.BoardArchiveCacheLimit, config.BooruHiddenTagTooltipLimit);
}
