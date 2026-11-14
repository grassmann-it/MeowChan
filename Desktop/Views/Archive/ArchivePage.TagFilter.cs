using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ursa.Controls;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan;

// Own-tag part of the archive search draft. It follows the existing draft/applied split: the editor
// only edits a draft; a search binds the definition to the tag index once, and that frozen request
// is what list, paging, gallery, export and reader navigation all use.
public partial class ArchivePage
{
    private int _tagFilterTagsPerList = ArchiveTagFilter.DefaultTagsPerList;
    // Admission only: changing this preference never edits an existing draft or applied filter.
    internal int ArchiveTagFilterTagsPerListPreference
    {
        get => _tagFilterTagsPerList;
        set => _tagFilterTagsPerList = Math.Clamp(value, ArchiveTagFilter.MinimumTagsPerList, ArchiveTagFilter.MaximumConfigurableTagsPerList);
    }

    private readonly List<Guid> _draftIncludeTags = new();
    private readonly List<Guid> _draftExcludeTags = new();

    private void InitializeTagFilterEditor()
    {
        ArchiveTagMode.SelectionChanged += (_, _) => { if (!_localizingSearchOptions) DraftEdited("tags"); };
        // The popover adds to the draft at once (Enter includes, Shift+Enter or a leading "-" excludes); a tag the draft holds is
        // not offered again, so include and exclude cannot overlap.
        ArchiveTagChoice.Snapshot = () => _privateTags;
        ArchiveTagChoice.Used = () => _draftIncludeTags.Concat(_draftExcludeTags).Select(id => new TagId(id)).ToHashSet();
        ArchiveTagChoice.Picked = (id, exclude) => AddDraftTag(id, include: !exclude);
        // What was typed in the popover and not picked stays: it is an unfinished entry, explained when a search is started.
        ArchiveTagChoice.KeepQueryOnDismiss = true;
        // The host is a stand-alone DataValidationErrors (not inside a control template), so it has no templated owner whose
        // errors it would show; it shows its own. Without this the "choose Include or Exclude" request was set but never drawn.
        ArchiveTagChoiceHost.Owner = ArchiveTagChoiceHost;
        ArchiveTagChoice.PendingChanged += (_, _) =>
        {
            // The "unfinished entry" mark belongs to the typed tag; it goes away as soon as the entry does.
            if (!HasPendingTagChoice) DataValidationErrors.ClearErrors(ArchiveTagChoiceHost);
            if (!_localizingSearchOptions) DraftEdited("tags");
        };
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

    /// <summary>
    /// Adds a tag to the draft (the popover and the tests). Null when it was added, else the localized reason it was refused; the
    /// popover shows that reason itself, so the page's error line stays untouched.
    /// </summary>
    internal string? AddDraftTag(Guid id, bool include)
    {
        if (DraftTagRefusal(include ? _draftIncludeTags : _draftExcludeTags, include ? _draftExcludeTags : _draftIncludeTags, id, ArchiveTagFilterTagsPerListPreference) is string refusal)
            return refusal;
        if ((include ? _draftIncludeTags : _draftExcludeTags).Contains(id)) return null;
        try
        {
            _ = new ArchiveTagFilter(include ? _draftIncludeTags.Append(id) : _draftIncludeTags,
                include ? _draftExcludeTags : _draftExcludeTags.Append(id), ArchiveTagMatch.All, ArchiveTagFilter.MaximumConfigurableTagsPerList);
        }
        catch (ArgumentException) { return Texts.T("coreLimits.tags.invalid"); }
        (include ? _draftIncludeTags : _draftExcludeTags).Add(id);
        DataValidationErrors.ClearErrors(ArchiveTagChoiceHost);
        SearchEditorError(string.Empty);
        DraftEdited("tags");
        RenderDraftSets();
        return null;
    }

    /// <summary>Test seam of the popover: adds the own tag with this display name; null when added, else why not.</summary>
    internal string? AddDraftTagByName(string name, bool include) =>
        _privateTags?.Tags.FirstOrDefault(tag => tag.Name.Display == name) is { } found ? AddDraftTag(found.Id.Value, include) : "Unknown tag: " + name;

    /// <summary>
    /// The explicit include/exclude toggle of a draft tag chip: moves the tag to the other list. A refused move (a list at its
    /// limit) is explained on the page's error line. Nothing is searched; like every draft edit it waits for Search.
    /// </summary>
    internal string? ToggleDraftTag(Guid id)
    {
        bool wasIncluded = _draftIncludeTags.Contains(id);
        if (!wasIncluded && !_draftExcludeTags.Contains(id)) return null;
        // The destination grows; the source may already exceed a newly lowered preference.
        if (DraftTagRefusal(wasIncluded ? _draftExcludeTags : _draftIncludeTags, Array.Empty<Guid>(), id,
                ArchiveTagFilterTagsPerListPreference) is string refusal)
        { SearchEditorError(refusal); return refusal; }
        try
        {
            _ = new ArchiveTagFilter(wasIncluded ? _draftIncludeTags.Where(tag => tag != id) : _draftIncludeTags.Append(id),
                wasIncluded ? _draftExcludeTags.Append(id) : _draftExcludeTags.Where(tag => tag != id),
                ArchiveTagMatch.All, ArchiveTagFilter.MaximumConfigurableTagsPerList);
        }
        catch (ArgumentException)
        {
            string invalid = Texts.T("coreLimits.tags.invalid");
            SearchEditorError(invalid); return invalid;
        }
        (wasIncluded ? _draftIncludeTags : _draftExcludeTags).Remove(id);
        (wasIncluded ? _draftExcludeTags : _draftIncludeTags).Add(id);
        SearchEditorError(string.Empty);
        DraftEdited("tags");
        RenderDraftSets();
        return null;
    }

    /// <summary>Test seam of the chip toggle: switches the drafted own tag with this display name between include and exclude.</summary>
    internal string? ToggleDraftTagByName(string name) =>
        _privateTags?.Tags.FirstOrDefault(tag => tag.Name.Display == name) is { } found ? ToggleDraftTag(found.Id.Value) : "Unknown tag: " + name;

    /// <summary>Test seam: the names the popover offers for an empty search, in ordinal order.</summary>
    internal string[] AddableDraftTagNames => MeowChan.Desktop.Controls.TagFilterPicker.Choices(_privateTags, _draftIncludeTags.Concat(_draftExcludeTags).Select(id => new TagId(id)).ToHashSet(), null)
        .Select(item => item.Name.Display).Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Admission to the growing list only. Existing tags are never trimmed when the preference is lowered;
    /// an id already in the destination changes nothing and remains allowed.
    /// </summary>
    internal static string? DraftTagRefusal(IReadOnlyCollection<Guid> list, IReadOnlyCollection<Guid> otherList, Guid id) =>
        DraftTagRefusal(list, otherList, id, ArchiveTagFilter.DefaultTagsPerList);

    internal static string? DraftTagRefusal(IReadOnlyCollection<Guid> list, IReadOnlyCollection<Guid> otherList, Guid id, int tagsPerList) =>
        list.Contains(id) ? null
        : otherList.Contains(id) ? Texts.T("tags.filter.error.both")
        : list.Count >= tagsPerList ? Texts.T("coreLimits.tags.tooMany", tagsPerList)
        : null;

    private ArchiveTagFilter? ReadTagFilterDraft() => ArchiveTagFilter.OrNull(_draftIncludeTags, _draftExcludeTags,
        ArchiveTagMode.SelectedIndex == 1 ? ArchiveTagMatch.Any : ArchiveTagMatch.All, ArchiveTagFilter.MaximumConfigurableTagsPerList);

    // A tag typed in the popover and not added: the app cannot guess Include or Exclude for it.
    private bool HasPendingTagChoice => ArchiveTagChoice.HasPending;

    private void WriteTagFilterDraft(ArchiveTagFilter? tags)
    {
        _draftIncludeTags.Clear(); _draftExcludeTags.Clear();
        if (tags != null) { _draftIncludeTags.AddRange(tags.Include); _draftExcludeTags.AddRange(tags.Exclude); }
        ArchiveTagMode.SelectedIndex = tags?.Match == ArchiveTagMatch.Any ? 1 : 0;
        ArchiveTagChoice.ClearPending();
    }

    private Action CaptureTagFilterDraft()
    {
        Guid[] include = _draftIncludeTags.ToArray(), exclude = _draftExcludeTags.ToArray();
        int match = ArchiveTagMode.SelectedIndex;
        string pending = ArchiveTagChoice.Search.Text ?? string.Empty;
        return () =>
        {
            _draftIncludeTags.Clear(); _draftIncludeTags.AddRange(include);
            _draftExcludeTags.Clear(); _draftExcludeTags.AddRange(exclude);
            ArchiveTagMode.SelectedIndex = match;
            RenderDraftTagFilter();
            ArchiveTagChoice.Search.SetTextSilently(pending);
        };
    }

    private void RenderDraftTagFilter()
    {
        // Included tags first, then excluded ones, like the applied chips; a chip carries its state toggle besides the close mark.
        (Guid Id, bool Exclude)[] drafted = _draftIncludeTags.Select(id => (id, false)).Concat(_draftExcludeTags.Select(id => (id, true))).ToArray();
        RenderDraftTags("ArchiveDraftTags", "ArchiveTagChoice",
            drafted.Select(entry => (Texts.T(entry.Exclude ? "tags.filter.excludeChip" : "tags.filter.includeChip", TagFilterLabel(entry.Id)), (Action)(() =>
            { (entry.Exclude ? _draftExcludeTags : _draftIncludeTags).Remove(entry.Id); DraftEdited("tags"); RenderDraftSets(); }))),
            (position, label, remove) =>
            {
                // The one chip menu (switch, copy text, Copy tag name, remove); no second menu host on the same chip.
                return CreateTagFilterChip(label, TagFilterLabel(drafted[position].Id), drafted[position].Id, drafted[position].Exclude, remove,
                    () => ToggleDraftChip(drafted[position].Id), ChipMenuOptions(label, TagFilterLabel(drafted[position].Id), applied: false));
            });
        ArchiveTagChoice.Refresh();
    }

    // A toggle rebuilds the chips (the tag moves behind the other list); a keyboard user stays on the toggle of the same tag.
    private void ToggleDraftChip(Guid id)
    {
        WrapPanel panel = Control<WrapPanel>("ArchiveDraftTags");
        bool focused = panel.GetVisualDescendants().OfType<Control>().Any(control => control.IsKeyboardFocusWithin && control.IsFocused);
        if (ToggleDraftTag(id) != null || !focused) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended) return;
            ClosableTag? chip = Control<WrapPanel>("ArchiveDraftTags").Children.OfType<ClosableTag>().FirstOrDefault(tag => tag.Tag is Guid tagId && tagId == id);
            if (chip?.Content is StackPanel { Children: [Control toggle, ..] }) toggle.Focus(NavigationMethod.Tab);
            else ArchiveTagChoice.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void LocalizeTagFilterEditor()
    {
        SetOptions("ArchiveTagMode", "tags.filter.matchAll", "tags.filter.matchAny");
        ArchiveTagChoice.IncludeHint = Texts.T("tags.filter.includeHint");
        ArchiveTagChoice.ExcludeHint = Texts.T("tags.filter.excludeHint");
        ArchiveTagChoice.ApplyTexts();
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
        "tagInclude" => ArchiveTagFilter.OrNull(tags.Include.Where(id => id.ToString("D") != value), tags.Exclude, tags.Match, ArchiveTagFilter.MaximumConfigurableTagsPerList),
        "tagExclude" => ArchiveTagFilter.OrNull(tags.Include, tags.Exclude.Where(id => id.ToString("D") != value), tags.Match, ArchiveTagFilter.MaximumConfigurableTagsPerList),
        "tagMatch" => ArchiveTagFilter.OrNull(tags.Include, tags.Exclude, ArchiveTagMatch.All, ArchiveTagFilter.MaximumConfigurableTagsPerList),
        _ => tags
    };

    /// <summary>The frozen request behind the shown list; gallery, export and navigation reuse this instance.</summary>
    internal ArchiveSearchRequest? AppliedSearchRequest => _appliedSearch;

    // Applied chips name single tags; the draft treats the whole tag filter as one category.
    private static string DraftCategory(string field) => field.StartsWith("tag", StringComparison.Ordinal) ? "tags" : field;
}
