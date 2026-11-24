using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MeowChan.Desktop.Controls;

/// <summary>Tile presentation over a host-owned preview, badge state and actions.</summary>
internal sealed class MediaTileView : Grid
{
    private const double CornerInset = 2;
    private const double CornerGap = 2;
    internal const string QuietFavoriteClass = "tile-favorite-quiet";

    private readonly LetterboxPanel _preview = new() { MarksOnSlot = true };
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Border _selectionCheck;
    private IDisposable? _selection;

    protected override Type StyleKeyOverride => typeof(Grid);

    internal MediaTileView(Control placeholder, ToggleButton favorite, MediaBadgeView badges, string selectionName,
        bool favoriteAtTopLeft = false)
    {
        Badges = badges;
        _selectionCheck = WorkspaceChips.CreateSelectionCheck(selectionName);
        _selectionCheck.HorizontalAlignment = HorizontalAlignment.Right;
        UpdateSelectionInset();
        Badges.PlanChanged += UpdateSelectionInset;
        favorite.Classes.Add(QuietFavoriteClass);
        favorite.HorizontalAlignment = favoriteAtTopLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        favorite.VerticalAlignment = favoriteAtTopLeft ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        favorite.Margin = favoriteAtTopLeft ? new Thickness(CornerInset, CornerInset, 0, 0) : new Thickness(0, 0, CornerInset, CornerInset);

        _preview.Children.Add(_image);
        _preview.Children.Add(placeholder);
        _preview.Children.Add(Badges);
        _preview.Children.Add(_selectionCheck);
        _preview.Children.Add(favorite);
        Children.Add(_preview);
        SetRow(Title, 1);
        Children.Add(Title);
    }

    internal TextBlock Title { get; } = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
    internal MediaBadgeView Badges { get; }
    internal Rect PictureBounds => _preview.PictureBounds;

    // The host keeps the lease, including when the visual is detached or reused.
    internal Bitmap? Thumbnail
    {
        set
        {
            _image.Source = value;
            _preview.Aspect = MediaSlotGeometry.AspectOf(value);
        }
    }

    internal void SetPreviewSize(double width, double height)
    {
        _preview.MaxWidth = _image.MaxWidth = width;
        _preview.Height = _image.MaxHeight = height;
        RowDefinitions[0].Height = new GridLength(height);
    }

    internal void ShowBadges(in MediaBadgeState state) => Badges.Apply(state, _preview.PictureBounds.Size, _preview.Bounds.Size);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _selection = WorkspaceChips.ObserveSelection(this, _selectionCheck);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _selection?.Dispose();
        _selection = null;
        WorkspaceChips.ShowSelection(_selectionCheck, false);
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateSelectionInset()
    {
        double reach = 0;
        foreach (PlacedBadge placed in Badges.Plan.Badges)
            if (placed.Badge.Corner == MediaBadgeCorner.TopRight)
                reach = Math.Max(reach, Badges.Slot.Width - placed.Bounds.Left);
        double inset = CornerInset + (reach > 0 ? reach + CornerGap : 0);
        if (_selectionCheck.Margin.Right != inset) _selectionCheck.Margin = new Thickness(0, CornerInset, inset, 0);
    }
}
