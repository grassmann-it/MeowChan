using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// AP2: one search field with a scope chip in the archive list and in the reader find bar.
// Text keeps the existing archive search and reader find unchanged; File name and SHA-256 open the
// existing result dialogs with the field's draft. Each scope has its own box (and draft) in the same
// place, so the archive text draft, saved searches and reader find never see a file name or a hash.
public partial class ArchivePage
{
    // The latest SHA-256 draft refused by the field's own validation, per field; cleared by any edit.
    private bool _archiveHashRejected, _readerHashRejected;

    internal SearchScope ArchiveScope => ArchiveSearchScope.Scope;
    internal SearchScope ReaderScope => ReaderFindScope.Scope;

    private void InitializeUnifiedSearch()
    {
        ArchiveSearchScope.ScopeChanged += (_, _) => OnArchiveScopeChanged();
        ReaderFindScope.ScopeChanged += (_, _) => OnReaderScopeChanged();
        foreach (SearchBox box in new[] { ArchiveFilenameQuery, ArchiveHashQuery })
            box.AddHandler(KeyDownEvent, OnArchiveScopedKeyDown, RoutingStrategies.Tunnel);
        ArchiveHashQuery.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && _archiveHashRejected) { _archiveHashRejected = false; UpdateUnifiedSearch(); }
        };
        ReaderHashQuery.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && _readerHashRejected) { _readerHashRejected = false; UpdateUnifiedSearch(); }
        };
        ArchiveSearchScope.CarryActions = ArchiveCarryActions;
        ReaderFindScope.CarryActions = ReaderCarryActions;
        LocalizeUnifiedSearch();
    }

    // ---- Search carry-over (AP2 extension): the query moves with its compatible scope between the archive
    // field, the reader find bar and the Media page; a different query in the target is offered, never replaced.

    /// <summary>Host: show Media and hand it this query for its file name search.</summary>
    internal Action<string>? SearchInMediaRequested { get; set; }

    private static string CarryLabel(string key, string draft) =>
        draft.Length == 0 ? Texts.T(key + "Plain") : Texts.T(key, draft);

    private IEnumerable<SearchCarryAction> ArchiveCarryActions()
    {
        string draft = ArchiveScopedBox.Text ?? string.Empty;
        if (SearchInMediaRequested != null)
        {
            string? reason = draft.Length == 0 ? Texts.T("search.carry.empty")
                : ArchiveScope == SearchScope.Sha256 ? Texts.T("search.carry.noHash") : null;
            yield return new SearchCarryAction("ArchiveSearchInMedia", CarryLabel("search.carry.toMedia", draft), reason == null,
                () => { if (!_ended) SearchInMediaRequested?.Invoke(draft); }, reason);
        }
        string? thread = draft.Length == 0 ? Texts.T("search.carry.empty")
            : _thread == null || _busy || _ended || IsReaderDialogOpen ? Texts.T("search.carry.noThread") : null;
        yield return new SearchCarryAction("ArchiveSearchInThread", CarryLabel("search.carry.toThread", draft), thread == null,
            () => CarryToReader(ArchiveScope, draft), thread);
    }

    private IEnumerable<SearchCarryAction> ReaderCarryActions()
    {
        string draft = ReaderScopedBox.Text ?? string.Empty;
        string? reason = draft.Length == 0 ? Texts.T("search.carry.empty") : null;
        yield return new SearchCarryAction("ReaderSearchInArchive", CarryLabel("search.carry.toArchive", draft), reason == null,
            () => CarryFromReader(ReaderScope, draft), reason);
    }

    /// <summary>Puts a query carried from elsewhere into the archive field with <paramref name="scope"/>. An empty field
    /// takes it at once (Text also applies it); a different draft gets an offer instead.</summary>
    internal bool ReceiveCarriedSearch(SearchScope scope, string query, string source)
    {
        if (_ended) return false;
        ArchiveSearchScope.Scope = scope;
        SearchBox box = ArchiveScopedBox;
        return ArchiveSearchCarryOffer.Carry(source, query, box.Text ?? string.Empty, () =>
        {
            if (_ended) return;
            ArchiveSearchScope.Scope = scope;
            SearchBox target = ArchiveScopedBox;
            target.Text = query;
            target.Focus();
            if (scope == SearchScope.Text && !_busy) _ = ApplySearchAsync();
        });
    }

    // Archive field -> find bar of the open thread, same scope.
    private void CarryToReader(SearchScope scope, string query)
    {
        if (_ended || _busy || _thread == null || IsReaderDialogOpen) return;
        if (!ReaderFindBar.IsVisible) OnOpenThreadFind(null, new RoutedEventArgs());
        if (!ReaderFindBar.IsVisible) return;
        ReaderFindScope.Scope = scope;
        ReaderFindCarryOffer.Carry(Texts.T("nav.archive"), query, ReaderScopedBox.Text ?? string.Empty, () =>
        {
            if (_ended || !ReaderFindBar.IsVisible) return;
            ReaderFindScope.Scope = scope;
            SearchBox target = ReaderScopedBox;
            target.Text = query;
            target.Focus();
        });
    }

    // Find bar -> archive field, same scope. A large reader closes first so the archive field is visible.
    private void CarryFromReader(SearchScope scope, string query)
    {
        if (_ended || IsReaderDialogOpen) return;
        if (_largeReader) CloseReaderRequested?.Invoke();
        ReceiveCarriedSearch(scope, query, Texts.T("search.carry.reader"));
    }

    private void LocalizeUnifiedSearch()
    {
        foreach (SearchBox box in new[] { ArchiveFilenameQuery, ReaderFilenameQuery })
            box.PlaceholderText = Texts.T("search.scope.filename.placeholder");
        foreach (SearchBox box in new[] { ArchiveHashQuery, ReaderHashQuery })
            box.PlaceholderText = Texts.T("search.scope.hash.placeholder");
        ArchiveSearchCarryOffer.Localize();
        ReaderFindCarryOffer.Localize();
        ReaderFindSearch.Content = Texts.T("archive.search");
        AutomationProperties.SetName(ReaderFindSearch, Texts.T("archive.search"));
        UpdateUnifiedSearch();
    }

    private static SearchBox ScopedBox(SearchScope scope, SearchBox text, SearchBox fileName, SearchBox hash) => scope switch
    {
        SearchScope.FileName => fileName,
        SearchScope.Sha256 => hash,
        _ => text
    };

    private SearchBox ArchiveScopedBox => ScopedBox(ArchiveScope, ArchiveSearchText, ArchiveFilenameQuery, ArchiveHashQuery);
    private SearchBox ReaderScopedBox => ScopedBox(ReaderScope, ReaderFindQuery, ReaderFilenameQuery, ReaderHashQuery);

    private void OnArchiveScopeChanged()
    {
        _archiveHashRejected = false;
        UpdateUnifiedSearch();
        UpdateActions();
    }

    private void OnReaderScopeChanged()
    {
        _readerHashRejected = false;
        if (ReaderScope == SearchScope.Text)
        {
            // Back to Text: find the retained query again, exactly like reopening the bar.
            if (ReaderFindBar.IsVisible && _thread != null) ThreadFindTask = SearchThreadFindAsync();
        }
        else
        {
            // A running or finished thread find never publishes into another scope.
            ++_threadFindRequest;
            _threadFindCancellation?.Cancel();
            _threadFindResult = null;
            _threadFindCursor = -1;
            _threadFindStatus = "readerFind.ready";
        }
        LocalizeThreadFind();
    }

    /// <summary>Primary+F in the archive list: the text scope of the archive field gets the focus.</summary>
    internal void FocusArchiveTextSearch()
    {
        ArchiveSearchScope.Scope = SearchScope.Text;
        ArchiveSearchText.Focus();
    }

    private bool CanRunArchiveScope => ArchiveScope switch
    {
        SearchScope.FileName => CanOpenFilenameSearch,
        SearchScope.Sha256 => CanOpenHashSearch,
        _ => false
    };

    private bool CanRunReaderScope => ReaderScope switch
    {
        SearchScope.FileName => CanOpenFilenameSearch,
        SearchScope.Sha256 => CanOpenHashSearch,
        _ => false
    };

    // The scope hint: what the scope searches, why it cannot run here, or why a hash draft was refused.
    private string ScopeHint(SearchScope scope, bool hashRejected) => scope switch
    {
        SearchScope.FileName => Texts.T("search.scope.filename.hint"),
        SearchScope.Sha256 when hashRejected => MediaHashSearchCatalog.T("mediahash.invalid"),
        SearchScope.Sha256 when HashSearchDirectories.Length > 1 => Texts.T("reader.hashSearch.multiple"),
        SearchScope.Sha256 when HashSearchDirectories.Length == 0 => Texts.T("search.scope.hash.noThread"),
        SearchScope.Sha256 => Texts.T("search.scope.hash.hint"),
        _ => string.Empty
    };

    /// <summary>Shows the box of the chosen scope in both fields and sets hints and the scoped actions.</summary>
    private void UpdateUnifiedSearch()
    {
        if (ArchiveSearchScope is null) return; // before InitializeComponent
        SearchScope archive = ArchiveScope, reader = ReaderScope;
        ArchiveSearchText.IsVisible = archive == SearchScope.Text;
        ArchiveFilenameQuery.IsVisible = archive == SearchScope.FileName;
        ArchiveHashQuery.IsVisible = archive == SearchScope.Sha256;
        ArchiveScopedBox.ScopeChip = ArchiveSearchScope; // the chip sits inside the shown field (S5)
        string archiveHint = ScopeHint(archive, _archiveHashRejected);
        ArchiveSearchScopeHint.Text = archiveHint;
        ArchiveSearchScopeHint.IsVisible = archiveHint.Length > 0;
        // One line in the narrow search column (A4); a refused hash draft wraps so its reason stays readable.
        ArchiveSearchScopeHint.TextWrapping = archive == SearchScope.Sha256 && _archiveHashRejected
            ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap;
        ToolTip.SetTip(ArchiveSearchScopeHint, archiveHint.Length > 0 ? archiveHint : null);
        ArchiveSearchScopeHint.Classes.Set("workspace-error", archive == SearchScope.Sha256 && _archiveHashRejected);
        ArchiveSearchScopeHint.Classes.Set("muted", !(archive == SearchScope.Sha256 && _archiveHashRejected));
        if (archive != SearchScope.Text) SearchArchiveButton.IsEnabled = CanRunArchiveScope;

        bool text = reader == SearchScope.Text;
        ReaderFindQuery.IsVisible = text;
        ReaderFilenameQuery.IsVisible = reader == SearchScope.FileName;
        ReaderHashQuery.IsVisible = reader == SearchScope.Sha256;
        ReaderScopedBox.ScopeChip = ReaderFindScope;
        ReaderFindPrevious.IsVisible = text;
        ReaderFindNext.IsVisible = text;
        ReaderFindSearch.IsVisible = !text;
        ReaderFindSearch.IsEnabled = !text && CanRunReaderScope;
        if (!text)
        {
            ReaderFindRetry.IsVisible = false;
            ReaderFindStatus.Text = ScopeHint(reader, _readerHashRejected);
            ReaderFindStatus.Classes.Set("workspace-error", reader == SearchScope.Sha256 && _readerHashRejected);
            ReaderFindExcerpt.Inlines?.Clear();
        }
        else ReaderFindStatus.Classes.Set("workspace-error", false);
    }

    private void OnArchiveScopedKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Source is Avalonia.Visual source && Avalonia.VisualTree.VisualExtensions.GetSelfAndVisualAncestors(source).Contains(ArchiveSearchScope)) return;
        e.Handled = true;
        RunArchiveScope(sender as Control);
    }

    // Search button and Enter of the archive field in the File name and SHA-256 scopes.
    private void RunArchiveScope(Control? origin)
    {
        if (!CanRunArchiveScope) return;
        string draft = ArchiveScopedBox.Text ?? string.Empty;
        origin ??= ArchiveScopedBox;
        if (ArchiveScope == SearchScope.FileName) _ = OpenFilenameSearchAsync(origin, draft);
        else if (ValidHashDraft(draft, rejected => _archiveHashRejected = rejected))
            MediaHashSearchTask = OpenHashSearchAsync(origin, draft);
    }

    private void OnReaderScopeSearchClick(object? sender, RoutedEventArgs e) => RunReaderScope(ReaderScopedBox);

    // Search button and Enter of the reader find bar in the File name and SHA-256 scopes.
    private void RunReaderScope(Control? origin)
    {
        if (!CanRunReaderScope) return;
        string draft = ReaderScopedBox.Text ?? string.Empty;
        origin ??= ReaderScopedBox;
        if (ReaderScope == SearchScope.FileName) _ = OpenFilenameSearchAsync(origin, draft);
        else if (ValidHashDraft(draft, rejected => _readerHashRejected = rejected))
            MediaHashSearchTask = OpenHashSearchAsync(origin, draft);
    }

    // The same exact 64-hex rule as the dialog. An empty draft opens the dialog idle, as the former entry did;
    // a refused draft stays in the field with the dialog's own message beside it.
    private bool ValidHashDraft(string draft, Action<bool> rejected)
    {
        bool valid = draft.Length == 0 || MediaHashSearchOperation.IsExactSha256(draft);
        rejected(!valid);
        UpdateUnifiedSearch();
        return valid;
    }
}
