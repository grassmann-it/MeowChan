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
        string? resourceKey = visible switch
        {
            Config.GalleryBackgroundWhite => "Workspace.GalleryBackground.White",
            Config.GalleryBackgroundCheckerboard => "Workspace.GalleryBackground.Checkerboard",
            _ => null
        };
        _viewport.Background = resourceKey is not null
            && Application.Current?.TryGetResource(resourceKey, null, out object? resource) == true
            && resource is IBrush backdrop
            ? backdrop
            : LetterboxBrush;
    }
}
