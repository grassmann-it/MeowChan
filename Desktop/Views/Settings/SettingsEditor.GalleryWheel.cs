using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// The "Gallery mouse wheel" row of the "Reading and media" page: what the wheel does over the gallery's picture or video, zoom (the
/// default) or browse to the next and previous medium; Ctrl does the other one. A choice row like the reader navigation; the draft
/// is loaded, discarded, saved and reset with the viewer rows (SettingsEditor.Viewer.cs calls the two members below).
/// </summary>
public partial class SettingsEditor
{
    private SettingsChoice? _selectedGalleryWheelMode;

    public IReadOnlyList<SettingsChoice> GalleryWheelModeChoices { get; private set; } = Array.Empty<SettingsChoice>();

    public SettingsChoice? SelectedGalleryWheelMode { get => _selectedGalleryWheelMode; set => SetField(ref _selectedGalleryWheelMode, value, nameof(SelectedGalleryWheelMode)); }

    public string GalleryWheelModeLabel => Texts.T("settings.galleryWheel");
    public string GalleryWheelModeHint => Texts.T("settings.galleryWheel.hint");

    /// <summary>Rebuilds the localized choices (Zoom / Browse) and selects <paramref name="mode"/>.</summary>
    private void RefreshGalleryWheelModeChoices(string mode)
    {
        GalleryWheelModeChoices = new SettingsChoice[]
        {
            new(Config.GalleryWheelModeZoom, Texts.T("settings.galleryWheel.zoom")),
            new(Config.GalleryWheelModeBrowse, Texts.T("settings.galleryWheel.browse")),
        };
        Raise(new[] { nameof(GalleryWheelModeChoices) });
        SelectedGalleryWheelMode = GalleryWheelModeChoices.FirstOrDefault(item => item.Value == mode) ?? GalleryWheelModeChoices[0];
    }

    /// <summary>The gallery's "⋯" menu changed the setting: the page shows the confirmed value (the draft keeps its other edits).</summary>
    internal void RefreshConfirmedGalleryWheelMode(string mode)
    {
        if (_isClosed) return;
        RefreshGalleryWheelModeChoices(mode);
    }
}
