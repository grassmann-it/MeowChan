using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// The "Header tagline" setting (Interface block): Off, Always the same (with the quip to keep), New every day, New at every start. The quips offered
/// are those the active language has translated, so the choice never lists an English text in a translated settings page.
/// </summary>
public partial class SettingsEditor
{
    private SettingsChoice? _selectedHeaderTagline, _selectedHeaderTaglineFixed;

    public string HeaderTaglineLabel => Texts.T("settings.headerTagline.label");
    public string HeaderTaglineHint => Texts.T("settings.headerTagline.hint");
    public string HeaderTaglineFixedLabel => Texts.T("settings.headerTagline.fixedLabel");
    public string HeaderTaglineFixedHint => Texts.T("settings.headerTagline.fixedHint");
    public IReadOnlyList<SettingsChoice> HeaderTaglineChoices { get; private set; } = Array.Empty<SettingsChoice>();
    public IReadOnlyList<SettingsChoice> HeaderTaglineFixedChoices { get; private set; } = Array.Empty<SettingsChoice>();

    /// <summary>The quip row is usable only while the mode is "Always the same".</summary>
    public bool HeaderTaglineFixedEnabled => SelectedHeaderTagline?.Value == TaglinePicker.ModeFixed;

    public SettingsChoice? SelectedHeaderTagline
    {
        get => _selectedHeaderTagline;
        set
        {
            SetField(ref _selectedHeaderTagline, value, nameof(SelectedHeaderTagline));
            RaiseHeaderTaglineChanged(nameof(HeaderTaglineFixedEnabled));
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
            new(TaglinePicker.ModePerStart, Texts.T("settings.headerTagline.perStart"))
        };
        IReadOnlyList<TaglineEntry> pool = TaglineCatalog.Available();
        HeaderTaglineFixedChoices = pool.Select(entry => new SettingsChoice(entry.Id, entry.Text)).ToArray();
        foreach (string property in new[] { nameof(HeaderTaglineLabel), nameof(HeaderTaglineHint), nameof(HeaderTaglineFixedLabel),
            nameof(HeaderTaglineFixedHint), nameof(HeaderTaglineChoices), nameof(HeaderTaglineFixedChoices) })
            RaiseHeaderTaglineChanged(property);
        string normalized = TaglinePicker.NormalizeMode(mode);
        SelectedHeaderTagline = HeaderTaglineChoices.First(item => item.Value == normalized);
        // A stored id this language cannot show (or one that no longer exists) shows the classic quip, else the first available.
        SelectedHeaderTaglineFixed = HeaderTaglineFixedChoices.FirstOrDefault(item => item.Value == fixedId)
            ?? HeaderTaglineFixedChoices.FirstOrDefault(item => item.Value == TaglineCatalog.DefaultId) ?? HeaderTaglineFixedChoices.FirstOrDefault();
    }

    private void RaiseHeaderTaglineChanged(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
