using System.Globalization;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MeowChan.Admission;
using MeowChan.Desktop.Localization;

namespace MeowChan;

internal sealed partial class BoardAdmissionWindow
{
    private sealed record WatcherFields(StackPanel View, TextBox Name, TextBox Interval)
    {
        internal string? BoardCode { get; set; }
        // Null means the user owns the field. A manually cleared name is kept blank.
        internal string? AutomaticName { get; set; }
    }

    private WatcherFields CreateWatcherFields(BoardAdmissionPolicy initial, bool isNew, int defaultInterval)
    {
        var name = new TextBox { Text = initial.WatcherName };
        int? minutes = isNew ? defaultInterval : initial.WatcherIntervalMinutes;
        var interval = new TextBox { Text = minutes?.ToString(CultureInfo.InvariantCulture), MinWidth = 80 };
        AutomationProperties.SetAutomationId(name, "BoardAdmissionWatcherName");
        AutomationProperties.SetAutomationId(interval, "BoardAdmissionWatcherInterval");
        var nameLabel = WatcherLabel(name, "wizard.watcher.name");
        var intervalLabel = WatcherLabel(interval, "wizard.watcher.interval");
        var unit = Localized(new TextBlock { VerticalAlignment = VerticalAlignment.Center },
            control => control.Text = Texts.T("wizard.watcher.minutes"));
        var intervalEditor = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { interval, unit } };
        var nameField = new StackPanel { Spacing = 4, Children = { nameLabel, name } };
        var intervalField = new StackPanel { Spacing = 4, Children = { intervalLabel, intervalEditor } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16, Children = { nameField, intervalField } };
        Grid.SetColumn(intervalField, 1);
        var hint = Help(string.Empty);
        AutomationProperties.SetLiveSetting(hint, AutomationLiveSetting.Polite);
        Localized(hint, control => UpdateWatcherIntervalText(interval, control));
        interval.TextChanged += (_, _) =>
        {
            if (_closing || _disposed || _apply == null) return;
            UpdateWatcherIntervalText(interval, hint);
            RefreshTotal();
        };
        return new(new StackPanel { Spacing = 4, Children = { row, hint } }, name, interval)
        { AutomaticName = isNew ? string.Empty : null };
    }

    private TextBlock WatcherLabel(TextBox input, string key)
    {
        var label = Localized(new TextBlock { Classes = { "workspace-field-label" } }, control =>
        {
            control.Text = Texts.T(key);
            AutomationProperties.SetName(input, control.Text);
        });
        AutomationProperties.SetLabeledBy(input, label);
        return label;
    }

    private static void UpdateWatcherIntervalText(TextBox interval, TextBlock hint)
    {
        bool invalid = !ReadWatcherInterval(interval.Text, out _);
        hint.Text = Texts.T(invalid ? "wizard.watcher.intervalInvalid" : "wizard.watcher.intervalHint",
            Config.BoardWatcherIntervalMinutesMinimum, Config.BoardWatcherIntervalMinutesMaximum);
        hint.Classes.Set("workspace-error", invalid);
        interval.Classes.Set("workspace-invalid", invalid);
        AutomationProperties.SetHelpText(interval, hint.Text);
    }

    private static bool ReadWatcherInterval(string? text, out int? minutes)
    {
        minutes = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            || parsed is < Config.BoardWatcherIntervalMinutesMinimum or > Config.BoardWatcherIntervalMinutesMaximum)
            return false;
        minutes = parsed;
        return true;
    }

    private static void RefreshWatcherName(Draft draft)
    {
        WatcherFields watcher = draft.Watcher;
        if (watcher.AutomaticName == null) return;
        if (watcher.AutomaticName != (watcher.Name.Text ?? string.Empty))
        {
            watcher.AutomaticName = null;
            return;
        }
        if (watcher.BoardCode == null) return;
        string[] patterns = draft.Editors.Select(editor => editor.Text.Text?.Trim())
            .OfType<string>().Where(pattern => pattern.Length != 0).ToArray();
        watcher.AutomaticName = patterns.Length switch
        {
            0 => string.Empty,
            1 => $"/{watcher.BoardCode}/ · {patterns[0]}",
            _ => $"/{watcher.BoardCode}/ · {Texts.T("wizard.watcher.patterns", patterns.Length)}"
        };
        if ((watcher.Name.Text ?? string.Empty) != watcher.AutomaticName)
            watcher.Name.Text = watcher.AutomaticName;
    }

    private static BoardAdmissionPolicy WatcherPolicy(Draft draft)
    {
        ReadWatcherInterval(draft.Watcher.Interval.Text, out int? minutes);
        return new(draft.Editors.Select(ReadRule), draft.Exceptions, draft.Watcher.Name.Text, minutes);
    }
}
