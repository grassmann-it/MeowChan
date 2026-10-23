using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MeowChan.Desktop;

public sealed record SectionDescriptor(string Id, string Title, Func<Control?> ResolveTarget, Action? Open = null);

/// <summary>Ephemeral navigation only: never owns or rebuilds a form or its draft.</summary>
public sealed class SectionNavigationController : IDisposable
{
    private readonly ScrollViewer _scroll;
    private readonly Func<bool> _available;
    private IReadOnlyList<SectionDescriptor> _sections = Array.Empty<SectionDescriptor>();
    private long _revision;
    private long _request;
    private bool _disposed;
    public event Action? TargetUnavailable;
    public event Action? Navigating;
    public event Action? Navigated;
    public IReadOnlyList<SectionDescriptor> Sections => _sections;
    public bool IsAvailable => !_disposed && _available() && TopLevel.GetTopLevel(_scroll) != null && _scroll.IsEffectivelyVisible;

    public SectionNavigationController(ScrollViewer scroll, Func<bool> available)
    {
        _scroll = scroll;
        _available = available;
        _scroll.DetachedFromVisualTree += OnDetached;
    }

    public void SetSections(IEnumerable<SectionDescriptor> sections)
    {
        _revision++;
        // Ambiguous identities are excluded, never resolved to the first similarly named scope.
        var groups = sections.GroupBy(section => section.Id, StringComparer.Ordinal).ToArray();
        foreach (var duplicate in groups.Where(group => group.Count() != 1))
            System.Diagnostics.Debug.Fail("Duplicate section identity: " + duplicate.Key);
        _sections = groups.Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
    }

    public bool CanNavigate(string id) => IsAvailable && _sections.Any(section => section.Id == id && section.ResolveTarget() is { IsVisible: true });

    public void NavigateTo(string id)
    {
        long revision = _revision, request = ++_request;
        SectionDescriptor? section = _sections.SingleOrDefault(item => item.Id == id);
        Control? target = section?.ResolveTarget();
        if (!IsAvailable || target == null || !target.IsVisible) { TargetUnavailable?.Invoke(); return; }
        Navigating?.Invoke();
        section!.Open?.Invoke();
        foreach (Expander parent in target.GetVisualAncestors().OfType<Expander>()) parent.IsExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || request != _request || revision != _revision) return;
            Control? current = section.ResolveTarget();
            if (!IsAvailable || !ReferenceEquals(current, target) || !target.IsEffectivelyVisible
                || !target.GetVisualAncestors().Contains(_scroll)) { TargetUnavailable?.Invoke(); return; }
            _scroll.UpdateLayout();
            Point? point = target.TranslatePoint(default, _scroll);
            if (point == null) { TargetUnavailable?.Invoke(); return; }
            double maximum = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
            _scroll.Offset = new Vector(_scroll.Offset.X, Math.Clamp(_scroll.Offset.Y + point.Value.Y, 0, maximum));
            target.Focusable = true;
            AutomationProperties.SetName(target, section.Title);
            target.Focus(NavigationMethod.Directional);
            Navigated?.Invoke();
        }, DispatcherPriority.Loaded);
    }

    // Validation/error reveal supersedes an earlier queued intentional jump.
    public void CancelPendingNavigation() => _request++;
    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs args) => CancelPendingNavigation();
    public void Dispose()
    {
        _disposed = true;
        CancelPendingNavigation();
        _sections = Array.Empty<SectionDescriptor>();
        _scroll.DetachedFromVisualTree -= OnDetached;
    }
}
