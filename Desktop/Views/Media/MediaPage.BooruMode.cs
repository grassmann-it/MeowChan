using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

// MP4: the Booru mode of the media page, a switch of the toolbar (saved per profile). On, a tag list stands left of the grid: every tag of
// the current results with how many tiles carry it, own tags and the provider's source tags (Danbooru) in two groups (MediaTagSidebar).
// A click includes a tag, Alt+click (or the menu) excludes it; both write the page's term set (M2), so chips, query and work view see the
// same filter. The counts are made off the UI thread after every presentation (MediaTagCountIndex keeps every resolved occurrence, so a
// filter change only adds up again) and a newer presentation cancels an older count. The list stays in the focus mode: the mode hides the
// rows for finding above the grid, while the tag list is part of browsing the results (switch the Booru mode off for the bare grid).
// Only the mechanism of a booru's tag column is taken over; the look is the app's own (section, rows, chips' marks).
public sealed partial class MediaPage
{
    private readonly ToggleButton _booruMode = new() { Name = "MediaBooruMode" };
    private readonly MediaTagSidebar _tagSidebar = new() { IsVisible = false, Margin = new Thickness(12, 0, 0, 8), VerticalAlignment = VerticalAlignment.Stretch };
    private readonly MediaSourceTagIndexCache _sourceTagCache = new();
    private MediaTagCountIndex? _tagCountIndex;
    private CancellationTokenSource? _tagCounting;
    private bool _booruModeOn;
    private bool _booruModeSyncing;

    /// <summary>Saves the mode and the order of the tag list (a view preference of the profile); null: nothing is saved.</summary>
    internal Func<bool, MediaTagListSort, Task>? SaveBooruModeAsync { get; set; }
    /// <summary>The top-N of each tag group (Settings); read at every rebuild of the list.</summary>
    internal Func<int>? TagListTopSource { get; set; }
    internal bool IsBooruMode => _booruModeOn;
    internal ToggleButton BooruModeButton => _booruMode;
    internal MediaTagSidebar TagSidebar => _tagSidebar;
    /// <summary>The count that runs now or ran last (tests).</summary>
    internal Task TagCountTask { get; private set; } = Task.CompletedTask;
    /// <summary>How many counts were started (tests: a filter change starts one, the mode off starts none).</summary>
    internal int TagCountRuns { get; private set; }
    /// <summary>The counts the list shows now (tests).</summary>
    internal MediaTagCountResult? TagCounts { get; private set; }

    // The grid surface with the tag list in a column left of it (hidden while the mode is off).
    private Control WrapWithTagSidebar(Control surface)
    {
        Avalonia.Controls.Grid row = new() { Name = "MediaBodyRow", ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(_tagSidebar);
        row.Children.Add(surface); Avalonia.Controls.Grid.SetColumn(surface, 1);
        _tagSidebar.OwnName = id => _userData.TagSnapshot?.Find(new TagId(id))?.Name.Display;
        _tagSidebar.Top = () => Math.Clamp(TagListTopSource?.Invoke() ?? MeowChan.Config.MediaTagListTopDefault, MeowChan.Config.MediaTagListTopMinimum, MeowChan.Config.MediaTagListTopMaximum);
        _tagSidebar.Changed = (term, change) => SetTagListTerm(term, change);
        _tagSidebar.ShowOnly = term => ShowOnlyTagListTerm(term);
        _tagSidebar.CopyName = name => _ = CopyTagNameAsync(name);
        _tagSidebar.SortChanged = _ => SaveBooruMode();
        _booruMode.IsCheckedChanged += (_, _) =>
        {
            if (!_booruModeSyncing) SetBooruMode(_booruMode.IsChecked == true);
        };
        return row;
    }

    private void LocalizeBooruMode()
    {
        WorkspaceIcons.MakeSquare(_booruMode, WorkspaceIcons.TagList, Texts.T("media.booruMode"), Texts.T("media.booruMode.tip"));
        Avalonia.Automation.AutomationProperties.SetHelpText(_booruMode, Texts.T("media.booruMode.tip"));
        _tagSidebar.Localize();
    }

    /// <summary>The saved mode and order of the profile (the shell, once); nothing is saved back.</summary>
    internal void RestoreBooruMode(bool on, MediaTagListSort sort)
    {
        _tagSidebar.Sort = sort;
        SetBooruMode(on, save: false);
    }

    /// <summary>Turns the Booru mode on or off (the toolbar switch, its entry under More, tests).</summary>
    internal void SetBooruMode(bool on, bool save = true)
    {
        if (_lifetime.IsCancellationRequested) return;
        bool changed = on != _booruModeOn;
        _booruModeOn = on;
        _booruModeSyncing = true;
        try { _booruMode.IsChecked = on; }
        finally { _booruModeSyncing = false; }
        _tagSidebar.IsVisible = on;
        if (!on)
        {
            _tagCounting?.Cancel();
            _tagCounting = null;
            // Keyboard focus in the list that just went away continues on the grid.
            if (_tagSidebar.IsKeyboardFocusWithin) FocusGrid();
        }
        else RefreshTagSidebar();
        if (changed && save) SaveBooruMode();
    }

    private void SaveBooruMode()
    {
        if (SaveBooruModeAsync is not { } save || _lifetime.IsCancellationRequested) return;
        _ = SaveQuietlyAsync(save(_booruModeOn, _tagSidebar.Sort));
    }

    private static async Task SaveQuietlyAsync(Task save)
    {
        try { await save; }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.booruMode.save")) { /* Logged and shown; the mode stays as chosen. */ }
    }

    // After every presentation (and when the mode starts): the list shows the terms of the filter now and the counts of the tiles now.
    private void RefreshTagSidebar()
    {
        if (!_booruModeOn || _lifetime.IsCancellationRequested) return;
        _tagSidebar.SetTerms(_terms.ToArray());
        ScheduleTagCount();
    }

    private void ScheduleTagCount()
    {
        _tagCounting?.Cancel();
        _tagCounting = null;
        if (!_booruModeOn || _lifetime.IsCancellationRequested) return;
        if (_snapshot is not { } snapshot || _filterFailed)
        {
            TagCounts = null;
            _tagSidebar.SetCounts(null, counting: _snapshot == null && (_building != null || _loadingCache));
            return;
        }
        IReadOnlyList<MediaLibraryEntry> shown = _shown;
        MediaLibraryUserData data = _userData;
        Func<BooruCatalogSnapshot?>? readBooru = _providerEntries > 0 ? ReadBooruCatalog : null;
        CancellationTokenSource counting = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _tagCounting = counting;
        TagCountRuns++;
        // "Counting…" only when the count takes a moment: a quick one must not make the line flicker.
        DispatcherTimer.RunOnce(() => { if (ReferenceEquals(_tagCounting, counting)) _tagSidebar.SetCounting(true); }, TimeSpan.FromMilliseconds(150));
        TagCountTask = CountTagsAsync(snapshot, shown, data, readBooru, _tagCountIndex, counting);
    }

    private async Task CountTagsAsync(MediaLibrarySnapshot snapshot, IReadOnlyList<MediaLibraryEntry> shown, MediaLibraryUserData data,
        Func<BooruCatalogSnapshot?>? readBooru, MediaTagCountIndex? previous, CancellationTokenSource counting)
    {
        MediaSourceTagIndexCache cache = _sourceTagCache;
        try
        {
            (MediaTagCountIndex index, MediaTagCountResult result) = await Task.Run(() =>
            {
                MediaSourceTagIndex? sourceTags = null;
                if (readBooru != null)
                    try { sourceTags = cache.For(readBooru()); }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException) { }
                PrivateTagStoreSnapshot? tags = data.TagSnapshot;
                Func<MediaLibraryEntry, IEnumerable<Guid>>? ownTags = tags == null ? null : entry => data.TagsOf(entry);
                MediaTagCountIndex made = MediaTagCountIndex.Create(snapshot.Entries, data, ownTags, id => tags?.Find(new TagId(id))?.Name.Display,
                    sourceTags, sourceTags == null ? null : sourceTags.TagsOf, previous);
                return (made, made.Count(shown, counting.Token));
            }, counting.Token);
            if (!ReferenceEquals(_tagCounting, counting) || _lifetime.IsCancellationRequested) return;
            _tagCountIndex = index;
            TagCounts = result;
            _tagSidebar.SetCounts(result, counting: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.tags.count"))
        {
            if (ReferenceEquals(_tagCounting, counting)) { TagCounts = null; _tagSidebar.SetCounts(null, counting: false); }
        }
        finally
        {
            if (ReferenceEquals(_tagCounting, counting)) _tagCounting = null;
            counting.Dispose();
        }
    }

    /// <summary>
    /// A gesture of the tag list on a tag (its term, included polarity): include, exclude or take it out of the filter. Own tags go through
    /// the same checks as every other way of adding a tag (the tags-per-list setting), source tags through the same limit. Null when done,
    /// else the localized reason, also shown on the error line.
    /// </summary>
    internal string? SetTagListTerm(MediaFilterTerm term, MediaTagListChange change)
    {
        if (_lifetime.IsCancellationRequested || change == MediaTagListChange.None) return null;
        MediaFilterTerm identity = MediaFilterTermSet.Identity(term);
        MediaFilterTerm? held = _terms.FirstOrDefault(other => MediaFilterTermSet.Identity(other) == identity);
        string? refusal = null;
        if (change == MediaTagListChange.Remove)
        {
            if (held != null) RemoveFilterTerm(held);
        }
        else
        {
            bool exclude = change == MediaTagListChange.Exclude;
            if (held?.Exclude == exclude) return null;
            if (identity.Field == MediaFilterField.Tag)
                refusal = held != null ? ToggleTagFilter(Guid.Parse(identity.Value)) : TryIncludeOrExclude(Guid.Parse(identity.Value), include: !exclude);
            else refusal = SourceTagRefusal(identity with { Exclude = exclude }) ?? AddFilterTerm(identity with { Exclude = exclude });
        }
        if (refusal != null)
        {
            _filterError.Text = refusal;
            _filterError.IsVisible = true;
        }
        return refusal;
    }

    // Source tags share the configured tags-per-list limit of the own tags (Settings, "Tags per filter list").
    private string? SourceTagRefusal(MediaFilterTerm term, IReadOnlyList<MediaFilterTerm>? terms = null)
    {
        if (term.Field != MediaFilterField.SourceTag) return null;
        int same = (terms ?? _terms).Count(other => other.Field == MediaFilterField.SourceTag && other.Exclude == term.Exclude && other.Value != term.Value);
        return same >= ArchiveTagFilterTagsPerListPreference ? Texts.T("coreLimits.tags.tooMany", ArchiveTagFilterTagsPerListPreference) : null;
    }

    /// <summary>"Show only this" of the tag list: the tag included and every other tag of its kind (own or source) out of the filter.</summary>
    internal string? ShowOnlyTagListTerm(MediaFilterTerm term)
    {
        if (_lifetime.IsCancellationRequested) return null;
        MediaFilterTerm included = MediaFilterTermSet.Identity(term);
        IReadOnlyList<MediaFilterTerm> next = MediaFilterTermSet.With(MediaFilterTermSet.WithoutField(_terms.ToArray(), included.Field), included);
        if (!IsValidFilter(next))
        {
            string refusal = Texts.T("media.filter.add.refused");
            _filterError.Text = refusal;
            _filterError.IsVisible = true;
            return refusal;
        }
        EditTerms(_ => next);
        return null;
    }

    private void EndBooruModeLifetime()
    {
        _tagCounting?.Cancel();
        _tagCounting = null;
    }
}
