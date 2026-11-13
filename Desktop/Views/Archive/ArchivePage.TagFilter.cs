using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan;

// Own-tag part of the archive search draft. It follows the existing draft/applied split: the editor
// only edits a draft; a search binds the definition to the tag index once, and that frozen request
// is what list, paging, gallery, export and reader navigation all use.
public partial class ArchivePage
{
    private readonly List<Guid> _draftIncludeTags = new();
    private readonly List<Guid> _draftExcludeTags = new();
    private sealed record TagChoice(Guid? Id, string Label) { public override string ToString() => Label; }

    private void InitializeTagFilterEditor()
    {
        foreach (string name in new[] { "ArchiveTagChoice", "ArchiveTagMode" })
            Control<ComboBox>(name).SelectionChanged += (_, _) => { if (!_localizingSearchOptions) DraftEdited("tags"); };
        // The "unfinished entry" mark belongs to a chosen tag; it goes away as soon as the choice does.
        ArchiveTagChoice.SelectionChanged += (_, _) =>
        { if (!HasPendingTagChoice) DataValidationErrors.ClearErrors(ArchiveTagChoice); };
    }

    private string? TagFilterName(Guid id) => _privateTags?.Find(new TagId(id))?.Name.Display;
    private string TagFilterLabel(Guid id) => TagFilterName(id) ?? Texts.T("tags.filter.deleted");

    private PrivateTagStoreSnapshot? CurrentTagSnapshot()
    {
        ReloadPrivateTags();
        return _privateTags;
    }

    private static string TagFilterError(ArchiveTagFilterException error) => Texts.T(
        error.Error == ArchiveTagFilterError.UnknownTag ? "tags.filter.error.unknown" : "tags.filter.error.unavailable");

    /// <summary>
    /// Binds a definition to the current tag index. A tag filter that cannot be bound is refused with
    /// an explained error; it is never dropped, because that would silently show more threads.
    /// </summary>
    internal ArchiveSearchRequest CreateSearchRequest(ArchiveSearchSpec spec)
    {
        try { return ArchiveSearchRequest.Create(spec, spec.Tags == null ? null : CurrentTagSnapshot()); }
        catch (ArchiveTagFilterException error) { throw new InvalidOperationException(TagFilterError(error)); }
    }

    // A refresh of the applied search re-reads tag membership, like it re-reads work status.
    private ArchiveSearchRequest RefreshTagMembership(ArchiveSearchRequest request)
    {
        if (request.Spec.Tags == null) return request;
        try { return request.WithTagSnapshot(CurrentTagSnapshot()); }
        catch (ArchiveTagFilterException error) { throw new InvalidOperationException(TagFilterError(error)); }
    }

    // A refused refresh or chip removal explains itself; the explanation goes away with the next
    // list that was actually loaded, and only if nothing else replaced it meanwhile.
    private string? _shownTagFilterError;
    private void ShowTagFilterError(string message)
    {
        _shownTagFilterError = message;
        SearchEditorError(message);
    }
    private void ClearTagFilterError()
    {
        if (_shownTagFilterError == null) return;
        if (ArchiveSearchError.Text == _shownTagFilterError) SearchEditorError(string.Empty);
        _shownTagFilterError = null;
    }

    private void OnAddTagInclude(object? sender, RoutedEventArgs e) => AddDraftTag(include: true);
    private void OnAddTagExclude(object? sender, RoutedEventArgs e) => AddDraftTag(include: false);

    private void AddDraftTag(bool include)
    {
        if (ArchiveTagChoice.SelectedItem is not TagChoice { Id: Guid id }) return;
        if (DraftTagRefusal(include ? _draftIncludeTags : _draftExcludeTags, include ? _draftExcludeTags : _draftIncludeTags, id) is string refusal)
        { SearchEditorError(refusal); return; }
        try
        {
            _ = new ArchiveTagFilter(include ? _draftIncludeTags.Append(id) : _draftIncludeTags,
                include ? _draftExcludeTags : _draftExcludeTags.Append(id));
        }
        catch (ArgumentException exception) { SearchEditorError(exception.Message); return; }
        (include ? _draftIncludeTags : _draftExcludeTags).Add(id);
        DataValidationErrors.ClearErrors(ArchiveTagChoice);
        SearchEditorError(string.Empty);
        DraftEdited("tags");
        RenderDraftSets();
    }

    /// <summary>
    /// Why the chosen tag cannot join the list, as a localized sentence, or null when it can. A list holds at most
    /// <see cref="ArchiveTagFilter.MaximumTagsPerList"/> tags (a fixed limit of the search definition), and a tag cannot
    /// be included and excluded at once. Adding a tag that is already in the list changes nothing and is allowed.
    /// </summary>
    internal static string? DraftTagRefusal(IReadOnlyCollection<Guid> list, IReadOnlyCollection<Guid> otherList, Guid id) =>
        list.Contains(id) ? null
        : otherList.Contains(id) ? Texts.T("tags.filter.error.both")
        : list.Count >= ArchiveTagFilter.MaximumTagsPerList ? Texts.T("tags.filter.error.tooMany", ArchiveTagFilter.MaximumTagsPerList)
        : null;

    private ArchiveTagFilter? ReadTagFilterDraft() => ArchiveTagFilter.OrNull(_draftIncludeTags, _draftExcludeTags,
        ArchiveTagMode.SelectedIndex == 1 ? ArchiveTagMatch.Any : ArchiveTagMatch.All);

    private bool HasPendingTagChoice => ArchiveTagChoice.SelectedItem is TagChoice { Id: not null };

    private void WriteTagFilterDraft(ArchiveTagFilter? tags)
    {
        _draftIncludeTags.Clear(); _draftExcludeTags.Clear();
        if (tags != null) { _draftIncludeTags.AddRange(tags.Include); _draftExcludeTags.AddRange(tags.Exclude); }
        ArchiveTagMode.SelectedIndex = tags?.Match == ArchiveTagMatch.Any ? 1 : 0;
        ArchiveTagChoice.SelectedIndex = 0;
    }

    private Action CaptureTagFilterDraft()
    {
        Guid[] include = _draftIncludeTags.ToArray(), exclude = _draftExcludeTags.ToArray();
        int match = ArchiveTagMode.SelectedIndex;
        Guid? pending = (ArchiveTagChoice.SelectedItem as TagChoice)?.Id;
        return () =>
        {
            _draftIncludeTags.Clear(); _draftIncludeTags.AddRange(include);
            _draftExcludeTags.Clear(); _draftExcludeTags.AddRange(exclude);
            ArchiveTagMode.SelectedIndex = match;
            RenderDraftTagFilter();
            ComboBox combo = ArchiveTagChoice;
            if (pending != null && combo.ItemsSource is IEnumerable<TagChoice> options)
                combo.SelectedItem = options.FirstOrDefault(option => option.Id == pending) ?? options.First();
        };
    }

    private void RenderDraftTagFilter()
    {
        RenderDraftTags("ArchiveDraftTags", "ArchiveTagChoice",
            _draftIncludeTags.Select(id => (Texts.T("tags.filter.includeChip", TagFilterLabel(id)), (Action)(() =>
            { _draftIncludeTags.Remove(id); DraftEdited("tags"); RenderDraftSets(); })))
            .Concat(_draftExcludeTags.Select(id => (Texts.T("tags.filter.excludeChip", TagFilterLabel(id)), (Action)(() =>
            { _draftExcludeTags.Remove(id); DraftEdited("tags"); RenderDraftSets(); })))));
        // C1: drafted exclusions look like applied ones (chip-exclude); the label already says "without".
        foreach (Control excluded in Control<WrapPanel>("ArchiveDraftTags").Children.Skip(_draftIncludeTags.Count))
            MeowChan.Desktop.Controls.WorkspaceChips.MarkFilter(excluded, exclude: true);
        ComboBox combo = ArchiveTagChoice;
        PrivateTagStoreSnapshot? tags = _privateTags;
        HashSet<Guid> used = _draftIncludeTags.Concat(_draftExcludeTags).ToHashSet();
        // A tag already in the draft is not offered again, so include and exclude cannot overlap.
        TagChoice[] options = new[] { new TagChoice(null, Texts.T(tags == null ? "tags.filter.unavailable"
                : tags.Tags.Count == 0 ? "tags.filter.none" : "tags.filter.choose")) }
            .Concat((tags?.Tags ?? Array.Empty<PrivateTagDefinition>()).Where(tag => !used.Contains(tag.Id.Value))
                .Select(tag => new TagChoice(tag.Id.Value, tag.Name.Display))).ToArray();
        Guid? selected = (combo.SelectedItem as TagChoice)?.Id;
        _localizingSearchOptions = true;
        try
        {
            combo.ItemsSource = options;
            combo.SelectedItem = options.FirstOrDefault(option => selected != null && option.Id == selected) ?? options[0];
        }
        finally { _localizingSearchOptions = false; }
        ArchiveAddTagInclude.IsEnabled = ArchiveAddTagExclude.IsEnabled = options.Length > 1;
    }

    private void LocalizeTagFilterEditor()
    {
        SetOptions("ArchiveTagMode", "tags.filter.matchAll", "tags.filter.matchAny");
        ArchiveAddTagInclude.Content = Texts.T("tags.filter.include");
        ArchiveAddTagExclude.Content = Texts.T("tags.filter.exclude");
        ToolTip.SetTip(ArchiveAddTagInclude, Texts.T("tags.filter.includeHint"));
        ToolTip.SetTip(ArchiveAddTagExclude, Texts.T("tags.filter.excludeHint"));
        AutomationProperties.SetName(ArchiveAddTagInclude, Texts.T("tags.filter.includeHint"));
        AutomationProperties.SetName(ArchiveAddTagExclude, Texts.T("tags.filter.excludeHint"));
        AutomationProperties.SetName(ArchiveTagChoice, Texts.T("tags.filter.label"));
        AutomationProperties.SetName(ArchiveTagMode, Texts.T("tags.filter.matchLabel"));
    }

    // Names can change (rename) and tags can appear or disappear while the page is open.
    private void OnPrivateTagsChangedForFilter()
    {
        if (_ended) return;
        RenderDraftTagFilter();
        UpdateAppliedFilters();
    }

    private static ArchiveTagFilter? CopyTagFilter(ArchiveTagFilter? tags, string field, string? value) => tags == null ? null : field switch
    {
        "tags" => null,
        "tagInclude" => ArchiveTagFilter.OrNull(tags.Include.Where(id => id.ToString("D") != value), tags.Exclude, tags.Match),
        "tagExclude" => ArchiveTagFilter.OrNull(tags.Include, tags.Exclude.Where(id => id.ToString("D") != value), tags.Match),
        "tagMatch" => ArchiveTagFilter.OrNull(tags.Include, tags.Exclude),
        _ => tags
    };

    /// <summary>The frozen request behind the shown list; gallery, export and navigation reuse this instance.</summary>
    internal ArchiveSearchRequest? AppliedSearchRequest => _appliedSearch;

    // Applied chips name single tags; the draft treats the whole tag filter as one category.
    private static string DraftCategory(string field) => field.StartsWith("tag", StringComparison.Ordinal) ? "tags" : field;
}
