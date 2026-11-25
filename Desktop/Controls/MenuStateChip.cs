using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MeowChan.Desktop.Theming;

namespace MeowChan.Desktop.Controls;

/// <summary>The leading cell shared by a menu's icon and its checked state. The row owns the input and automation.</summary>
internal sealed class MenuStateChip : TemplatedControl
{
    public static readonly StyledProperty<object?> ContentProperty = ContentControl.ContentProperty.AddOwner<MenuStateChip>();
    public static readonly StyledProperty<MenuItemToggleType> ToggleTypeProperty =
        AvaloniaProperty.Register<MenuStateChip, MenuItemToggleType>(nameof(ToggleType));
    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<MenuStateChip, bool>(nameof(IsChecked));
    public static readonly StyledProperty<bool> ShowRadioDotProperty =
        AvaloniaProperty.Register<MenuStateChip, bool>(nameof(ShowRadioDot));

    private static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<MenuStateChip, IBrush?>("Accent");
    private static readonly StyledProperty<IBrush?> AccentSurfaceProperty = AvaloniaProperty.Register<MenuStateChip, IBrush?>("AccentSurface");
    private static readonly StyledProperty<IBrush?> MenuSurfaceProperty = AvaloniaProperty.Register<MenuStateChip, IBrush?>("MenuSurface");
    private static readonly StyledProperty<IBrush?> TextBrushProperty = AvaloniaProperty.Register<MenuStateChip, IBrush?>("TextBrush");
    private static readonly StyledProperty<IBrush?> MutedProperty = AvaloniaProperty.Register<MenuStateChip, IBrush?>("Muted");
    private static readonly StyledProperty<IBrush?> ContrastBorderProperty = AvaloniaProperty.Register<MenuStateChip, IBrush?>("ContrastBorder");
    private IDisposable? _maskTint;

    public MenuStateChip()
    {
        Width = Height = MenuLayout.ChipColumn;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        IsVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        this[!AccentProperty] = new DynamicResourceExtension("Workspace.Accent");
        this[!AccentSurfaceProperty] = new DynamicResourceExtension("Workspace.AccentSurface");
        this[!MenuSurfaceProperty] = new DynamicResourceExtension("Workspace.Overlay");
        this[!TextBrushProperty] = new DynamicResourceExtension("Workspace.Text");
        this[!MutedProperty] = new DynamicResourceExtension("Workspace.Muted");
        this[!ContrastBorderProperty] = new DynamicResourceExtension("Workspace.ControlBorder");
    }

    public object? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }
    public MenuItemToggleType ToggleType { get => GetValue(ToggleTypeProperty); set => SetValue(ToggleTypeProperty, value); }
    public bool IsChecked { get => GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }
    public bool ShowRadioDot => GetValue(ShowRadioDotProperty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty)
        {
            _maskTint?.Dispose();
            _maskTint = Content is Border mask && MenuIcons.KeyOf(mask) != null
                ? mask.Bind(Border.BackgroundProperty, this.GetObservable(ForegroundProperty), BindingPriority.Animation)
                : null;
        }
        if (change.Property == ContentProperty || change.Property == ToggleTypeProperty || change.Property == IsCheckedProperty
            || change.Property == IsEffectivelyEnabledProperty || change.Property == AccentProperty || change.Property == AccentSurfaceProperty
            || change.Property == MenuSurfaceProperty || change.Property == TextBrushProperty || change.Property == MutedProperty
            || change.Property == ContrastBorderProperty)
            UpdateAppearance();
    }

    private void UpdateAppearance()
    {
        bool isToggle = ToggleType != MenuItemToggleType.None;
        bool hasIcon = Content != null;
        bool active = isToggle && IsChecked;
        IsVisible = hasIcon || isToggle;
        PseudoClasses.Set(":hasicon", hasIcon);
        SetValue(ShowRadioDotProperty, active && ToggleType == MenuItemToggleType.Radio);

        IBrush? muted = GetValue(MutedProperty);
        IBrush? text = GetValue(TextBrushProperty);
        IBrush? accent = GetValue(AccentProperty);
        IBrush? tint = GetValue(AccentSurfaceProperty);
        if (active && IsEffectivelyEnabled)
        {
            IBrush? surface = GetValue(MenuSurfaceProperty);
            IBrush? outline = GetValue(ContrastBorderProperty);
            Background = tint;
            BorderBrush = MeetsContrast(accent, surface, ContrastGate.GraphicMinimum) ? accent
                : MeetsContrast(outline, surface, ContrastGate.GraphicMinimum) ? outline : text;
            Foreground = MeetsContrast(accent, tint, ContrastGate.TextMinimum) ? accent : text;
        }
        else
        {
            Background = active && muted is ISolidColorBrush solid ? new ImmutableSolidColorBrush(solid.Color, 0.16) : null;
            BorderBrush = active || (isToggle && !hasIcon) ? muted : null;
            Foreground = IsEffectivelyEnabled ? text : muted;
        }
    }

    private static bool MeetsContrast(IBrush? foreground, IBrush? background, double minimum) =>
        foreground is ISolidColorBrush ink && background is ISolidColorBrush surface
        && ContrastRules.Contrast(ThemeColorMath.Format(ink.Color), ThemeColorMath.Format(surface.Color)) >= minimum;
}
