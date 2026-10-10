using System;
using System.ComponentModel;
using oChan.Desktop.Localization;

namespace oChan.Desktop;

/// <summary>Passive board binding state. Its owner applies snapshots on the desktop dispatcher.</summary>
public sealed class BoardRow : INotifyPropertyChanged, IDisposable
{
    private bool _disposed;

    public long WatchId { get; }
    public BoardSnapshot Snapshot { get; private set; }
    public string Url => Snapshot.Url;
    public string BoardCode => Snapshot.Id.Board;
    public string HostName { get; private set; } = string.Empty;
    public string NiceName { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public string ErrorDetails { get; private set; } = string.Empty;
    public string DownloadDirectory { get; private set; } = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public BoardRow(DesktopBoardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        WatchId = entry.WatchId;
        Snapshot = entry.Snapshot;
        UpdateSnapshot(entry);
    }

    public void UpdateSnapshot(DesktopBoardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_disposed)
        {
            return;
        }

        if (entry.WatchId != WatchId)
            throw new ArgumentException("A row cannot be rebound to another watch lifetime.", nameof(entry));
        BoardSnapshot snapshot = entry.Snapshot;
        Snapshot = snapshot;
        HostName = entry.HostName;
        NiceName = entry.BoardName;
        DownloadDirectory = snapshot.DownloadDirectory;
        string? request = RequestStatus.Format(snapshot.RequestProgress);
        Status = snapshot.LastCheckFailure == null ? request ?? Texts.T("status.monitoring")
            : request == null ? Texts.T("status.error") : Texts.T("status.error") + " · " + request;
        ErrorDetails = snapshot.LastCheckFailure?.Message ?? string.Empty;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
