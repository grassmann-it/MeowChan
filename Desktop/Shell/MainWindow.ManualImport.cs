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
using Serilog;

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

    // The "Import files…" command: the same dialog as the menu entry, with its shortcut. Like the menu entry it works on
    // every page; it waits while a blocking decision, another dialog or a busy session owns the window.
    private bool CanOpenManualImport => _restoreCompleted && !_isClosing && !_applyingPreferences && IsEnabled
        && _session.State == DesktopSessionState.Ready && !WorkspaceCommandBusy && !HasVisibleShellDialog()
        && !HasBlockingPageDecision() && _archivePage?.IsReaderDialogOpen != true;

    private void RegisterManualImportCommand() => _shortcutRouter?.Register(new MeowChan.Desktop.Commands.CommandRegistration(
        "import.open", this, () => !_largeReaderOpen && _gallery == null, () => CanOpenManualImport,
        () => OpenManualImportDialog(Array.Empty<string>())));

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
        catch (Exception checkError) when (checkError is not OutOfMemoryException) { failure = checkError; }
        finally { _manualImportOpening = false; }
        string[] roots = _manualImportPending.ToArray();
        _manualImportPending.Clear();
        if (_isClosing || !ReferenceEquals(session, _session) || _session.State != DesktopSessionState.Ready) return;
        if (failure != null)
        {
            // The exception text is English and may hold a profile path: it goes to the log, the user gets a sentence.
            Log.Warning(failure, "Could not check the import catalog before opening the import dialog.");
            ShowNotification(Texts.T("import.title"), Texts.T("import.open.failed"), NotificationType.Warning, operation: "import.open");
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

    /// <summary>What one dialog session keeps between index builds: its own manifest cache, the last index and whether the saved one was tried.</summary>
    private sealed class KnownMediaState
    {
        internal MediaManifestCache? Manifests;
        internal MediaLibrarySnapshot? Last;
        internal bool CacheTried;
    }

    private async Task<ManualImportKnownMedia> BuildKnownMediaAsync(KnownMediaState state, CancellationToken token)
    {
        // The Media page's index is reused when it exists: a thread whose manifest files still have the identities recorded in it is
        // not opened or hashed again, so adding one file costs about what a refresh of that page costs, not a cold scan. The check
        // keeps its own manifest cache: invalidating the page's shared one would make its thumbnail and sound-check views prove
        // their file identity again after every check, and the reuse does not depend on kept views.
        MediaPage? page = _mediaPage;
        MediaManifestCache manifests = state.Manifests ??= page?.CreateManifestCache() ?? new MediaManifestCache();
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
