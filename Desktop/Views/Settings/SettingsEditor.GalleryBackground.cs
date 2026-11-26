using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SettingsEditor
{
    private SettingsChoice? _selectedGalleryBackground;

    public IReadOnlyList<SettingsChoice> GalleryBackgroundChoices { get; private set; } = Array.Empty<SettingsChoice>();
    public SettingsChoice? SelectedGalleryBackground
    {
        get => _selectedGalleryBackground;
        set
        {
            // Replacing ItemsSource can temporarily clear the selection.
            if (value != null) SetField(ref _selectedGalleryBackground, value, nameof(SelectedGalleryBackground));
        }
    }

    public string GalleryBackgroundLabel => Texts.T("command.gallery.background");
    public string GalleryBackgroundHint => Texts.T("gallery.background.help");

    private void RefreshGalleryBackgroundChoices(string mode)
    {
        GalleryBackgroundChoices = Config.GalleryBackgroundModes
            .Select(value => new SettingsChoice(value, Texts.T("gallery.background." + value.ToLowerInvariant())))
            .ToArray();
        Raise(new[] { nameof(GalleryBackgroundChoices) });
        SettingsChoice selected = GalleryBackgroundChoices.First(item => item.Value == mode);
        if (_refreshingLanguageLabels)
        {
            _selectedGalleryBackground = selected;
            Raise(new[] { nameof(SelectedGalleryBackground) });
        }
        else SelectedGalleryBackground = selected;
    }

    internal void RefreshConfirmedGalleryBackground(string previous, string mode)
    {
        if (SelectedGalleryBackground?.Value != previous) return;
        _selectedGalleryBackground = GalleryBackgroundChoices.First(item => item.Value == mode);
        Raise(new[] { nameof(SelectedGalleryBackground) });
    }
}
