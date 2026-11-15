using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Rendering;

namespace MeowChan.Desktop.Controls;

// The hit zones of the badge view (dead-affordance audit, package D). The view stays render-only everywhere unless its owner names the badges that
// act (Activatable): then exactly the places of those badges (a little more than they are drawn, so a 12 dip chip is no pixel hunt) are click
// targets with a hand cursor, and nowhere else the view takes a pointer: the picture and the rest of the tile keep their own clicks. A click
// on a zone is the zone's alone (the tile is not selected and a double click does not open it); the owner decides what it does (Activated).
internal sealed partial class MediaBadgeView : ICustomHitTest
{
    /// <summary>The smallest side of a hit zone in dip: a badge is 12 to 20 dip, a pointer target should not be smaller than this.</summary>
    internal const double MinimumZone = 22;

    private Func<MediaBadgeId, bool>? _activatable;
    private MediaBadgeId? _pressed;

    /// <summary>
    /// Which badges act as buttons (null: none, the view is not a hit target at all). Asked at every pointer move and click, so the owner's
    /// current abilities count (a page that gained the Tagging navigation, a tile that cannot be tagged).
    /// </summary>
    internal Func<MediaBadgeId, bool>? Activatable
    {
        get => _activatable;
        set
        {
            _activatable = value;
            IsHitTestVisible = value != null;
            Cursor = value != null ? PointerCursors.Hand : null;
        }
    }

    /// <summary>A badge that acts was clicked (left button, press and release on the same badge).</summary>
    internal event Action<MediaBadgeId>? Activated;

    private void InitializeHitZones()
    {
        AddHandler(DoubleTappedEvent, (_, e) =>
        {
            if (ZoneAt(e.GetPosition(this)) != null) e.Handled = true; // a double click on a zone is no "open the tile"
        });
    }

    /// <summary>The hit zone of a placed badge: its bounds, at least <see cref="MinimumZone"/> on each side around the same centre, kept inside the view.</summary>
    internal static Rect ZoneOf(Rect badge, Size view)
    {
        double width = Math.Max(badge.Width, MinimumZone), height = Math.Max(badge.Height, MinimumZone);
        Rect zone = new(badge.X + (badge.Width - width) / 2, badge.Y + (badge.Height - height) / 2, width, height);
        return zone.Intersect(new Rect(view));
    }

    /// <summary>The acting badge whose zone holds <paramref name="point"/> (view coordinates), or null. The nearest centre wins where zones overlap.</summary>
    internal MediaBadgeId? ZoneAt(Point point)
    {
        if (_activatable is not { } activatable) return null;
        MediaBadgeId? found = null;
        double best = double.MaxValue;
        Size size = Bounds.Size;
        foreach (PlacedBadge placed in _plan.Badges)
        {
            if (!activatable(placed.Badge.Id) || !ZoneOf(placed.Bounds, size).Contains(point)) continue;
            Point center = placed.Bounds.Center;
            double distance = (center.X - point.X) * (center.X - point.X) + (center.Y - point.Y) * (center.Y - point.Y);
            if (distance >= best) continue;
            best = distance;
            found = placed.Badge.Id;
        }
        return found;
    }

    /// <summary>The hit zones of the acting badges now (tests and smokes), in view coordinates.</summary>
    internal Rect[] Zones()
    {
        if (_activatable is not { } activatable) return [];
        Size size = Bounds.Size;
        return Array.ConvertAll(Array.FindAll(_plan.Badges, placed => activatable(placed.Badge.Id)), placed => ZoneOf(placed.Bounds, size));
    }

    bool ICustomHitTest.HitTest(Point point) => ZoneAt(point) != null;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.Handled && Press(e.GetPosition(this), e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)) e.Handled = true; // the tile under the badge is not selected by it
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Release(e.GetPosition(this), e.InitialPressMouseButton)) e.Handled = true;
    }

    /// <summary>The press of a pointer at <paramref name="at"/> (view coordinates): true when it is a left press on a zone (the caller then handles it).</summary>
    internal bool Press(Point at, bool leftButton)
    {
        _pressed = null;
        if (!leftButton || ZoneAt(at) is not { } id) return false;
        _pressed = id;
        return true;
    }

    /// <summary>The release that ends a press: true when that press was a zone's (handled); a release on the zone it began on is a click (<see cref="Activated"/>).</summary>
    internal bool Release(Point at, MouseButton initialButton)
    {
        MediaBadgeId? pressed = _pressed;
        _pressed = null;
        if (pressed == null || initialButton != MouseButton.Left) return false;
        if (ZoneAt(at) == pressed) Activated?.Invoke(pressed.Value);
        return true;
    }
}
