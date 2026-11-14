using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// Viewer rows of the "Reading and media" page: "Repeat video" (split out of the playback mode), what a running slideshow
/// does with a video (once, at least X seconds, N passes; X and N with default/minimum/maximum) and "Hide controls after"
/// (the idle time of the gallery's overlay controls and the reader's arrow cross). The video values travel in the gallery
/// playback draft (<see cref="GalleryPlaybackPreferences"/>) like the other slideshow choices; the idle time is a plain setting.
/// </summary>
public partial class SettingsEditor
{
    private bool _galleryVideoRepeat;
    private SettingsChoice? _selectedGallerySlideshowVideoRule;
    private decimal? _gallerySlideshowVideoSeconds = Config.GallerySlideshowVideoSecondsDefault;
    private decimal? _gallerySlideshowVideoRepeats = Config.GallerySlideshowVideoRepeatsDefault;
    private decimal? _controlsIdleHideMs = Config.ControlsIdleHideMsDefault;
    private bool _galleryFilmstrip = true;
    private bool _mediaFocusHidesHeader = true;
    private decimal? _galleryFilmstripHeight = Config.GalleryFilmstripHeightDefault;
    private decimal? _galleryFilmstripPreload = Config.GalleryFilmstripPreloadDefault;
    private decimal? _galleryVideoSeekStepSeconds = Config.GalleryVideoSeekStepSecondsDefault;
    private decimal? _mediaBadgeCollapseBelow = Config.MediaBadgeCollapseBelowDefault;
    private decimal? _mediaBadgeHideBelow = Config.MediaBadgeHideBelowDefault;
    private decimal? _mediaBadgeMaxPerCorner = Config.MediaBadgeMaxPerCornerDefault;
    private bool _galleryLayoutResetPending;
    // H1: the filmstrip box as this draft loaded it (start, discard, after a save). A save sets the layout of both modes only when the box
    // differs from it; the gallery may have changed the live value since (L), and an untouched box must not roll that back.
    private bool _initialGalleryFilmstrip = true;

    public IReadOnlyList<SettingsChoice> GallerySlideshowVideoRuleChoices { get; private set; } = Array.Empty<SettingsChoice>();

    public bool GalleryVideoRepeat { get => _galleryVideoRepeat; set => SetField(ref _galleryVideoRepeat, value, nameof(GalleryVideoRepeat)); }

    public SettingsChoice? SelectedGallerySlideshowVideoRule
    {
        get => _selectedGallerySlideshowVideoRule;
        set
        {
            SetField(ref _selectedGallerySlideshowVideoRule, value, nameof(SelectedGallerySlideshowVideoRule));
            // A language refresh replaces the items and may clear the selection for a moment; keep the rows stable.
            if (!_refreshingLanguageLabels) RaiseViewerVisibilityChanged();
        }
    }

    public decimal? GallerySlideshowVideoSeconds { get => _gallerySlideshowVideoSeconds; set => SetField(ref _gallerySlideshowVideoSeconds, value, nameof(GallerySlideshowVideoSeconds)); }
    public decimal? GallerySlideshowVideoRepeats { get => _gallerySlideshowVideoRepeats; set => SetField(ref _gallerySlideshowVideoRepeats, value, nameof(GallerySlideshowVideoRepeats)); }
    public decimal? ControlsIdleHideMs { get => _controlsIdleHideMs; set => SetField(ref _controlsIdleHideMs, value, nameof(ControlsIdleHideMs)); }
    public bool GalleryFilmstrip { get => _galleryFilmstrip; set => SetField(ref _galleryFilmstrip, value, nameof(GalleryFilmstrip)); }
    public bool MediaFocusHidesHeader { get => _mediaFocusHidesHeader; set => SetField(ref _mediaFocusHidesHeader, value, nameof(MediaFocusHidesHeader)); }
    public decimal? GalleryFilmstripHeight { get => _galleryFilmstripHeight; set => SetField(ref _galleryFilmstripHeight, value, nameof(GalleryFilmstripHeight)); }
    public decimal? GalleryFilmstripPreload { get => _galleryFilmstripPreload; set => SetField(ref _galleryFilmstripPreload, value, nameof(GalleryFilmstripPreload)); }
    public decimal? GalleryVideoSeekStepSeconds { get => _galleryVideoSeekStepSeconds; set => SetField(ref _galleryVideoSeekStepSeconds, value, nameof(GalleryVideoSeekStepSeconds)); }
    public decimal? MediaBadgeCollapseBelow { get => _mediaBadgeCollapseBelow; set => SetField(ref _mediaBadgeCollapseBelow, value, nameof(MediaBadgeCollapseBelow)); }
    public decimal? MediaBadgeHideBelow { get => _mediaBadgeHideBelow; set => SetField(ref _mediaBadgeHideBelow, value, nameof(MediaBadgeHideBelow)); }
    public decimal? MediaBadgeMaxPerCorner { get => _mediaBadgeMaxPerCorner; set => SetField(ref _mediaBadgeMaxPerCorner, value, nameof(MediaBadgeMaxPerCorner)); }

    /// <summary>
    /// GL4: "Reset gallery layout" was pressed in this draft: the layout choice of both modes (View and Tag) goes back to the work
    /// view's defaults when the settings are saved. Draft only; a discard or a save clears it.
    /// </summary>
    public bool GalleryLayoutResetPending
    {
        get => _galleryLayoutResetPending;
        set { SetField(ref _galleryLayoutResetPending, value, nameof(GalleryLayoutResetPending)); Raise(new[] { nameof(GalleryLayoutResetHint) }); }
    }

    private void OnGalleryLayoutResetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_isClosed || !IsEditable) return;
        GalleryLayoutResetPending = true;
        GalleryFilmstrip = true; // the layout after the reset: the filmstrip follows the work view
    }

    private SlideshowVideoRule SelectedVideoRule => GalleryPlaybackPreferences.ParseVideoRule(SelectedGallerySlideshowVideoRule?.Value);
    /// <summary>The seconds row is shown (and validated) only for the "at least X seconds" rule of a chosen slideshow.</summary>
    public bool IsGallerySlideshowVideoSeconds => IsGallerySlideshow && SelectedVideoRule == SlideshowVideoRule.LoopAtLeastSeconds;
    /// <summary>The passes row is shown (and validated) only for the "N passes" rule of a chosen slideshow.</summary>
    public bool IsGallerySlideshowVideoRepeats => IsGallerySlideshow && SelectedVideoRule == SlideshowVideoRule.RepeatTimes;

    public string GalleryVideoRepeatLabel => GalleryPlaybackCatalog.Get("viewer.playback.repeat");
    public string GalleryVideoRepeatHint => GalleryPlaybackCatalog.Get("viewer.playback.repeatHelp");
    public string GallerySlideshowVideoRuleLabel => GalleryPlaybackCatalog.Get("viewer.playback.videoRule");
    public string GallerySlideshowVideoRuleHint => GalleryPlaybackCatalog.Get("viewer.playback.videoRuleHelp");
    public string GallerySlideshowVideoSecondsLabel => GalleryPlaybackCatalog.Get("viewer.playback.videoSeconds");
    public string GallerySlideshowVideoSecondsHint => Texts.T("settings.galleryPlayback.videoSecondsHint",
        Config.GallerySlideshowVideoSecondsDefault, Config.GallerySlideshowVideoSecondsMinimum, Config.GallerySlideshowVideoSecondsMaximum);
    public string GallerySlideshowVideoRepeatsLabel => GalleryPlaybackCatalog.Get("viewer.playback.videoRepeats");
    public string GallerySlideshowVideoRepeatsHint => Texts.T("settings.galleryPlayback.videoRepeatsHint",
        Config.GallerySlideshowVideoRepeatsDefault, Config.GallerySlideshowVideoRepeatsMinimum, Config.GallerySlideshowVideoRepeatsMaximum);
    public string ControlsIdleHideMsLabel => Texts.T("settings.controlsIdleHide");
    public string ControlsIdleHideMsHint => Texts.T("settings.controlsIdleHideHint",
        Config.ControlsIdleHideMsDefault, Config.ControlsIdleHideMsMinimum, Config.ControlsIdleHideMsMaximum);
    public string MediaFocusHidesHeaderLabel => Texts.T("settings.mediaFocusHidesHeader");
    public string MediaFocusHidesHeaderHint => Texts.T("settings.mediaFocusHidesHeaderHint");
    public string GalleryFilmstripLabel => Texts.T("settings.galleryFilmstrip");
    // The key of the filmstrip toggle is read from the binding (it may be moved), never written into the text.
    public string GalleryFilmstripHint => Texts.T("settings.galleryFilmstripHint", MeowChan.Desktop.Commands.CommandCatalog.ShortcutDisplay("gallery.filmstrip-toggle") ?? "—");
    public string GalleryFilmstripHeightLabel => Texts.T("settings.galleryFilmstripHeight");
    public string GalleryFilmstripHeightHint => Texts.T("settings.galleryFilmstripHeightHint",
        Config.GalleryFilmstripHeightDefault, Config.GalleryFilmstripHeightMinimum, Config.GalleryFilmstripHeightMaximum);
    public string GalleryLayoutResetLabel => Texts.T("settings.galleryLayoutReset");
    // The caption names what the button does; once pressed it says what happens on save (read out: the text block is a polite live region).
    public string GalleryLayoutResetHint => Texts.T(GalleryLayoutResetPending ? "settings.galleryLayoutResetDone" : "settings.galleryLayoutResetHint");
    public string GalleryFilmstripPreloadLabel => Texts.T("settings.galleryFilmstripPreload");
    public string GalleryFilmstripPreloadHint => Texts.T("settings.galleryFilmstripPreloadHint",
        Config.GalleryFilmstripPreloadDefault, Config.GalleryFilmstripPreloadMinimum, Config.GalleryFilmstripPreloadMaximum);
    public string GalleryVideoSeekStepSecondsLabel => Texts.T("settings.galleryVideoSeekStep");
    public string GalleryVideoSeekStepSecondsHint => Texts.T("settings.galleryVideoSeekStepHint",
        Config.GalleryVideoSeekStepSecondsDefault, Config.GalleryVideoSeekStepSecondsMinimum, Config.GalleryVideoSeekStepSecondsMaximum);
    public string MediaBadgeCollapseBelowLabel => Texts.T("settings.mediaBadgeCollapseBelow");
    public string MediaBadgeCollapseBelowHint => Texts.T("settings.mediaBadgeCollapseBelowHint",
        Config.MediaBadgeCollapseBelowDefault, Config.MediaBadgeCollapseBelowMinimum, Config.MediaBadgeCollapseBelowMaximum);
    public string MediaBadgeHideBelowLabel => Texts.T("settings.mediaBadgeHideBelow");
    public string MediaBadgeHideBelowHint => Texts.T("settings.mediaBadgeHideBelowHint",
        Config.MediaBadgeHideBelowDefault, Config.MediaBadgeHideBelowMinimum, Config.MediaBadgeHideBelowMaximum);
    public string MediaBadgeMaxPerCornerLabel => Texts.T("settings.mediaBadgeMaxPerCorner");
    public string MediaBadgeMaxPerCornerHint => Texts.T("settings.mediaBadgeMaxPerCornerHint",
        Config.MediaBadgeMaxPerCornerDefault, Config.MediaBadgeMaxPerCornerMinimum, Config.MediaBadgeMaxPerCornerMaximum);

    public string GallerySlideshowVideoSecondsHelp => FieldHelp(nameof(GallerySlideshowVideoSecondsInput), Texts.T("settings.unitSeconds") + ". " + GallerySlideshowVideoSecondsHint);
    public string GallerySlideshowVideoRepeatsHelp => FieldHelp(nameof(GallerySlideshowVideoRepeatsInput), Texts.T("settings.unitCount") + ". " + GallerySlideshowVideoRepeatsHint);
    public string ControlsIdleHideMsHelp => FieldHelp(nameof(ControlsIdleHideMsInput), Texts.T("stats.settings.unitMs") + ". " + ControlsIdleHideMsHint);
    public string GalleryFilmstripHeightHelp => FieldHelp(nameof(GalleryFilmstripHeightInput), Texts.T("settings.unitDip") + ". " + GalleryFilmstripHeightHint);
    public string GalleryFilmstripPreloadHelp => FieldHelp(nameof(GalleryFilmstripPreloadInput), Texts.T("settings.unitCount") + ". " + GalleryFilmstripPreloadHint);
    public string GalleryVideoSeekStepSecondsHelp => FieldHelp(nameof(GalleryVideoSeekStepSecondsInput), Texts.T("settings.unitSeconds") + ". " + GalleryVideoSeekStepSecondsHint);
    public string MediaBadgeCollapseBelowHelp => FieldHelp(nameof(MediaBadgeCollapseBelowInput), Texts.T("settings.unitDip") + ". " + MediaBadgeCollapseBelowHint);
    public string MediaBadgeHideBelowHelp => FieldHelp(nameof(MediaBadgeHideBelowInput), Texts.T("settings.unitDip") + ". " + MediaBadgeHideBelowHint);
    public string MediaBadgeMaxPerCornerHelp => FieldHelp(nameof(MediaBadgeMaxPerCornerInput), Texts.T("settings.unitCount") + ". " + MediaBadgeMaxPerCornerHint);

    private static readonly string[] ViewerHelpProperties =
    {
        nameof(GallerySlideshowVideoSecondsHelp), nameof(GallerySlideshowVideoRepeatsHelp), nameof(ControlsIdleHideMsHelp),
        nameof(GalleryFilmstripHeightHelp), nameof(GalleryFilmstripPreloadHelp), nameof(GalleryVideoSeekStepSecondsHelp),
        nameof(MediaBadgeCollapseBelowHelp), nameof(MediaBadgeHideBelowHelp), nameof(MediaBadgeMaxPerCornerHelp)
    };

    private static readonly string[] ViewerTextProperties =
    {
        nameof(GalleryVideoRepeatLabel), nameof(GalleryVideoRepeatHint), nameof(GallerySlideshowVideoRuleLabel), nameof(GallerySlideshowVideoRuleHint),
        nameof(GallerySlideshowVideoSecondsLabel), nameof(GallerySlideshowVideoSecondsHint), nameof(GallerySlideshowVideoRepeatsLabel),
        nameof(GallerySlideshowVideoRepeatsHint), nameof(ControlsIdleHideMsLabel), nameof(ControlsIdleHideMsHint),
        nameof(GalleryFilmstripLabel), nameof(GalleryFilmstripHint), nameof(GalleryFilmstripHeightLabel), nameof(GalleryFilmstripHeightHint),
        nameof(GalleryFilmstripPreloadLabel), nameof(GalleryFilmstripPreloadHint), nameof(MediaFocusHidesHeaderLabel), nameof(MediaFocusHidesHeaderHint),
        nameof(GalleryLayoutResetLabel), nameof(GalleryLayoutResetHint), nameof(GalleryVideoSeekStepSecondsLabel), nameof(GalleryVideoSeekStepSecondsHint),
        nameof(MediaBadgeCollapseBelowLabel), nameof(MediaBadgeCollapseBelowHint), nameof(MediaBadgeHideBelowLabel), nameof(MediaBadgeHideBelowHint),
        nameof(MediaBadgeMaxPerCornerLabel), nameof(MediaBadgeMaxPerCornerHint)
    };

    private void RaiseViewerVisibilityChanged() => Raise(new[] { nameof(IsGallerySlideshowVideoSeconds), nameof(IsGallerySlideshowVideoRepeats) });

    /// <summary>A language change refreshes text only; it is not a change of any value.</summary>
    private void RaiseViewerTextsChanged() => Raise(ViewerTextProperties.Concat(ViewerHelpProperties));

    private void NotifyViewerHelpChanged() => Raise(ViewerHelpProperties);

    /// <summary>Rebuilds the localized rule choices and restores the semantic selection (called with the other choices).</summary>
    private void RefreshViewerChoices()
    {
        string rule = SelectedGallerySlideshowVideoRule?.Value ?? _initialGalleryPlayback?.VideoRule.ToString() ?? nameof(SlideshowVideoRule.PlayOnce);
        GallerySlideshowVideoRuleChoices = new SettingsChoice[]
        {
            new(nameof(SlideshowVideoRule.PlayOnce), GalleryPlaybackCatalog.Get("viewer.playback.videoRule.once")),
            new(nameof(SlideshowVideoRule.LoopAtLeastSeconds), GalleryPlaybackCatalog.Get("viewer.playback.videoRule.atLeast")),
            new(nameof(SlideshowVideoRule.RepeatTimes), GalleryPlaybackCatalog.Get("viewer.playback.videoRule.repeat")),
        };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GallerySlideshowVideoRuleChoices)));
        SelectedGallerySlideshowVideoRule = GallerySlideshowVideoRuleChoices.FirstOrDefault(item => item.Value == rule) ?? GallerySlideshowVideoRuleChoices[0];
        if (_refreshingLanguageLabels) RaiseViewerVisibilityChanged();
    }

    /// <summary>The video choices from the playback baseline (start, discard, after a save).</summary>
    private void LoadGalleryVideoDraft(GalleryPlaybackPreferences baseline)
    {
        GalleryVideoRepeat = baseline.VideoRepeat;
        string rule = baseline.VideoRule.ToString();
        SelectedGallerySlideshowVideoRule = GallerySlideshowVideoRuleChoices.FirstOrDefault(item => item.Value == rule) ?? new(rule, rule);
        GallerySlideshowVideoSeconds = baseline.VideoSeconds;
        GallerySlideshowVideoRepeats = baseline.VideoRepeats;
        ResetNumber(GallerySlideshowVideoSecondsInput, baseline.VideoSeconds);
        ResetNumber(GallerySlideshowVideoRepeatsInput, baseline.VideoRepeats);
        RaiseViewerVisibilityChanged();
    }

    /// <summary>The idle time from <paramref name="config"/> (start, discard, after a save). <paramref name="layoutReset"/>: the section reset also resets the gallery layout.</summary>
    private void LoadViewerDraft(Config config, bool layoutReset = false)
    {
        GalleryLayoutResetPending = layoutReset;
        ControlsIdleHideMs = config.ControlsIdleHideMs;
        ResetNumber(ControlsIdleHideMsInput, config.ControlsIdleHideMs);
        GalleryFilmstrip = config.GalleryFilmstrip;
        if (!layoutReset) _initialGalleryFilmstrip = config.GalleryFilmstrip; // the section reset is an edit of this draft, not a new load
        MediaFocusHidesHeader = config.MediaFocusHidesHeader;
        GalleryFilmstripHeight = config.GalleryFilmstripHeight;
        GalleryFilmstripPreload = config.GalleryFilmstripPreload;
        ResetNumber(GalleryFilmstripHeightInput, config.GalleryFilmstripHeight);
        ResetNumber(GalleryFilmstripPreloadInput, config.GalleryFilmstripPreload);
        GalleryVideoSeekStepSeconds = config.GalleryVideoSeekStepSeconds;
        ResetNumber(GalleryVideoSeekStepSecondsInput, config.GalleryVideoSeekStepSeconds);
        MediaBadgeCollapseBelow = config.MediaBadgeCollapseBelow;
        ResetNumber(MediaBadgeCollapseBelowInput, config.MediaBadgeCollapseBelow);
        MediaBadgeHideBelow = config.MediaBadgeHideBelow;
        ResetNumber(MediaBadgeHideBelowInput, config.MediaBadgeHideBelow);
        MediaBadgeMaxPerCorner = config.MediaBadgeMaxPerCorner;
        ResetNumber(MediaBadgeMaxPerCornerInput, config.MediaBadgeMaxPerCorner);
    }

    private void ApplyViewerDraft(Config candidate)
    {
        candidate.ControlsIdleHideMs = GetInteger(ControlsIdleHideMs, ControlsIdleHideMsLabel, Config.ControlsIdleHideMsMinimum, Config.ControlsIdleHideMsMaximum);
        candidate.GalleryFilmstrip = GalleryFilmstrip;
        candidate.SettingsBaselineGalleryFilmstrip = _initialGalleryFilmstrip;
        candidate.MediaFocusHidesHeader = MediaFocusHidesHeader;
        candidate.GalleryLayoutResetRequested = GalleryLayoutResetPending;
        candidate.GalleryFilmstripHeight = GetInteger(GalleryFilmstripHeight, GalleryFilmstripHeightLabel, Config.GalleryFilmstripHeightMinimum, Config.GalleryFilmstripHeightMaximum);
        candidate.GalleryFilmstripPreload = GetInteger(GalleryFilmstripPreload, GalleryFilmstripPreloadLabel, Config.GalleryFilmstripPreloadMinimum, Config.GalleryFilmstripPreloadMaximum);
        candidate.GalleryVideoSeekStepSeconds = GetInteger(GalleryVideoSeekStepSeconds, GalleryVideoSeekStepSecondsLabel, Config.GalleryVideoSeekStepSecondsMinimum, Config.GalleryVideoSeekStepSecondsMaximum);
        candidate.MediaBadgeCollapseBelow = GetInteger(MediaBadgeCollapseBelow, MediaBadgeCollapseBelowLabel, Config.MediaBadgeCollapseBelowMinimum, Config.MediaBadgeCollapseBelowMaximum);
        candidate.MediaBadgeHideBelow = GetInteger(MediaBadgeHideBelow, MediaBadgeHideBelowLabel, Config.MediaBadgeHideBelowMinimum, Config.MediaBadgeHideBelowMaximum);
        candidate.MediaBadgeMaxPerCorner = GetInteger(MediaBadgeMaxPerCorner, MediaBadgeMaxPerCornerLabel, Config.MediaBadgeMaxPerCornerMinimum, Config.MediaBadgeMaxPerCornerMaximum);
    }

    /// <summary>A hidden row keeps its draft when that is valid and the saved value otherwise; it never blocks a save.</summary>
    private static int ViewerNumber(decimal? draft, int minimum, int maximum, int saved) =>
        draft.HasValue && draft >= minimum && draft <= maximum && decimal.Truncate(draft.Value) == draft.Value ? decimal.ToInt32(draft.Value) : saved;

    private (bool Repeat, SlideshowVideoRule Rule, int Seconds, int Repeats) CaptureGalleryVideoDraft() => (GalleryVideoRepeat, SelectedVideoRule,
        ViewerNumber(GallerySlideshowVideoSeconds, Config.GallerySlideshowVideoSecondsMinimum, Config.GallerySlideshowVideoSecondsMaximum, _initialGalleryPlayback.VideoSeconds),
        ViewerNumber(GallerySlideshowVideoRepeats, Config.GallerySlideshowVideoRepeatsMinimum, Config.GallerySlideshowVideoRepeatsMaximum, _initialGalleryPlayback.VideoRepeats));

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> ViewerNumericFields()
    {
        yield return (GallerySlideshowVideoSecondsInput, GallerySlideshowVideoSeconds, GallerySlideshowVideoSecondsLabel, Texts.T("settings.unitSeconds"),
            Config.GallerySlideshowVideoSecondsMinimum, Config.GallerySlideshowVideoSecondsMaximum, true);
        yield return (GallerySlideshowVideoRepeatsInput, GallerySlideshowVideoRepeats, GallerySlideshowVideoRepeatsLabel, Texts.T("settings.unitCount"),
            Config.GallerySlideshowVideoRepeatsMinimum, Config.GallerySlideshowVideoRepeatsMaximum, true);
        yield return (ControlsIdleHideMsInput, ControlsIdleHideMs, ControlsIdleHideMsLabel, Texts.T("stats.settings.unitMs"),
            Config.ControlsIdleHideMsMinimum, Config.ControlsIdleHideMsMaximum, true);
        yield return (GalleryFilmstripHeightInput, GalleryFilmstripHeight, GalleryFilmstripHeightLabel, Texts.T("settings.unitDip"),
            Config.GalleryFilmstripHeightMinimum, Config.GalleryFilmstripHeightMaximum, true);
        yield return (GalleryFilmstripPreloadInput, GalleryFilmstripPreload, GalleryFilmstripPreloadLabel, Texts.T("settings.unitCount"),
            Config.GalleryFilmstripPreloadMinimum, Config.GalleryFilmstripPreloadMaximum, true);
        yield return (GalleryVideoSeekStepSecondsInput, GalleryVideoSeekStepSeconds, GalleryVideoSeekStepSecondsLabel, Texts.T("settings.unitSeconds"),
            Config.GalleryVideoSeekStepSecondsMinimum, Config.GalleryVideoSeekStepSecondsMaximum, true);
        yield return (MediaBadgeCollapseBelowInput, MediaBadgeCollapseBelow, MediaBadgeCollapseBelowLabel, Texts.T("settings.unitDip"),
            Config.MediaBadgeCollapseBelowMinimum, Config.MediaBadgeCollapseBelowMaximum, true);
        yield return (MediaBadgeHideBelowInput, MediaBadgeHideBelow, MediaBadgeHideBelowLabel, Texts.T("settings.unitDip"),
            Config.MediaBadgeHideBelowMinimum, Config.MediaBadgeHideBelowMaximum, true);
        yield return (MediaBadgeMaxPerCornerInput, MediaBadgeMaxPerCorner, MediaBadgeMaxPerCornerLabel, Texts.T("settings.unitCount"),
            Config.MediaBadgeMaxPerCornerMinimum, Config.MediaBadgeMaxPerCornerMaximum, true);
    }
}
