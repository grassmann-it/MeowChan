using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MeowChan.Gallery;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal sealed partial class GalleryViewer
{
    private const string BackgroundCommandId = "gallery.background";
    private const string FlipCommandId = "gallery.flip-horizontal";

    private readonly ComboBox _backgroundPicker = new();
    private readonly TextBlock _backgroundLabel = new();
    private readonly Border _themeBackdrop = new()
    {
        IsHitTestVisible = false,
        IsVisible = false,
        [!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Background")
    };
    private readonly ToggleButton _flipHorizontal = new();
    private string _galleryBackground = Config.GalleryBackgroundBlack;

    internal event Action<string>? BackgroundChanged;

    internal void ConfigureBackground(string? mode)
    {
        _galleryBackground = Config.NormalizeGalleryBackground(mode);
        _backgroundPicker.SelectedIndex = BackgroundIndex();
        UpdatePictureBackground();
    }

    private void InitializeAppearanceMenu()
    {
        AddOverflowItem(_flipHorizontal, FlipCommandId, check: true, keepOpen: true,
            label: () => CommandCatalog.Label(FlipCommandId));
        _flipHorizontal.Click += (_, _) => FlipView();

        string? icon = CommandCatalog.Get(BackgroundCommandId).Icon;
        Grid row = MenuLayout.CreateFlyoutSetting(_backgroundPicker, _backgroundLabel, icon);
        _overflowPanel.Children.Add(row);
        AutomationProperties.SetAutomationId(_backgroundPicker, BackgroundCommandId);
        AutomationProperties.SetLabeledBy(_backgroundPicker, _backgroundLabel);
        _backgroundPicker.SelectionChanged += (_, _) =>
        {
            if (_backgroundPicker.SelectedIndex >= 0) ChangeBackground(_backgroundPicker.SelectedIndex);
        };
        _backgroundPicker.DropDownClosed += (_, _) => RememberPlaybackMenuClose(_backgroundPicker);
        RefreshAppearanceMenuText();
    }

    private void RefreshAppearanceMenuText()
    {
        _backgroundLabel.Text = CommandCatalog.Label(BackgroundCommandId);
        AutomationProperties.SetName(_backgroundPicker, _backgroundLabel.Text);
        SetHelp(_backgroundPicker, BackgroundCommandId, "gallery.background.help");
        SetHelp(_flipHorizontal, FlipCommandId, "gallery.flipHorizontal.help");
        _backgroundPicker.ItemsSource = Config.GalleryBackgroundModes
            .Select(value => Texts.T("gallery.background." + value.ToLowerInvariant())).ToArray();
        _backgroundPicker.SelectedIndex = BackgroundIndex();
    }

    private static void SetHelp(Control control, string commandId, string helpKey)
    {
        string tip = CommandBindings.Tooltip(commandId) + "\n" + Texts.T(helpKey);
        ToolTip.SetTip(control, tip);
        AutomationProperties.SetHelpText(control, tip);
    }

    private int BackgroundIndex() => Config.GalleryBackgroundModes
        .TakeWhile(mode => mode != _galleryBackground).Count();

    private bool CanChooseBackground => !_mediaFailed && _entry?.Kind is SavedMediaKind.Image or SavedMediaKind.Unknown;

    private void ChangeBackground(int index)
    {
        string mode = Config.GalleryBackgroundModes[index];
        if (!CanChooseBackground || mode == _galleryBackground) return;
        ConfigureBackground(mode);
        BackgroundChanged?.Invoke(mode);
    }

    private void CycleBackground() => ChangeBackground((BackgroundIndex() + 1) % Config.GalleryBackgroundModes.Count);

    private void UpdatePictureBackground()
    {
        _backgroundPicker.IsEnabled = CanChooseBackground;
        string visible = CanChooseBackground ? _galleryBackground : Config.GalleryBackgroundBlack;
        _themeBackdrop.IsVisible = visible == Config.GalleryBackgroundTheme;
        _viewport.Background = visible switch
        {
            Config.GalleryBackgroundWhite => Brushes.White,
            Config.GalleryBackgroundCheckerboard => CheckerboardBrush,
            _ => Brushes.Black
        };
    }

    private static readonly DrawingBrush CheckerboardBrush = CreateCheckerboardBrush();

    private static DrawingBrush CreateCheckerboardBrush()
    {
        const double square = 8;
        Rect tile = new(0, 0, 2 * square, 2 * square);
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing { Brush = Brushes.LightGray, Geometry = new RectangleGeometry(tile) });
        drawing.Children.Add(new GeometryDrawing { Brush = Brushes.Gray, Geometry = new RectangleGeometry(new Rect(0, 0, square, square)) });
        drawing.Children.Add(new GeometryDrawing { Brush = Brushes.Gray, Geometry = new RectangleGeometry(new Rect(square, square, square, square)) });
        return new DrawingBrush
        {
            Drawing = drawing,
            SourceRect = new RelativeRect(tile, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(tile, RelativeUnit.Absolute),
            TileMode = TileMode.Tile
        };
    }
}
