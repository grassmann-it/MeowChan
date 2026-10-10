using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using oChan.Desktop.Localization;
using oChan.Interfaces;

namespace oChan.Desktop;

/// <summary>Passive binding state. Its owner applies snapshots on the desktop dispatcher.</summary>
public sealed class ThreadRow : INotifyPropertyChanged, IDisposable
{
    private bool _disposed;

    public long WatchId { get; }
    public ThreadSnapshot Snapshot { get; private set; }
    public string Url => Snapshot.Url;
    public string DownloadDirectory => Snapshot.DownloadDirectory;
    public int KnownMedia => Snapshot.KnownMedia;
    public int CompletedMedia => Snapshot.CompletedMedia;
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
    public string Progress { get; private set; } = string.Empty;
    public string ErrorDetails { get; private set; } = string.Empty;
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
        Snapshot = snapshot;
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
        Progress = Texts.T("progress.media", snapshot.CompletedMedia, snapshot.SelectedMedia, Completion);
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

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
