using System;
using System.ComponentModel;
using System.Collections.Generic;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>Passive board binding state. Its owner applies snapshots on the desktop dispatcher.</summary>
public sealed class BoardRow : INotifyPropertyChanged, IDisposable
{
    private bool _disposed;
    private string? _presentationLanguage;
    // Initialize replaces this immutable list even when reloading the same language.
    private IReadOnlyList<LanguageInfo>? _presentationLanguages;

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
        bool unchanged = _presentationLanguage == Texts.Language
            && ReferenceEquals(_presentationLanguages, Texts.Languages)
            && HostName == entry.HostName && NiceName == entry.BoardName && Snapshot == snapshot;
        Snapshot = snapshot;
        if (unchanged) return;
        _presentationLanguage = Texts.Language;
        _presentationLanguages = Texts.Languages;
        HostName = entry.HostName;
        NiceName = entry.BoardName;
        DownloadDirectory = snapshot.DownloadDirectory;
        string? request = RequestStatus.Format(snapshot.RequestProgress);
        // Same headline and inspector text as threads, Diagnostics and Sources (DiagnosticFailurePresentation).
        Status = snapshot.LastCheckFailure is not FailureInfo failure ? request ?? Texts.T("status.monitoring")
            : Texts.T("status.error") + (request == null ? string.Empty : " · " + request)
                + " · " + DiagnosticFailurePresentation.Headline(failure, DiagnosticOperation.Board);
        ErrorDetails = snapshot.LastCheckFailure is FailureInfo detail
            ? DiagnosticFailurePresentation.Inspector(detail, DiagnosticOperation.Board) : string.Empty;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
