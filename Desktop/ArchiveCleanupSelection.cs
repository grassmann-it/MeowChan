using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop;

/// <summary>Selection belongs to one frozen preview; a refresh never silently carries it forward.</summary>
internal sealed class ArchiveCleanupSelection
{
    private readonly HashSet<ResourceId> _selected = new();
    private readonly Dictionary<ResourceId, ArchiveCleanupPreviewRow> _rows = new();
    private bool _unique;
    public ArchiveCleanupPreview? Preview { get; private set; }
    public bool Invalidated { get; private set; }
    public bool CanChoose => Preview is { Complete: true, IsStale: false, PolicyVersion: 1 } && !Invalidated
        && _unique;
    public IReadOnlyList<ArchiveCleanupPreviewRow> SelectedRows => Preview?.Rows
        .Where(row => _selected.Contains(row.Id)).ToArray() ?? Array.Empty<ArchiveCleanupPreviewRow>();

    public void Load(ArchiveCleanupPreview preview)
    {
        Preview = preview;
        Invalidated = false;
        _selected.Clear();
        _rows.Clear();
        foreach (ArchiveCleanupPreviewRow row in preview.Rows) _rows.TryAdd(row.Id, row);
        _unique = _rows.Count == preview.Rows.Count;
    }

    public void Invalidate()
    {
        Invalidated = true;
        _selected.Clear();
    }

    public bool CanSelect(ArchiveCleanupPreviewRow row) => CanChoose
        && _rows.TryGetValue(row.Id, out var stored) && ReferenceEquals(stored, row)
        && row.Eligibility == ArchiveCleanupEligibility.Candidate
        && row.Reasons.Count == 0 && row.Media.SavedAssetCount is >= 0 and <= ArchiveCleanupPreview.MaximumSavedAssets
        && row.Media.Error == null;

    public bool IsSelected(ResourceId id) => _selected.Contains(id);

    public bool SetSelected(ResourceId id, bool selected)
    {
        if (!selected) return _selected.Remove(id);
        if (!CanChoose) return false;
        return _rows.TryGetValue(id, out var row) && CanSelect(row) && _selected.Add(id);
    }

    public void Clear() => _selected.Clear();

    public ResourceId[] FreezeSelected()
    {
        ArchiveCleanupPreviewRow[] rows = SelectedRows.ToArray();
        if (rows.Length == 0 || rows.Any(row => !CanSelect(row)))
            throw new InvalidOperationException("A fresh preview and an explicit eligible selection are required.");
        return rows.Select(row => row.Id).ToArray();
    }
}
