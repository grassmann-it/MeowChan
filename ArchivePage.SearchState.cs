using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;

namespace MeowChan;

public partial class ArchivePage
{
    // Editor controls are the draft; a load is frozen before the first await, and
    // the applied request changes only together with the complete rendered page.
    private sealed record ListLoad(ArchiveSearchRequest Request, int Offset,
        IReadOnlyDictionary<string, ThreadWorkSnapshot> Work, Action? AfterCommit = null);
    private ArchiveSearchRequest? _appliedSearch;
    private ListLoad? _runningList;
    private IReadOnlyDictionary<string, ThreadWorkSnapshot> _appliedWork = new Dictionary<string, ThreadWorkSnapshot>();
    private ListLoad? _retryList;
    internal bool CanApplySearch => _available && !_busy && !_ended;
    internal bool CanSaveAppliedSearch => CanApplySearch && _appliedSearch != null;
    internal bool IsSearchLoading => _runningList != null;
    internal ArchiveSearchSpec? AppliedSearchSpec => _appliedSearch?.Spec;
    internal event Action? AppliedSearchChanged;
    private IReadOnlyDictionary<string, ThreadWorkSnapshot> FreezeWork() =>
        (GetWorkSnapshots?.Invoke() ?? Array.Empty<ThreadWorkSnapshot>())
        .GroupBy(item => item.Url, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First() with { Media = Array.AsReadOnly(group.First().Media.ToArray()) }, StringComparer.Ordinal);
    private ListLoad NewListLoad(ArchiveSearchRequest request, int offset = 0, Action? afterCommit = null) =>
        new(request, offset, FreezeWork(), afterCommit);

    internal async Task<bool> ApplySearchSpecAsync(ArchiveSearchSpec spec, CancellationToken cancellationToken = default)
    {
        if (_busy || _ended || !_available) return false;
        try
        {
            ArchiveSearchRequest request = ArchiveSearchRequest.Create(spec);
            long revision = _draftRevision;
            // Publish no definition/draft on failure or after the dialog closes.
            return await LoadListAsync(NewListLoad(request, afterCommit: () =>
            {
                if (_draftRevision == revision) WriteSearchDraft(spec);
                _archiveSelectionIds.Clear(); _archiveLeadId = null;
                _history.Clear();
                SynchronizeArchiveSelection(ArchiveVisibleRows);
                ClearArchiveThreadPresentation();
            }), cancellationToken);
        }
        catch (Exception exception) { SearchEditorError(exception.Message); return false; }
    }

    internal static MeowChan.Archive.ArchiveWorkFilter WorkChoice(int index) => index switch
    {
        0 => MeowChan.Archive.ArchiveWorkFilter.All, 1 => MeowChan.Archive.ArchiveWorkFilter.Watching, 2 => MeowChan.Archive.ArchiveWorkFilter.Followup,
        3 => MeowChan.Archive.ArchiveWorkFilter.Intervention, 4 => MeowChan.Archive.ArchiveWorkFilter.Completed, 5 => MeowChan.Archive.ArchiveWorkFilter.Stopped,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
    internal static int WorkIndex(MeowChan.Archive.ArchiveWorkFilter filter) => filter switch
    {
        MeowChan.Archive.ArchiveWorkFilter.All => 0, MeowChan.Archive.ArchiveWorkFilter.Watching => 1, MeowChan.Archive.ArchiveWorkFilter.Followup => 2,
        MeowChan.Archive.ArchiveWorkFilter.Intervention => 3, MeowChan.Archive.ArchiveWorkFilter.Completed => 4, MeowChan.Archive.ArchiveWorkFilter.Stopped => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(filter))
    };

    internal static ArchiveSearchSpec CopySearch(ArchiveSearchSpec spec, string field, string? value = null) =>
        ArchiveSearchSpec.FromExactIdentities(field == "text" ? "" : spec.Text,
            field == "source" ? spec.Sources.Where(item => item != value) : spec.Sources,
            field == "board" ? spec.Boards.Where(item => BoardKey(item) != value) : spec.Boards,
            field == "state" ? spec.SourceStates.Where(item => item.ToString() != value) : spec.SourceStates,
            field == "capture" ? null : spec.CaptureEnabled, field == "posts" ? null : spec.HasPosts,
            spec.Sort, field == "work" ? MeowChan.Archive.ArchiveWorkFilter.All : spec.WorkFilter,
            field == "date" ? null : spec.DateRange);
    private static string BoardKey(ArchiveBoardConstraint board) =>
        (board.Source == null ? "*" : board.Source.Length + ":" + board.Source) + "/" + board.Board;
}
