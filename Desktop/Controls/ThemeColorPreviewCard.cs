using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Theming;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// TH3: the live preview of the colour dialog. A small page (background), a panel on it with text, muted text, an accent button, a chip,
/// the favorite star and an error line, all painted from the <see cref="ThemePalette"/> the draft would give, not from the colours
/// the window shows now, so the Light preview is Light even while the app is Dark. Drawn with the same palette keys as the real
/// controls (Background, Card, CardEdge, Text, Muted, Accent, OnAccent, ChipSurface, ChipBorder, Favorite, Error, ErrorSurface). It takes
/// no input; the accessible name is the title, and the sample lines are read as the text they are.
/// </summary>
internal sealed class ThemeColorPreviewCard : Border
{
    private readonly TextBlock _title = new() { Name = "ThemeColorPreviewTitle" };
    private readonly Border _card = new() { Name = "ThemeColorPreviewPanel", BorderThickness = new Thickness(1), Padding = new Thickness(12) };
    private readonly TextBlock _text = new() { Name = "ThemeColorPreviewText", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _muted = new() { Name = "ThemeColorPreviewMuted", TextWrapping = TextWrapping.Wrap };
    private readonly Border _button = new() { Name = "ThemeColorPreviewButton", Padding = new Thickness(12, 4) };
    private readonly TextBlock _buttonText = new();
    private readonly Border _chip = new() { Name = "ThemeColorPreviewChip", BorderThickness = new Thickness(1), Padding = new Thickness(8, 2) };
    private readonly TextBlock _chipText = new();
    private readonly Path _star = WorkspaceIcons.Create(WorkspaceIcons.StarFilled, inheritForeground: false);
    private readonly TextBlock _starText = new();
    private readonly Border _errorSurface = new() { Name = "ThemeColorPreviewErrorSurface", Padding = new Thickness(8, 4) };
    private readonly TextBlock _error = new() { Name = "ThemeColorPreviewError", TextWrapping = TextWrapping.Wrap };
    private ThemePalette? _palette;
    private string _variantName = string.Empty;

    internal ThemeColorPreviewCard()
    {
        Name = "ThemeColorPreviewCard";
        BorderThickness = new Thickness(1);
        Padding = new Thickness(12);
        Width = ThemeColorsMetrics.PreviewWidth;
        VerticalAlignment = VerticalAlignment.Top;
        this[!CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Group");
        _card[!CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Group");
        _button[!CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Control");
        _chip[!CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Chip");
        _errorSurface[!CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Control");
        _star.Stretch = Stretch.Uniform; _star.Width = 16; _star.Height = 16; _star.VerticalAlignment = VerticalAlignment.Center;
        _button.Child = _buttonText; _chip.Child = _chipText; _errorSurface.Child = _error;
        WrapPanel marks = new() { ItemSpacing = 8, LineSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        marks.Children.Add(_button); marks.Children.Add(_chip);
        marks.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { _star, _starText } });
        _card.Child = new StackPanel { Spacing = 8, Children = { _text, _muted, marks, _errorSurface } };
        Child = new StackPanel { Spacing = 8, Children = { _title, _card } };
        ApplyTexts();
    }

    internal TextBlock Title => _title;
    internal Border PanelBorder => _card;
    internal TextBlock Text => _text;
    internal Border AccentButton => _button;
    internal TextBlock AccentButtonText => _buttonText;
    internal Border ErrorSurface => _errorSurface;
    internal TextBlock ErrorText => _error;
    /// <summary>The palette the card is painted with, or null before the first <see cref="Show"/>.</summary>
    internal ThemePalette? Palette => _palette;

    /// <summary>Paints the card with <paramref name="palette"/>; <paramref name="variantName"/> is the localized name of its variant (Light or Dark).</summary>
    internal void Show(ThemePalette palette, string variantName)
    {
        ArgumentNullException.ThrowIfNull(palette);
        _palette = palette;
        _variantName = variantName;
        IReadOnlyDictionary<string, Color> colors = palette.Colors;
        IBrush Brush(string key) => new SolidColorBrush(colors[key]);
        Background = Brush("Workspace.Background");
        BorderBrush = Brush("Workspace.Border");
        _card.Background = Brush("Workspace.Panel");
        _card.BorderBrush = Brush("Workspace.CardEdge");
        _title.Foreground = Brush("Workspace.Muted");
        _text.Foreground = Brush("Workspace.Text");
        _muted.Foreground = Brush("Workspace.Muted");
        _button.Background = Brush("Workspace.Accent");
        _buttonText.Foreground = Brush("Workspace.OnAccent");
        _chip.Background = Brush("Workspace.ChipSurface");
        _chip.BorderBrush = Brush("Workspace.ChipBorder");
        _chipText.Foreground = Brush("Workspace.Text");
        _star.Stroke = Brush("Workspace.Favorite");
        _star.Fill = Brush("Workspace.Favorite");
        _starText.Foreground = Brush("Workspace.Text");
        _errorSurface.Background = Brush("Workspace.ErrorSurface");
        _error.Foreground = Brush("Workspace.Error");
        ApplyTexts();
    }

    /// <summary>Sets the language-dependent texts; called again after a language change.</summary>
    internal void ApplyTexts()
    {
        _title.Text = Texts.T("theme.colors.preview.title", _variantName);
        AutomationProperties.SetName(this, _title.Text);
        _text.Text = Texts.T("theme.colors.preview.text");
        _muted.Text = Texts.T("theme.colors.preview.muted");
        _buttonText.Text = Texts.T("theme.colors.preview.button");
        _chipText.Text = Texts.T("theme.colors.preview.chip");
        _starText.Text = Texts.T("theme.color.favorite");
        _error.Text = Texts.T("theme.colors.preview.error");
    }
}
