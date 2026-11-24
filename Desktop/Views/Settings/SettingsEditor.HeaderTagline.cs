using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// The "Header tagline" setting (Interface block): Off, Always the same (with the quip to keep), New every day, New at every start, New every N minutes (with the minutes between two changes). The quips offered
/// are those the active language has translated, so the choice never lists an English text in a translated settings page.
/// </summary>
public partial class SettingsEditor
{
    private SettingsChoice? _selectedHeaderTagline, _selectedHeaderTaglineFixed;

    public string HeaderTaglineLabel => Texts.T("settings.headerTagline.label");
    public string HeaderTaglineHint => Texts.T("settings.headerTagline.hint");
    public string HeaderTaglineFixedLabel => Texts.T("settings.headerTagline.fixedLabel");
    public string HeaderTaglineFixedHint => Texts.T("settings.headerTagline.fixedHint");
    public string HeaderTaglineIntervalMinutesLabel => Texts.T("settings.headerTagline.intervalLabel");
    public string HeaderTaglineIntervalMinutesHint => Texts.T("settings.headerTagline.intervalHint",
        Config.HeaderTaglineIntervalMinutesDefault, Config.HeaderTaglineIntervalMinutesMinimum, Config.HeaderTaglineIntervalMinutesMaximum);
    public string HeaderTaglineIntervalMinutesHelp => FieldHelp(nameof(HeaderTaglineIntervalMinutesInput), Texts.T("settings.unitMinutes") + ". " + HeaderTaglineIntervalMinutesHint);
    private decimal? _headerTaglineIntervalMinutes = Config.HeaderTaglineIntervalMinutesDefault;
    public decimal? HeaderTaglineIntervalMinutes { get => _headerTaglineIntervalMinutes; set => SetField(ref _headerTaglineIntervalMinutes, value, nameof(HeaderTaglineIntervalMinutes)); }
    public IReadOnlyList<SettingsChoice> HeaderTaglineChoices { get; private set; } = Array.Empty<SettingsChoice>();
    public IReadOnlyList<SettingsChoice> HeaderTaglineFixedChoices { get; private set; } = Array.Empty<SettingsChoice>();

    /// <summary>The quip row is usable only while the mode is "Always the same".</summary>
    public bool HeaderTaglineFixedEnabled => SelectedHeaderTagline?.Value == TaglinePicker.ModeFixed;

    /// <summary>The minutes row is usable (and shown) only while the mode is "New every N minutes"; hidden, it never blocks a save.</summary>
    public bool HeaderTaglineIntervalEnabled => SelectedHeaderTagline?.Value == TaglinePicker.ModeInterval;

    public SettingsChoice? SelectedHeaderTagline
    {
        get => _selectedHeaderTagline;
        set
        {
            // Replacing translated mode labels briefly clears the ComboBox selection.
            // Keep the fixed row and its popup visible until the new mode choice binds.
            if (_refreshingLanguageLabels && value is null) return;
            SetField(ref _selectedHeaderTagline, value, nameof(SelectedHeaderTagline));
            RaiseHeaderTaglineChanged(nameof(HeaderTaglineFixedEnabled));
            RaiseHeaderTaglineChanged(nameof(HeaderTaglineIntervalEnabled));
        }
    }

    public SettingsChoice? SelectedHeaderTaglineFixed
    {
        get => _selectedHeaderTaglineFixed;
        set => SetField(ref _selectedHeaderTaglineFixed, value, nameof(SelectedHeaderTaglineFixed));
    }

    /// <summary>Rebuilds both lists in the current language and selects the given mode and quip id; also refreshes the labels.</summary>
    private void RefreshHeaderTaglineChoices(string mode, string fixedId)
    {
        HeaderTaglineChoices = new SettingsChoice[]
        {
            new(TaglinePicker.ModeOff, Texts.T("settings.headerTagline.off")),
            new(TaglinePicker.ModeFixed, Texts.T("settings.headerTagline.fixed")),
            new(TaglinePicker.ModeDaily, Texts.T("settings.headerTagline.daily")),
            new(TaglinePicker.ModePerStart, Texts.T("settings.headerTagline.perStart")),
            new(TaglinePicker.ModeInterval, Texts.T("settings.headerTagline.interval"))
        };
        IReadOnlyList<TaglineEntry> pool = TaglineCatalog.Available();
        var groups = pool.OrderBy(entry => entry.Category)
            .ThenBy(entry => entry.Text, StringComparer.Create(Texts.Culture, ignoreCase: true))
            .GroupBy(entry => entry.Category)
            .ToArray();
        HeaderTaglineFixedChoices = groups.SelectMany(group => group)
            .Select(entry => new SettingsChoice(entry.Id, entry.Text)).ToArray();
        if (HeaderTaglineFixedIdInput is { } picker)
        {
            var headings = groups.ToDictionary(group => group.First().Id, group => HeaderTaglineCategoryLabel(group.Key));
            // Keep headings with their first quip so keyboard navigation has one stop per choice.
            picker.SelectionBoxItemTemplate ??= picker.ItemTemplate;
            picker.ItemTemplate = new FuncDataTemplate<SettingsChoice>((choice, _) =>
            {
                if (choice is null) return null;
                TextBlock quip = new() { Text = choice.Label, TextWrapping = TextWrapping.Wrap };
                if (!headings.TryGetValue(choice.Value, out string? heading)) return quip;
                return new StackPanel
                {
                    Classes = { "workspace-field" },
                    Children = { new TextBlock { Text = heading, Classes = { "workspace-field-label" } }, quip }
                };
            });
        }
        foreach (string property in new[] { nameof(HeaderTaglineLabel), nameof(HeaderTaglineHint), nameof(HeaderTaglineFixedLabel),
            nameof(HeaderTaglineFixedHint), nameof(HeaderTaglineIntervalMinutesLabel), nameof(HeaderTaglineIntervalMinutesHint),
            nameof(HeaderTaglineIntervalMinutesHelp), nameof(HeaderTaglineChoices), nameof(HeaderTaglineFixedChoices) })
            RaiseHeaderTaglineChanged(property);
        string normalized = TaglinePicker.NormalizeMode(mode);
        SelectedHeaderTagline = HeaderTaglineChoices.First(item => item.Value == normalized);
        // Match the header fallback, including the original ID order when the classic quip is missing.
        string? firstId = pool.Count == 0 ? null : pool[0].Id;
        SelectedHeaderTaglineFixed = HeaderTaglineFixedChoices.FirstOrDefault(item => item.Value == fixedId)
            ?? HeaderTaglineFixedChoices.FirstOrDefault(item => item.Value == TaglineCatalog.DefaultId)
            ?? HeaderTaglineFixedChoices.FirstOrDefault(item => item.Value == firstId);
    }

    private static string HeaderTaglineCategoryLabel(TaglineCategory category) => category switch
    {
        TaglineCategory.Release => Texts.T("settings.headerTagline.category.release"),
        TaglineCategory.Seasonal => Texts.T("settings.headerTagline.category.seasonal"),
        _ => Texts.T("settings.headerTagline.category.general")
    };

    /// <summary>The minutes from <paramref name="minutes"/> into the draft (start, discard, reset).</summary>
    private void LoadHeaderTaglineIntervalDraft(int minutes)
    {
        HeaderTaglineIntervalMinutes = minutes;
        ResetNumber(HeaderTaglineIntervalMinutesInput, minutes);
    }

    /// <summary>The draft's minutes when the row applies (checked, a bad value blocks the save), else the valid draft or the saved value.</summary>
    private int HeaderTaglineIntervalForSave() => HeaderTaglineIntervalEnabled
        ? GetInteger(HeaderTaglineIntervalMinutes, HeaderTaglineIntervalMinutesLabel, Config.HeaderTaglineIntervalMinutesMinimum, Config.HeaderTaglineIntervalMinutesMaximum)
        : ViewerNumber(HeaderTaglineIntervalMinutes, Config.HeaderTaglineIntervalMinutesMinimum, Config.HeaderTaglineIntervalMinutesMaximum, _config.HeaderTaglineIntervalMinutes);

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> HeaderTaglineNumericFields()
    {
        yield return (HeaderTaglineIntervalMinutesInput, HeaderTaglineIntervalMinutes, HeaderTaglineIntervalMinutesLabel, Texts.T("settings.unitMinutes"),
            Config.HeaderTaglineIntervalMinutesMinimum, Config.HeaderTaglineIntervalMinutesMaximum, true);
    }

    private void RaiseHeaderTaglineChanged(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
