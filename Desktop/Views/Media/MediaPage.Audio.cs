using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan;

// Sound property of videos for the "Ton" filter. Whether a video has sound is read once per file from its
// container headers in the background (never on the UI thread), kept in cache/media-audio-v1.json and
// shown as a hint. A video whose sound is not known yet is shown for neither "mit Ton" nor "ohne Ton".
public sealed partial class MediaPage
{
    private readonly ComboBox _soundFilter = new() { Name = "MediaSoundFilter", MinWidth = 130 };
    private readonly TextBlock _audioNote = new() { Name = "MediaAudioNote", TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false, Classes = { "muted" } };
    private MediaAudioMap _audio = MediaAudioMap.Empty;
    private MediaAudioScanProgress? _audioProgress;
    // The latest identity is also used by queued UI updates after the worker finishes.
    private CancellationTokenSource? _audioScan;
    private readonly object _audioScanLock = new();
    private CancellationTokenSource? _activeAudioScan;
    private int _audioUnknown;

    /// <summary>Completes when the current background sound check has finished (or was cancelled).</summary>
    internal Task AudioScanTask { get; private set; } = Task.CompletedTask;
    internal MediaAudioMap AudioResults => _audio;
    internal int UncheckedVideos => _audioUnknown;

    private bool SoundFilterActive => _soundFilter.SelectedIndex > 0;

    private string? AudioCachePath => _cachePath == null ? null
        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_cachePath))!, MediaAudioCache.FileName);

    /// <summary>Starts (or restarts, for a newer index) the low-priority sound check of the videos in the index.</summary>
    private void StartAudioProbe(MediaLibrarySnapshot snapshot)
    {
        CancellationTokenSource scan;
        lock (_audioScanLock)
        {
            _activeAudioScan?.Cancel();
            if (_lifetime.IsCancellationRequested) return;
            scan = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _audioScan = _activeAudioScan = scan;
        }
        MediaAudioMap current = _audio;
        string? cachePath = AudioCachePath;
        Task work = Task.Factory.StartNew(() => RunAudioScan(snapshot, current, cachePath, scan), scan.Token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        AudioScanTask = work.ContinueWith(_ =>
        {
            lock (_audioScanLock)
            {
                if (ReferenceEquals(_activeAudioScan, scan)) _activeAudioScan = null;
                scan.Dispose();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Runs on its own thread at below-normal priority; everything it shows goes through the dispatcher.
    private void RunAudioScan(MediaLibrarySnapshot snapshot, MediaAudioMap current, string? cachePath, CancellationTokenSource scan)
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        void Post(MediaAudioMap map, MediaAudioScanProgress? progress) =>
            Dispatcher.UIThread.Post(() => OnAudioUpdate(scan, map, progress));
        try
        {
            if (current.Count == 0 && cachePath != null && MediaAudioCache.Load(cachePath) is { } stored) { current = stored; Post(stored, null); }
            MediaAudioMap result = MediaLibraryAudioScanner.Scan(snapshot, current, entry => OpenForAudioProbe(entry, scan.Token),
                (map, progress) => Post(map, progress), cachePath, scan.Token);
            Post(result, null);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            Post(current, null); // Whatever could not be checked stays unknown.
        }
    }

    private Stream? OpenForAudioProbe(MediaLibraryEntry entry, CancellationToken token)
    {
        if (entry.Directory == null || entry.AssetId == null) return null;
        Task<MediaAssetSnapshot> manifestTask;
        lock (_manifests)
        {
            if (!_manifests.TryGetValue(entry.Directory, out manifestTask!))
                _manifests[entry.Directory] = manifestTask = _openManifest(entry.Directory, _lifetime.Token);
        }
        MediaAssetSnapshot manifest;
        try { manifest = manifestTask.WaitAsync(token).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
        // Only the file the index recorded: same hash in the manifest and the same length on disk.
        return MediaLibraryAudioScanner.OpenForProbe(manifest.FindById(entry.AssetId), entry.Sha256, entry.Length);
    }

    private void OnAudioUpdate(CancellationTokenSource scan, MediaAudioMap map, MediaAudioScanProgress? progress)
    {
        if (!ReferenceEquals(_audioScan, scan) || _lifetime.IsCancellationRequested) return;
        bool changed = !ReferenceEquals(_audio, map);
        _audio = map;
        _audioProgress = progress is { } value && value.Done < value.Total ? value : null;
        // The grid only depends on the results while a sound filter is active.
        if (changed && SoundFilterActive && _snapshot != null) Present(); else UpdateAudioNote();
        UpdateExportAll(); // The end of the scan enables "export all" again.
    }

    private void UpdateAudioNote()
    {
        List<string> parts = new();
        if (_audioProgress is { } progress) parts.Add(Texts.T("media.audio.probing", progress.Done, progress.Total));
        if (SoundFilterActive && _audioUnknown > 0) parts.Add(Texts.T("media.audio.unknown", _audioUnknown));
        _audioNote.Text = string.Join(" · ", parts);
        _audioNote.IsVisible = parts.Count > 0;
    }
}
