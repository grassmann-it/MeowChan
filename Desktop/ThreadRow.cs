using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.Desktop;

/// <summary>Passive binding state. Its owner applies snapshots on the desktop dispatcher.</summary>
public sealed class ThreadRow : INotifyPropertyChanged, IDisposable
{
    private bool _disposed;
    private bool _isPaused;
    private bool _presentedPaused;
    private string? _presentationLanguage;
    // Initialize replaces this immutable list even when reloading the same language.
    private IReadOnlyList<LanguageInfo>? _presentationLanguages;

    public long WatchId { get; }
    public ThreadSnapshot Snapshot { get; private set; }
    public string Url => Snapshot.Url;
    public string DownloadDirectory => Snapshot.DownloadDirectory;
    public int KnownMedia => Snapshot.KnownMedia;
    public int CompletedMedia => Snapshot.Work?.SavedMedia ?? Snapshot.CompletedMedia;
    public int SelectedMedia => Snapshot.Work?.Media.Count ?? Snapshot.SelectedMedia;
    public int ExcludedMedia => Snapshot.Content?.ExcludedMedia ?? 0;
    public int MissingMedia => Snapshot.Work?.MissingMedia ?? Math.Max(0, SelectedMedia - CompletedMedia);
    public int MediaErrorCount => Snapshot.Work?.Media.Count(item => item.Status is MeowChan.Persistence.MediaWorkStatus.NeedsIntervention or MeowChan.Persistence.MediaWorkStatus.Expired)
        ?? Snapshot.MediaFailures.Count;
    public string MissingMediaText => Texts.T("work.missingAndErrors", MissingMedia, MediaErrorCount);
    public DateTimeOffset? EarliestRetry => Snapshot.Work?.NextDueAt;
    public string EarliestRetryText => EarliestRetry?.ToLocalTime().ToString("g", Texts.Culture) ?? "—";
    public string ExceptionHint { get; private set; } = string.Empty;
    public bool HasExceptionHint => ExceptionHint.Length > 0;
    public ThreadActivity Activity => Snapshot.Activity;
    public ThreadSourceState SourceState => Snapshot.SourceState;
    public string HostName { get; private set; } = string.Empty;
    public string BoardName { get; private set; } = string.Empty;
    public string BoardLabel => "/" + Snapshot.Id.Board + "/";
    public string SourceBoard => HostName + " " + BoardLabel;
    public string ThreadId { get; private set; } = string.Empty;
    public long? ThreadNumber { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    // Row-only projection: never use this in the authoritative detail/status column.
    public string CompactStatus { get; private set; } = string.Empty;
    public bool IsHighPriority { get; private set; }
    public string PriorityBadge => IsHighPriority ? "↑" : string.Empty;
    public string PriorityDescription => IsHighPriority
        ? (Texts.Language == "de" ? "Hohe Priorität · nächste ausführbare Dateien; laufende Downloads bleiben unverändert."
            : "High priority · next runnable files; active downloads are unchanged.") : string.Empty;

    // Authoritative host projection only; do not optimistically change on click.
    internal void ApplyUserPriority(bool high)
    {
        if (IsHighPriority == high) return;
        IsHighPriority = high;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighPriority)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PriorityBadge)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PriorityDescription)));
    }

    public string CompactDetails => string.Join(Environment.NewLine,
        new[] { CompactStatus, SavedProgress, ErrorDetails }.Where(value => !string.IsNullOrEmpty(value)));
    public string Progress { get; private set; } = string.Empty;
    public string SavedProgress => Texts.T("work.saved", CompletedMedia, SelectedMedia);
    public string IssueDetails { get; private set; } = string.Empty;
    public string ContextDetails { get; private set; } = string.Empty;
    public string ErrorDetails { get; private set; } = string.Empty;
    public bool CanRetryMedia => Snapshot.Work != null && WorkStatusFormatter.Format(Snapshot.Work, _isPaused).CanRetry;
    public string StatusDetails => string.IsNullOrEmpty(ErrorDetails) ? Status : Status + Environment.NewLine + ErrorDetails;
    public double Completion { get; private set; }
    public int Queued { get; private set; }
    public int Active { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ThreadRow(DesktopThreadEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        WatchId = entry.WatchId;
        Snapshot = entry.Snapshot;
        UpdateSnapshot(entry);
    }

    public void UpdateSnapshot(DesktopThreadEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_disposed)
        {
            return;
        }

        if (entry.WatchId != WatchId)
            throw new ArgumentException("A row cannot be rebound to another watch lifetime.", nameof(entry));
        ThreadSnapshot snapshot = entry.Snapshot;
        bool unchanged = _presentationLanguage == Texts.Language
            && ReferenceEquals(_presentationLanguages, Texts.Languages)
            && _presentedPaused == _isPaused
            && HostName == entry.HostName && BoardName == entry.BoardName
            && SameSnapshot(Snapshot, snapshot);
        // Keep the latest immutable capture even when its presentation is unchanged.
        Snapshot = snapshot;
        if (unchanged) return;
        _presentationLanguage = Texts.Language;
        _presentationLanguages = Texts.Languages;
        _presentedPaused = _isPaused;
        HostName = entry.HostName;
        BoardName = entry.BoardName;
        ThreadId = snapshot.Id.Thread ?? string.Empty;
        ThreadNumber = long.TryParse(ThreadId, NumberStyles.None, CultureInfo.InvariantCulture, out long number) ? number : null;
        Title = snapshot.Title;
        string activity = snapshot.Activity switch
        {
            ThreadActivity.Pending => Texts.T("status.pending"),
            ThreadActivity.Checking => Texts.T("status.checking"),
            ThreadActivity.Downloading => Texts.T("status.downloading"),
            ThreadActivity.Finished => Texts.T("status.finished"),
            ThreadActivity.NoMedia => Texts.T("status.noMedia"),
            ThreadActivity.Error => Texts.T("status.error"),
            ThreadActivity.NotFound => Texts.T("status.notFound"),
            ThreadActivity.Stopped => Texts.T("status.stopped"),
            _ => Texts.T("status.unknown")
        };
        string? request = RequestStatus.Format(snapshot.RequestProgress);
        if (request != null && request != activity)
        {
            activity += " · " + request;
        }

        if (snapshot.LastCheckFailure != null)
        {
            activity = Texts.T("status.checkFailed", activity);
        }

        Status = snapshot.SourceState switch
        {
            ThreadSourceState.Archived => Texts.T("status.archived", activity),
            ThreadSourceState.NotFound => Texts.T("status.unavailable", activity),
            _ => activity
        };
        Completion = snapshot.SelectedMedia == 0 ? 0 : 100d * snapshot.CompletedMedia / snapshot.SelectedMedia;
        Progress = Texts.T("progress.media", CompletedMedia, SelectedMedia, Completion);
        ExceptionHint = snapshot.MediaFailures.Count > 0 ? Texts.T("work.errorSummary", snapshot.MediaFailures.Count) : string.Empty;
        if (snapshot.Content?.ExcludedMedia > 0)
            Progress += " · " + Texts.T("progress.excluded", snapshot.Content.ExcludedMedia);
        Queued = snapshot.Downloads.Queued;
        Active = snapshot.Downloads.Active;
        string[] mediaErrors = snapshot.MediaFailures.Values.Select(mediaFailure => mediaFailure.Message).Distinct().Take(5).ToArray();
        ErrorDetails = string.Join(Environment.NewLine, mediaErrors);
        if (snapshot.LastCheckFailure is FailureInfo failure)
        {
            ErrorDetails = string.IsNullOrEmpty(ErrorDetails) ? failure.Message : failure.Message + Environment.NewLine + ErrorDetails;
        }
        if (snapshot.Content?.CaptureFailure is FailureInfo captureFailure)
            ErrorDetails += (ErrorDetails.Length == 0 ? string.Empty : Environment.NewLine) + Texts.T("nav.archive") + ": " + captureFailure.Message;
        if (snapshot.Content?.SelectionFailure is FailureInfo selectionFailure)
            ErrorDetails += (ErrorDetails.Length == 0 ? string.Empty : Environment.NewLine) + Texts.T("nav.rules") + ": " + selectionFailure.Message;

        IssueDetails = ErrorDetails;
        ContextDetails = string.Empty;
        if (snapshot.Work is ThreadWorkSnapshot work)
        {
            WorkStatusPresentation presentation = WorkStatusFormatter.Format(work, _isPaused);
            Status = presentation.Status;
            IssueDetails = string.Join(Environment.NewLine, new[] { presentation.IssueDetails, ErrorDetails }.Where(value => !string.IsNullOrEmpty(value)));
            ContextDetails = presentation.ContextDetails;
            ExceptionHint = presentation.ExceptionHint;
            Progress = presentation.Status + " · " + presentation.Progress;
            if (snapshot.Content?.ExcludedMedia > 0)
                Progress += " · " + Texts.T("progress.excluded", snapshot.Content.ExcludedMedia);
            Completion = presentation.Completion;
            ErrorDetails = string.Join(Environment.NewLine, new[] { presentation.Details, ErrorDetails }.Where(value => !string.IsNullOrEmpty(value)));
        }
        // Only the generic intervention label is redundant with a quantified hint.
        // Other lifecycle states (and pause) must survive even alongside exceptions.
        bool genericIntervention = Snapshot.Work is { Intent: MeowChan.Persistence.WorkIntent.Watching } currentWork
            && (currentWork.CapturePending || currentWork.Media.Any(item => item.Status == MeowChan.Persistence.MediaWorkStatus.NeedsIntervention));
        CompactStatus = HasExceptionHint
            ? genericIntervention
                ? ExceptionHint + (_isPaused ? " · " + Texts.T("work.paused") : string.Empty)
                : Status + " · " + ExceptionHint
            : Status;
        // Durable media work does not erase the current check failure. Keep the
        // full failure in details and the lifecycle cue in the compact surface.
        if (snapshot.Work != null && snapshot.LastCheckFailure != null)
            CompactStatus = Texts.T("status.checkFailed", CompactStatus);
        if (snapshot.Work == null && _isPaused
            && snapshot.Activity is not (ThreadActivity.Stopped or ThreadActivity.Finished or ThreadActivity.NotFound))
            CompactStatus += " · " + Texts.T("work.paused");
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static bool SameSnapshot(ThreadSnapshot previous, ThreadSnapshot current) =>
        ReferenceEquals(previous, current) ||
        previous.Id == current.Id && previous.Url == current.Url && previous.Title == current.Title
        && previous.DownloadDirectory == current.DownloadDirectory && previous.Activity == current.Activity
        && previous.SourceState == current.SourceState && previous.RequestProgress == current.RequestProgress
        && previous.KnownMedia == current.KnownMedia && previous.CompletedMedia == current.CompletedMedia
        && previous.LastCheckFailure == current.LastCheckFailure && previous.Downloads == current.Downloads
        && previous.Content == current.Content
        && (ReferenceEquals(previous.MediaFailures, current.MediaFailures)
            || previous.MediaFailures.SequenceEqual(current.MediaFailures))
        && SameWork(previous.Work, current.Work);

    private static bool SameWork(ThreadWorkSnapshot? previous, ThreadWorkSnapshot? current) =>
        ReferenceEquals(previous, current) || previous != null && current != null
        && previous.Url == current.Url && previous.Intent == current.Intent && previous.Source == current.Source
        && previous.EndObservedAt == current.EndObservedAt && previous.CapturePending == current.CapturePending
        && (ReferenceEquals(previous.Media, current.Media) || previous.Media.SequenceEqual(current.Media));

    public void SetPaused(bool isPaused)
    {
        if (_disposed || _isPaused == isPaused) return;
        _isPaused = isPaused;
        UpdateSnapshot(new DesktopThreadEntry(WatchId, Snapshot, HostName, BoardName));
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
