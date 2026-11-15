using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan;

// Own thread tags in the reader: one inline bar, like reader find. The bar always shows the
// currently open thread; it has no modal state to drain and never selects or navigates.
// Unlike reader find it stays open across thread changes until the user closes it.
public partial class ArchivePage
{
    private Func<PrivateTagLoadSnapshot>? _loadPrivateTags;
    private Func<string, CancellationToken, Task<PrivateTagCommit>>? _definePrivateTag;
    private Func<TagId, ResourceId, bool, CancellationToken, Task<PrivateTagCommit>>? _setThreadTag;
    private PrivateTagStoreSnapshot? _privateTags;
    private Dictionary<ResourceId, string[]> _threadTagNames = new();
    private bool _privateTagBusy;
    private string? _privateTagMessage;
    private ResourceId? _tagBarThread;
    // The user's explicit choice. The bar hides while no thread is open and returns with the next one,
    // so several threads can be tagged in a row; it never takes focus when it returns.
    private bool _tagBarWanted;
    private (ResourceId Thread, long Revision, int SlashCount, IReadOnlyList<PrivateTagName> Names)? _tagSuggestions;
    private (ResourceId Thread, long Revision, bool CanEdit, string? Message, string Language, int SlashCount)? _tagBarRendered;
    private int _slashTagSuggestionCount = SlashTagSuggestions.DefaultSuggestionCount;
    internal int SlashTagSuggestionCountPreference
    {
        get => _slashTagSuggestionCount;
        set
        {
            int count = Math.Clamp(value, SlashTagSuggestions.MinimumSuggestionCount, SlashTagSuggestions.MaximumSuggestionCount);
            if (_slashTagSuggestionCount == count) return;
            _slashTagSuggestionCount = count;
            _tagSuggestions = null;
            _tagBarRendered = null;
            UpdatePrivateTagActions();
        }
    }

    internal Task PrivateTagTask { get; private set; } = Task.CompletedTask;
    // Danbooru source tags offered below the own tags; read again each time the bar is opened.
    private readonly SourceTagSuggestions _sourceTagSuggestions = new();
    internal SourceTagSuggestions SourceTagSuggestions => _sourceTagSuggestions;

    internal void AttachPrivateTags(DesktopSessionHost host)
    {
        _loadPrivateTags = host.GetPublishedPrivateTagSnapshot;
        _definePrivateTag = host.DefinePrivateTagAsync;
        _setThreadTag = host.SetThreadTagAsync;
        _sourceTagSuggestions.Read = host.ReadBooruCatalog;
        AttachMediaSelectionTags(host);
        _renamePrivateTag = host.RenamePrivateTagAsync;
        _deletePrivateTag = host.DeletePrivateTagAsync;
        _inspectLocalTagOrphans = host.InspectLocalMediaTagOrphansAsync;
        _removeLocalTagOrphans = host.RemoveLocalMediaTagAssignmentsAsync;
        _tagMergeCommands = new TagMergeCommands(host.MergePrivateTagsAsync, host.UndoPrivateTagMergeAsync,
            host.AddPrivateTagAliasAsync, host.RemovePrivateTagAliasAsync);
        ReloadPrivateTags();
    }

    private void InitializePrivateTags()
    {
        // One local tunnel registration, before native Button.Enter click handling.
        ReaderTagBar.AddHandler(KeyDownEvent, OnTagBarKeyDown, RoutingStrategies.Tunnel);
        // Own tags as suggestions while typing; the tags of the open thread are not offered again.
        TagSuggestionInput.Attach(ReaderTagInput, () => CanEditThreadTags ? _privateTags : null,
            () => _thread == null || _privateTags == null ? new HashSet<TagId>()
                : _privateTags.TagsOf(_thread.Summary.Id).Select(tag => tag.Id).ToHashSet(),
            () => OnAddThreadTag(ReaderTagInput, new RoutedEventArgs()), _sourceTagSuggestions);
        // The tag column sorts by the sorted tag names, threads without tags last.
        ArchiveThreadTable.Columns[Array.IndexOf(ThreadColumnKeys, "tags")].CustomSortComparer = ArchiveThreadRow.TagColumnComparer.Instance;
        LocalizePrivateTags();
    }

    private bool IsTagBarSource(object? source) => source is Visual visual
        && visual.GetSelfAndVisualAncestors().Contains(ReaderTagBar);

    private bool CanEditThreadTags => _available && !_ended && !_privateTagBusy && _privateTags != null
        && _definePrivateTag != null && _setThreadTag != null && _thread != null;

    // The published host view is a volatile read; it never touches the disk on the UI thread.
    private void ReloadPrivateTags()
    {
        if (_ended || _loadPrivateTags == null) return;
        PrivateTagLoadSnapshot loaded = _loadPrivateTags();
        AdoptPrivateTags(loaded.State == PrivateTagLoadState.Ready ? loaded.Snapshot : null);
    }

    private void AdoptPrivateTags(PrivateTagStoreSnapshot? snapshot)
    {
        if (ReferenceEquals(snapshot, _privateTags)) return;
        _privateTags = snapshot;
        Dictionary<ResourceId, string[]> names = new();
        if (snapshot != null)
        {
            // Vocabulary order, independent of the order in which a thread was tagged.
            Dictionary<TagId, int> order = new();
            for (int index = 0; index < snapshot.Tags.Count; index++) order[snapshot.Tags[index].Id] = index;
            foreach (var group in snapshot.ThreadAssignments.GroupBy(item => item.Thread))
                names[group.Key] = group.Select(item => order[item.TagId]).Order()
                    .Select(index => snapshot.Tags[index].Name.Display).ToArray();
        }
        _threadTagNames = names;
        // Every change of the published tag view reaches the shown rows (list line and table column) in place:
        // no list reload, so selection and scroll position stay. An unreadable store clears them.
        RefreshThreadTagRows(ArchiveVisibleRows);
        OnPrivateTagsChangedForFilter();
    }

    private void RefreshThreadTagRows(IEnumerable<ArchiveThreadRow> rows)
    {
        bool available = _privateTags != null;
        foreach (ArchiveThreadRow row in rows) row.SetTags(_threadTagNames.GetValueOrDefault(row.Summary.Id), available);
    }

    // An older acknowledgement must not hide a newer committed state.
    private void AdoptPrivateTagCommit(PrivateTagCommit commit)
    {
        if (_privateTags == null || commit.Snapshot.Revision >= _privateTags.Revision) AdoptPrivateTags(commit.Snapshot);
    }

    private void ConfigurePrivateTagRows(IEnumerable<ArchiveThreadRow> rows)
    {
        ReloadPrivateTags();
        RefreshThreadTagRows(rows);
    }

    private void UpdatePrivateTagActions()
    {
        if (_ended) return;
        // A volatile read of the published view; a store that became unreadable must not keep showing its tags.
        ReloadPrivateTags();
        UpdateTagManagerActions();
        Button open = ReaderTagsOpen;
        StackPanel bar = ReaderTagBar;
        ResourceId? id = _thread?.Summary.Id;
        int count = id == null ? 0 : _threadTagNames.GetValueOrDefault(id)?.Length ?? 0;
        string label = count > 0 ? Texts.T("tags.openCount", count) : Texts.T("tags.open");
        if (!Equals(open.Content, label)) open.Content = label;
        open.IsEnabled = id != null && !IsReaderDialogOpen;
        bool show = _tagBarWanted && id != null;
        if (bar.IsVisible != show)
        {
            bar.IsVisible = show;
            if (!show) _tagBarRendered = null;
        }
        if (show) RenderTagBar();
    }

    private void OnOpenThreadTags(object? sender, RoutedEventArgs e)
    {
        if (_ended || IsReaderDialogOpen || _thread == null) return;
        if (_tagBarWanted) { CloseTagBar(); return; }
        ReloadPrivateTags();
        _tagBarWanted = true;
        _sourceTagSuggestions.Load();
        ReaderTagBar.IsVisible = true;
        RenderTagBar();
        // Opening is explicit, so it may take focus; later updates never do.
        if (CanEditThreadTags) ReaderTagInput.Focus();
    }

    // The thread list menu's "Tags…": show that thread in the reader pane and open its tag bar there.
    // Opening only ever shows the bar; an already open bar is kept and takes focus instead of toggling shut.
    internal async Task OpenThreadTagsAsync(ResourceId id)
    {
        if (_ended || IsReaderDialogOpen) return;
        if (_thread?.Summary.Id != id)
        {
            await LoadThreadAsync(id);
            if (_ended || _thread?.Summary.Id != id) return;
        }
        if (!_tagBarWanted) OnOpenThreadTags(null, new RoutedEventArgs());
        // The closing menu hands focus back to the list afterwards; ask again once it has settled.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_ended && _tagBarWanted && _thread?.Summary.Id == id && CanEditThreadTags) ReaderTagInput.Focus();
        }, DispatcherPriority.Input);
    }

    /// <summary>The thread the Tags shortcut acts on: the thread read in the large reader, otherwise the one selected
    /// thread (a batch has no Tags entry in the menu either). Null while the page cannot act.</summary>
    internal ResourceId? ThreadTagsShortcutTarget => _ended || !_available || _busy || IsReaderDialogOpen ? null
        : _largeReader ? _thread?.Summary.Id
        : FrozenArchiveSelection() is { Length: 1 } single ? single[0] : null;

    internal void OpenThreadTagsByShortcut()
    {
        if (ThreadTagsShortcutTarget is ResourceId id) OpenActions.Start(() => OpenThreadTagsAsync(id), "archive.threadTags.open");
    }

    private void OnCloseThreadTags(object? sender, RoutedEventArgs e) => CloseTagBar(focusOpen: true);

    private void CloseTagBar(bool focusOpen = false)
    {
        _tagBarWanted = false;
        ReaderTagBar.IsVisible = false;
        _privateTagMessage = null;
        _tagBarRendered = null;
        if (focusOpen && ReaderTagsOpen.IsEffectivelyVisible) ReaderTagsOpen.Focus();
    }

    private void OnTagBarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _ended || ShortcutService.IsCapturing || IsReaderDialogOpen
            || !ReaderTagBar.IsVisible || !IsTagBarSource(e.Source)
            || e.KeyModifiers != KeyModifiers.None) return;
        bool inInput = e.Source is Visual source && source.GetSelfAndVisualAncestors().Contains(ReaderTagInput);
        TagFieldKey key = inInput ? TagSuggestionInput.Route(ReaderTagInput, e) : TagFieldKey.None;
        // The first Escape in the field closes its open suggestion list, the next one the bar.
        if (key == TagFieldKey.CloseList) { e.Handled = true; ReaderTagInput.Dismiss(); }
        else if (e.Key == Key.Escape) { e.Handled = true; CloseTagBar(focusOpen: true); }
        // Enter adds only from the text field; buttons keep their native activation. A highlighted
        // suggestion is left to the field, which accepts and then adds it.
        else if (key == TagFieldKey.CommitTyped)
        { e.Handled = true; ReaderTagInput.Dismiss(); OnAddThreadTag(sender, new RoutedEventArgs()); }
    }

    private void OnAddThreadTag(object? sender, RoutedEventArgs e)
    {
        string name = ReaderTagInput.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return;
        PrivateTagTask = AddThreadTagAsync(name, clearInput: true);
    }

    // Create-or-get, then assign. Both steps are idempotent, so a repeated click cannot duplicate. One entry may name several
    // tags (TagEntryParser: spaces, quotes, commas); each goes through the alias table and is added in turn.
    private Task AddThreadTagAsync(string name, bool clearInput) => RunThreadTagOperationAsync(async (thread, token) =>
    {
        TagEntry entry = TagEntryParser.Parse(name, _privateTags);
        IEnumerable<string> names = TagEntryParser.IsPlain(entry, name) || entry.Invalid.Count > 0
            ? new[] { entry.Invalid.Count > 0 ? entry.Invalid[0] : name } : entry.Items.Select(item => item.Name.Display);
        foreach (string each in names)
        {
            PrivateTagCommit defined = await _definePrivateTag!(each, token);
            AdoptPrivateTagCommit(defined);
            AdoptPrivateTagCommit(await _setThreadTag!(defined.Tag, thread, true, token));
        }
    }, clearInput);

    private Task RemoveThreadTagAsync(TagId tag) => RunThreadTagOperationAsync(async (thread, token) =>
        AdoptPrivateTagCommit(await _setThreadTag!(tag, thread, false, token)), clearInput: false);

    private async Task RunThreadTagOperationAsync(Func<ResourceId, CancellationToken, Task> operation, bool clearInput)
    {
        if (!CanEditThreadTags) return;
        ResourceId thread = _thread!.Summary.Id; // Frozen target: a later thread switch cannot redirect it.
        bool focusInBar = IsTagBarSource(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement());
        _privateTagBusy = true;
        _privateTagMessage = null;
        UpdatePrivateTagActions();
        try
        {
            await operation(thread, _lifetime.Token);
            if (!_ended && clearInput && _tagBarThread == thread) ReaderTagInput.Text = string.Empty;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (PrivateTagStoreException error)
        {
            _privateTagMessage = Texts.T(error.Error switch
            {
                PrivateTagStoreError.InvalidName => "tags.error.invalidName",
                PrivateTagStoreError.CapacityExceeded => "tags.error.capacity",
                PrivateTagStoreError.TagLimitReached => "tags.error.tagLimit",
                PrivateTagStoreError.DocumentLimitReached => "tags.error.fileLimit",
                PrivateTagStoreError.StaleRevision => "tags.error.stale",
                _ => "tags.error.other"
            });
        }
        catch (Exception error)
        {
            // Store and host messages are constant texts without names or paths.
            _privateTagMessage = Texts.T("tags.error.other") + "\n" + error.Message;
        }
        finally
        {
            _privateTagBusy = false;
            if (!_ended)
            {
                UpdatePrivateTagActions();
                // The acted-on chip is gone after a rebuild; keep keyboard users inside the bar.
                if (focusInBar && ReaderTagBar.IsVisible && CanEditThreadTags)
                    ReaderTagInput.Focus();
            }
        }
    }

    private IReadOnlyList<PrivateTagName> ThreadTagSuggestions(ArchivedThreadView thread)
    {
        if (_tagSuggestions is { } cached && cached.Thread == thread.Summary.Id && cached.Revision == thread.Summary.Revision && cached.SlashCount == SlashTagSuggestionCountPreference)
            return cached.Names;
        CapturedPost? first = thread.PostCount > 0 ? thread.GetPosts(0, 1).FirstOrDefault() : null;
        IReadOnlyList<PrivateTagName> names = SlashTagSuggestions.Detect(SlashTagSuggestionCountPreference, thread.Summary.Title, first?.Subject,
            first?.Html == null ? null : ArchiveText.PlainText(first.Html));
        _tagSuggestions = (thread.Summary.Id, thread.Summary.Revision, SlashTagSuggestionCountPreference, names);
        return names;
    }

    private void RenderTagBar()
    {
        ArchivedThreadView thread = _thread!;
        ResourceId id = thread.Summary.Id;
        if (_tagBarThread != id)
        {
            // A different thread: never carry a draft name or an old message across.
            _tagBarThread = id;
            _privateTagMessage = null;
            ReaderTagInput.Text = string.Empty;
        }
        bool canEdit = CanEditThreadTags;
        var key = (id, _privateTags?.Revision ?? -1, canEdit, _privateTagMessage, Texts.Language, SlashTagSuggestionCountPreference);
        if (_tagBarRendered == key) return;
        _tagBarRendered = key;

        WrapPanel chips = ReaderTagChips;
        AutomationProperties.SetName(chips, Texts.T("tags.assigned"));
        chips.Children.Clear();
        PrivateTagDefinition[] assigned = _privateTags?.TagsOf(id).ToArray() ?? Array.Empty<PrivateTagDefinition>();
        foreach (PrivateTagDefinition tag in assigned)
        {
            TagId tagId = tag.Id;
            // A TextBlock keeps underscores in names literal instead of access-key markers.
            Button chip = MeowChan.Desktop.Controls.WorkspaceChips.MarkOwn(new Button { Content = MeowChan.Desktop.Controls.WorkspaceChips.RemovableContent(tag.Name.Display), IsEnabled = canEdit });
            MeowChan.Desktop.Controls.WorkspaceChips.MarkTagPill(chip, MeowChan.Desktop.Controls.WorkspaceChips.TagPillAssigned);
            string hint = Texts.T("tags.remove", tag.Name.Display);
            AutomationProperties.SetName(chip, hint);
            ToolTip.SetTip(chip, hint);
            chip.Click += (_, _) => PrivateTagTask = RemoveThreadTagAsync(tagId);
            chips.Children.Add(chip);
        }
        if (assigned.Length == 0)
            chips.Children.Add(new TextBlock { Text = Texts.T("tags.none"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });

        WrapPanel suggestions = ReaderTagSuggestions;
        suggestions.Children.Clear();
        PrivateTagName[] open = ThreadTagSuggestions(thread).Where(name => assigned.All(tag => tag.Name != name)).ToArray();
        suggestions.IsVisible = open.Length > 0 && _privateTags != null;
        if (suggestions.IsVisible)
        {
            suggestions.Children.Add(new TextBlock { Text = Texts.T("tags.suggestions"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            foreach (PrivateTagName name in open)
            {
                string display = name.Display;
                Button suggestion = MeowChan.Desktop.Controls.WorkspaceChips.MarkOffered(new Button { Content = new TextBlock { Text = "+ " + display }, IsEnabled = canEdit });
                MeowChan.Desktop.Controls.WorkspaceChips.MarkTagPill(suggestion);
                string hint = Texts.T("tags.addSuggestion", display);
                AutomationProperties.SetName(suggestion, hint);
                ToolTip.SetTip(suggestion, hint);
                suggestion.Click += (_, _) => PrivateTagTask = AddThreadTagAsync(display, clearInput: false);
                suggestions.Children.Add(suggestion);
            }
        }

        ReaderTagInput.IsEnabled = canEdit;
        if (!canEdit) ReaderTagInput.Dismiss();
        ReaderTagAdd.IsEnabled = canEdit;
        TextBlock status = ReaderTagStatus;
        status.Text = _privateTags == null ? Texts.T("tags.unavailable")
            : _privateTagMessage ?? (!_available ? Texts.T("tags.notReady") : string.Empty);
        status.IsVisible = status.Text.Length > 0;
    }

    private void LocalizePrivateTags()
    {
        // RL1: "Thread tags… · Ctrl+T", then what the button shows.
        ToolTip.SetTip(ReaderTagsOpen, CommandBindings.Tooltip("thread.tags") + Environment.NewLine + Texts.T("tags.openHint"));
        TagSuggestionInput.Localize(ReaderTagInput);
        ReaderTagAdd.Content = Texts.T("tags.add");
        MeowChan.Desktop.Controls.HeaderBar.MakeClose(ReaderTagClose, Texts.T("tags.close"));
        _tagBarRendered = null;
        UpdatePrivateTagActions();
    }
}
