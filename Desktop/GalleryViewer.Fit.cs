using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

internal sealed partial class GalleryViewer
{
    private const string FitWidthCommand = "gallery.fit-width";
    private const string FitHeightCommand = "gallery.fit-height";
    private static readonly (GalleryFitMode Mode, string CommandId)[] FitCommands =
    {
        (GalleryFitMode.Fit, "gallery.fit"),
        (GalleryFitMode.Width, FitWidthCommand),
        (GalleryFitMode.Height, FitHeightCommand)
    };

    private GalleryFitPreferences _fitPreferences = new();
    private double _fitScrollPosition;

    internal event Action<GalleryFitMode>? FitModeChanged;

    /// <summary>Applies the saved image fit mode and enlargement limit.</summary>
    internal void ConfigureFit(GalleryFitPreferences preferences)
    {
        preferences = preferences with { MaxUpscalePercent = GalleryFitPreferences.ClampPercent(preferences.MaxUpscalePercent) };
        if (_fitPreferences == preferences) return;
        bool modeChanged = _fitPreferences.Mode != preferences.Mode;
        _fitPreferences = preferences;
        RefreshFitModeRows();
        if (_info == null) return;
        if (modeChanged)
        {
            _fitScrollPosition = 0;
            Fit(retry: false);
        }
        else FitIfAutomatic();
    }

    private GalleryFitMode EffectiveFitMode => _entry?.Kind == SavedMediaKind.Video ? GalleryFitMode.Fit : _fitPreferences.Mode;
    private double? FitUpscaleCap => _fitPreferences.MaxUpscale(DisplayScale);
    private bool CanSelectImageFitMode => _info != null && _entry?.Kind != SavedMediaKind.Video && !_mediaFailed;

    private void SelectFitMode(GalleryFitMode mode)
    {
        if (CurrentGeometry == null || (mode != GalleryFitMode.Fit && !CanSelectImageFitMode)) return;
        bool changed = CanSelectImageFitMode && _fitPreferences.Mode != mode;
        if (changed) _fitPreferences = _fitPreferences with { Mode = mode };
        _fitScrollPosition = 0;
        Fit(retry: true);
        AnnounceView(zoom: true);
        RefreshFitModeRows();
        if (changed) FitModeChanged?.Invoke(mode);
    }

    private bool RouteFitModeKey(KeyEventArgs e)
    {
        foreach (var (mode, commandId) in FitCommands)
        {
            if (!CommandBindings.Matches(e, commandId)) continue;
            SelectFitMode(mode);
            e.Handled = true;
            return true;
        }
        return false;
    }

    private static string FitModeLabel(GalleryFitMode mode) => mode switch
    {
        GalleryFitMode.Width => CommandCatalog.Label(FitWidthCommand),
        GalleryFitMode.Height => CommandCatalog.Label(FitHeightCommand),
        _ => Texts.T("gallery.fit")
    };

    /// <summary>Preloading uses a viewport-sized buffer to budget the unknown neighbors.</summary>
    internal GalleryDecodeRequest.Fit PreloadFitRequest
    {
        get
        {
            Size available = GetFitViewport();
            return new(available.Width * DisplayScale, available.Height * DisplayScale);
        }
    }

    private Size GetFitViewport()
    {
        Size available = _viewport.Bounds.Size;
        if (available.Width <= 0 || available.Height <= 0)
            available = TopLevel.GetTopLevel(this)?.ClientSize ?? new Size(1280, 720);
        return available;
    }

    private GalleryDecodeRequest.Fit CurrentFitRequest()
    {
        Size available = GetFitViewport();
        double scale = DisplayScale;
        // The decoder also limits Fit buffers to the source size and pixel budget.
        (double width, double height) = GalleryViewGeometry.FitBounds(
            available.Width * scale, available.Height * scale, _fitPreferences.Mode);
        return GalleryRotation.IsQuarterTurn(_rotation) ? new(height, width) : new(width, height);
    }

    private bool RouteFitScrollKey(KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || CurrentGeometry is not { } geometry) return false;
        double pages = e.Key switch
        {
            Key.PageUp => -1,
            Key.PageDown => 1,
            Key.Left when geometry.ScrollsHorizontally => -0.1,
            Key.Right when geometry.ScrollsHorizontally => 0.1,
            Key.Up when !geometry.ScrollsHorizontally => -0.1,
            Key.Down when !geometry.ScrollsHorizontally => 0.1,
            _ => 0
        };
        if (pages == 0 || !ScrollFitImage(geometry, pages)) return false;
        e.Handled = true;
        return true;
    }

    private void MoveOrScrollFit(KeyEventArgs e, string commandId, int direction)
    {
        // The default arrows scroll along the fitted axis when it overflows.
        // A saved navigation shortcut keeps its assigned action.
        bool assigned = ShortcutService.CurrentSection is { IsEditable: true } bindings && bindings.HasOverride(commandId);
        if (assigned || !RouteFitScrollKey(e)) Move(direction);
    }

    private bool ScrollFitImage(GalleryViewGeometry geometry, double pages)
    {
        if (_manualZoom || !CanSelectImageFitMode || !double.IsFinite(pages) || geometry.FitScrollLimit(_zoom) <= 0) return false;
        double length = geometry.ScrollsHorizontally ? geometry.ViewportWidth : geometry.ViewportHeight;
        Adopt(geometry.WithFitScrollPan(Placement, geometry.FitScrollPan(Placement) - pages * length));
        ApplyPan();
        return true;
    }
}
