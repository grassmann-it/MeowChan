using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SettingsEditor
{
    private sealed record BoardSuggestionSetting(string Name, string LabelKey, int Default, int Min, int Max,
        Func<Config, int> Read, Action<Config, int> Write, string UnitKey = "settings.unitCount");
    private sealed record BoardSuggestionField(BoardSuggestionSetting Setting, SettingsNumberInput Input,
        TextBlock Label, TextBlock Hint, TextBlock Error);

    private static readonly BoardSuggestionSetting[] BoardSuggestionSettings =
    [
        new(nameof(Config.BoardSuggestionMinThreads), "settings.boardSuggestions.minimum", Config.BoardSuggestionMinThreadsDefault,
            Config.BoardSuggestionMinThreadsMinimum, Config.BoardSuggestionMinThreadsMaximum,
            config => config.BoardSuggestionMinThreads, (config, value) => config.BoardSuggestionMinThreads = value),
        new(nameof(Config.BoardSuggestionLimit), "settings.boardSuggestions.limit", Config.BoardSuggestionLimitDefault,
            Config.BoardSuggestionLimitMinimum, Config.BoardSuggestionLimitMaximum,
            config => config.BoardSuggestionLimit, (config, value) => config.BoardSuggestionLimit = value),
        new(nameof(Config.BoardSuggestionScanThreads), "settings.boardSuggestions.scan", Config.BoardSuggestionScanThreadsDefault,
            Config.BoardSuggestionScanThreadsMinimum, Config.BoardSuggestionScanThreadsMaximum,
            config => config.BoardSuggestionScanThreads, (config, value) => config.BoardSuggestionScanThreads = value),
        new(nameof(Config.BoardWatcherIntervalMinutes), "settings.boardWatcher.interval", Config.BoardWatcherIntervalMinutesDefault,
            Config.BoardWatcherIntervalMinutesMinimum, Config.BoardWatcherIntervalMinutesMaximum,
            config => config.BoardWatcherIntervalMinutes, (config, value) => config.BoardWatcherIntervalMinutes = value, "settings.unitMinutes"),
    ];

    private readonly List<BoardSuggestionField> _boardSuggestionFields = new();
    private readonly TextBlock _boardSuggestionHeading = new()
    {
        Classes = { "section-title" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
    };

    private void InitializeBoardSuggestionFields()
    {
        var rows = new StackPanel { Children = { _boardSuggestionHeading } };
        foreach (var setting in BoardSuggestionSettings)
        {
            var input = new SettingsNumberInput { Name = setting.Name + "Input", Minimum = setting.Min, Maximum = setting.Max };
            input.ValueChanged += (_, _) =>
            {
                _draftRevision++;
                RetireSavedStatus();
            };
            var label = new TextBlock { Classes = { "workspace-field-label" }, TextWrapping = TextWrapping.Wrap };
            var hint = new TextBlock { Classes = { "workspace-help" }, TextWrapping = TextWrapping.Wrap };
            var error = new TextBlock { Name = input.Name + "Error", Classes = { "workspace-error" },
                TextWrapping = TextWrapping.Wrap, IsVisible = false };
            AutomationProperties.SetAutomationId(input, setting.Name + "Input");
            AutomationProperties.SetLabeledBy(input, label);
            AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
            var editor = new StackPanel { Spacing = 4, Children = { input, error } };
            rows.Children.Add(SettingsRowFactory.Create([label, hint], editor));
            _boardSuggestionFields.Add(new(setting, input, label, hint, error));
        }
        var boardWizardRow = BoardAdmissionWizardInput.GetLogicalAncestors().OfType<Border>()
            .First(row => row.Classes.Contains(SettingsRowFactory.DividerClass));
        SettingsBehaviorBody.Children.Insert(SettingsBehaviorBody.Children.IndexOf(boardWizardRow) + 1, rows);
        UpdateBoardSuggestionTexts();
    }

    private void LoadBoardSuggestionDraft(Config config)
    {
        if (_boardSuggestionFields.Count == 0) InitializeBoardSuggestionFields();
        foreach (var field in _boardSuggestionFields) ResetNumber(field.Input, field.Setting.Read(config));
    }

    private void ApplyBoardSuggestionDraft(Config config)
    {
        foreach (var field in _boardSuggestionFields)
            field.Setting.Write(config, GetInteger(field.Input.Value, field.Label.Text!, field.Setting.Min, field.Setting.Max));
    }

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> BoardSuggestionNumericFields()
    {
        foreach (var field in _boardSuggestionFields)
            yield return (field.Input, field.Input.Value, field.Label.Text!, Texts.T(field.Setting.UnitKey), field.Setting.Min, field.Setting.Max, true);
    }

    private IEnumerable<DefaultSetting> BoardSuggestionDefaults() => _boardSuggestionFields.Select(field =>
        Number(field.Input, () => field.Input.Value, value => field.Input.Value = value, config => field.Setting.Read(config)));

    private TextBlock? BoardSuggestionError(string name) => _boardSuggestionFields.FirstOrDefault(field => field.Error.Name == name)?.Error;

    private void UpdateBoardSuggestionTexts()
    {
        _boardSuggestionHeading.Text = Texts.T("downloads.boards");
        foreach (var field in _boardSuggestionFields)
        {
            var setting = field.Setting;
            field.Label.Text = Texts.T(setting.LabelKey);
            field.Hint.Text = Texts.T("settings.booruList.default", setting.Default) + " "
                + Texts.T("settings.reader.range", setting.Min, setting.Max);
            AutomationProperties.SetName(field.Input, field.Label.Text);
        }
        UpdateBoardSuggestionHelp();
    }

    private void UpdateBoardSuggestionHelp()
    {
        foreach (var field in _boardSuggestionFields)
            AutomationProperties.SetHelpText(field.Input, FieldHelp(field.Input.Name!, field.Hint.Text!));
    }
}
