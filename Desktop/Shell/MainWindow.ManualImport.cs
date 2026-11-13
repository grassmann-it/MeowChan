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
// folders on the window. The dedupe check builds a media index once per dialog session (reusing what the Media page already read).
public partial class MainWindow
{
    private ManualImportDialog? _manualImportDialog;
    internal ManualImportDialog? ManualImportWindow => _manualImportDialog;

    // The catalog is checked off the UI thread (a large catalog takes time to read once); drops that arrive meanwhile join the same dialog.
    private bool _manualImportOpening;
    private readonly List<string> _manualImportPending = new();

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
        if (_manualImportOpening)
        {
            _manualImportPending.AddRange(roots);
            return;
        }
        _manualImportOpening = true;
        _manualImportPending.AddRange(roots);
        _ = OpenManualImportDialogCheckedAsync();
    }

    private async Task OpenManualImportDialogCheckedAsync()
    {
        ManualImportError? blocked = null;
        Exception? failure = null;
        DesktopSessionHost session = _session;
        // Whatever the check throws, the opening flag must come back down: a stuck flag would swallow every later import and drop.
        try { blocked = await Task.Run(session.ManualImportBlocked); }
        catch (ObjectDisposedException) { _manualImportPending.Clear(); return; }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally { _manualImportOpening = false; }
        string[] roots = _manualImportPending.ToArray();
        _manualImportPending.Clear();
        if (_isClosing || !ReferenceEquals(session, _session) || _session.State != DesktopSessionState.Ready) return;
        if (failure != null)
        {
            UnhandledErrors.TryRecover(failure, "import.open");
            ShowNotification(Texts.T("import.title"), failure.Message, NotificationType.Warning, operation: "import.open");
            return;
        }
        if (_manualImportDialog is { } open)
        {
            open.Session.AddRoots(roots);
            open.Activate();
            return;
        }
        if (blocked is { } error)
        {
            ShowNotification(Texts.T("import.title"), ManualImportSession.MessageText(ManualImportSession.MessageOf(error)), NotificationType.Warning, operation: "import.open");
            return;
        }
        ShowManualImportDialog(roots);
    }

    private void ShowManualImportDialog(IReadOnlyList<string> roots)
    {
        // What the library holds is computed once per dialog session and again only after an import changed it.
        KnownMediaState known = new();
        ManualImportKnownMediaSource knownSource = new(token => BuildKnownMediaAsync(known, token));
        ManualImportSession session = new(
            (request, progress, token) => _session.PlanManualImportAsync(request, knownSource.GetAsync, progress, token),
            async (plan, options, progress, token) =>
            {
                try { return await _session.RunManualImportAsync(plan, options, progress, token); }
                finally { knownSource.Invalidate(); }
            },
            DateTimeOffset.Now);
        session.MaximumTags = _session.Config.ImportMaxTagsPerBatch;
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
        dialog.Closed += (_, _) =>
        {
            knownSource.Dispose();
            if (ReferenceEquals(_manualImportDialog, dialog)) _manualImportDialog = null;
        };
        dialog.Show(this);
        session.AddRoots(roots);
    }

    /// <summary>What one dialog session keeps between index builds when the Media page is not there to share its own.</summary>
    private sealed class KnownMediaState
    {
        internal MediaManifestCache? Manifests;
        internal MediaLibrarySnapshot? Last;
        internal bool CacheTried;
    }

    private async Task<ManualImportKnownMedia> BuildKnownMediaAsync(KnownMediaState state, CancellationToken token)
    {
        // The Media page's manifest views and index are reused when it exists: unchanged manifests are not opened or
        // hashed again, so adding one file costs about what a refresh of that page costs, not a cold scan.
        MediaPage? page = _mediaPage;
        MediaManifestCache manifests = page?.ManifestCache ?? (state.Manifests ??= new MediaManifestCache());
        manifests.Invalidate(); // Files may have changed since the views were read: they prove their identity first.
        MediaLibrarySnapshot? previous = page?.Snapshot ?? state.Last;
        if (previous == null && !state.CacheTried)
        {
            state.CacheTried = true;
            MediaLibraryIndexCacheRead? read = await MediaLibraryIndexCache.LoadAsync(
                Path.Combine(_session.ProfileDirectory, "cache", MediaLibraryIndexCache.FileName), token).ConfigureAwait(false);
            previous = read?.Snapshot;
        }
        try
        {
            MediaLibrarySnapshot built = await MediaLibraryIndexBuilder.BuildAsync(_session.OpenArchiveReaderAsync, manifests,
                previous, null, token, _session.ReadBooruCatalog, _session.ReadManualImportCatalog).ConfigureAwait(false);
            state.Last = built;
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
