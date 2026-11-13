using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Import;

namespace MeowChan;

// Manual import (contract manual-import-v1, phase 1): one dialog, opened from the "⋯" menu or by dropping files and
// folders on the window. The dedupe check builds a fresh media index (reusing what the Media page already read).
public partial class MainWindow
{
    private ManualImportDialog? _manualImportDialog;
    internal ManualImportDialog? ManualImportWindow => _manualImportDialog;

    private void OnManualImportMenuClick(object? sender, RoutedEventArgs e) => OpenManualImportDialog(Array.Empty<string>());

    /// <summary>Opens the import dialog (or brings the open one forward) and adds <paramref name="roots"/> to its selection.</summary>
    internal void OpenManualImportDialog(IReadOnlyList<string> roots)
    {
        if (_isClosing || _session.State != DesktopSessionState.Ready) return;
        if (_manualImportDialog is { } open)
        {
            open.Session.AddRoots(roots);
            open.Activate();
            return;
        }
        if (_session.ManualImportBlocked() is { } blocked)
        {
            ShowNotification(Texts.T("import.title"), Texts.T(ManualImportSession.MessageOf(blocked)), NotificationType.Warning, operation: "import.open");
            return;
        }
        ManualImportSession session = new(
            (request, progress, token) => _session.PlanManualImportAsync(request, BuildKnownMediaAsync, progress, token),
            (plan, options, progress, token) => _session.RunManualImportAsync(plan, options, progress, token),
            DateTimeOffset.Now);
        ManualImportDialog dialog = new(session, _session.ManualImportFolder, () => _session.GetPublishedPrivateTagSnapshot().Snapshot,
            _session.ReadBooruCatalog)
        {
            ShowBatch = ShowManualImportBatch
        };
        _manualImportDialog = dialog;
        session.Updated += () =>
        {
            // A finished import changes the library: the Media page reads it again (imports are always derived fresh).
            if (session.Stage == ManualImportStage.Done && session.Outcome != null) _ = _mediaPage?.RebuildAsync();
        };
        dialog.Closed += (_, _) => { if (ReferenceEquals(_manualImportDialog, dialog)) _manualImportDialog = null; };
        dialog.Show(this);
        session.AddRoots(roots);
    }

    private async Task<ManualImportKnownMedia> BuildKnownMediaAsync(CancellationToken token)
    {
        MediaLibrarySnapshot? previous = _mediaPage?.Snapshot;
        if (previous == null)
        {
            try { previous = MediaLibraryIndexCache.Load(Path.Combine(_session.ProfileDirectory, "cache", MediaLibraryIndexCache.FileName)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        try
        {
            MediaLibrarySnapshot built = await MediaLibraryIndexBuilder.BuildAsync(_session.OpenArchiveReaderAsync, Storage.MediaAssetReader.OpenAsync,
                previous, null, token, _session.ReadBooruCatalog, _session.ReadManualImportCatalog).ConfigureAwait(false);
            return ManualImportKnownMedia.From(built);
        }
        catch (FileNotFoundException)
        {
            // No archive, no Danbooru media and no imports yet: nothing to compare with.
            return ManualImportKnownMedia.Empty;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            // The check still runs; without an index every file counts as new and the dialog says the check was incomplete.
            return new ManualImportKnownMedia(new Dictionary<string, int>(), complete: false);
        }
    }

    private void ShowManualImportBatch(ManualImportBatch batch)
    {
        NavigateWorkspace(WorkspacePage.Media);
        if (_mediaPage is not { } page) return;
        page.FilterToThread(batch.Thread, batch.Name);
        _ = page.RebuildAsync();
    }
}
