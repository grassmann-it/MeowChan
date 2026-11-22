using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruGalleryPanel
{
    private readonly List<Visual> _visibilityAncestors = new();

    private bool CanShowSample => _visibilityAncestors.Count > 0 && IsEffectivelyVisible;

    private void ObserveVisibility()
    {
        if (_disposed) return;
        foreach (Visual ancestor in this.GetSelfAndVisualAncestors())
        {
            _visibilityAncestors.Add(ancestor);
            ancestor.PropertyChanged += OnAncestorVisibilityChanged;
        }
        ResumeWhenVisible();
    }

    private void StopObservingVisibility()
    {
        foreach (Visual ancestor in _visibilityAncestors) ancestor.PropertyChanged -= OnAncestorVisibilityChanged;
        _visibilityAncestors.Clear();
        PauseSample();
    }

    private void OnAncestorVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != IsVisibleProperty) return;
        if (change.GetNewValue<bool>()) ResumeWhenVisible();
        else PauseSample();
    }

    private void ResumeWhenVisible()
    {
        // Tab content can attach before the search model has restored that tab's snapshot.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && CanShowSample && _rows.FirstOrDefault(row => row.Post.PostId == _currentId) is { } row)
            {
                StartSampleIfVisible(row);
                Render(row);
            }
        }, DispatcherPriority.Loaded);
    }
}
