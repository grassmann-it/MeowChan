using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan;

public partial class ArchivePage
{
    /// <summary>A removable filter value; C1 styles it as <c>chip chip-filter</c>, an exclusion additionally as <c>chip-exclude</c>.</summary>
    internal static ClosableTag CreateFilterTag(string label, Action remove, bool exclude = false) =>
        WorkspaceChips.CreateTermChip(label, negatable: false, exclude, remove);
    /// <summary>The text of a filter chip, whether it is a plain chip or a tag chip with its include/exclude toggle.</summary>
    internal static string FilterTagLabel(ClosableTag tag) => WorkspaceChips.TermChipLabel(tag);

    /// <summary>
    /// A tag chip with its state (S1/M2): the chip says "Tag: x" (included) or "Without tag: x" (excluded, <c>chip-exclude</c>), a small
    /// toggle button in front of the text and a click on the chip body (E4) switch between the two, and the chip's own close mark
    /// removes the tag. The one chip model of the media page (<see cref="WorkspaceChips.CreateTermChip"/>), with its context menu.
    /// </summary>
    internal static ClosableTag CreateTagFilterChip(string label, string tagName, Guid id, bool exclude, Action remove, Action toggle,
        FilterChipMenuOptions? menu = null)
    {
        ClosableTag tag = WorkspaceChips.CreateTermChip(label, negatable: true, exclude, remove, toggle, tagName,
            menu: menu ?? new FilterChipMenuOptions { TagName = tagName });
        tag.Tag = id;
        return tag;
    }

    internal static IReadOnlyList<(string Field, string? Value, string Label)> AppliedFilterLabels(ArchiveSearchSpec spec,
        Func<Guid, string?>? tagName = null)
    {
        List<(string, string?, string)> tags = new();
        if (!string.IsNullOrWhiteSpace(spec.Text)) tags.Add(("text", null, Texts.T("archive.textChip", spec.Text)));
        if (spec.Scope != MeowChan.Archive.ArchiveSearchScope.AllPosts) tags.Add(("scope", null, Texts.T("archive.textScopeChip", TextScopeLabel(spec.Scope))));
        tags.AddRange(spec.Sources.Select(source => ("source", (string?)source, Texts.T("archive.sourceChip", source))));
        tags.AddRange(spec.Boards.Select(board => ("board", (string?)BoardKey(board), Texts.T("archive.boardChip", BoardLabel(board)))));
        tags.AddRange(spec.SourceStates.Select(state => ("state", (string?)state.ToString(), StateLabel(state))));
        if (spec.CaptureEnabled.HasValue) tags.Add(("capture", null, Texts.T(spec.CaptureEnabled.Value ? "archive.captureEnabled" : "archive.captureDisabled")));
        if (spec.HasPosts.HasValue) tags.Add(("posts", null, Texts.T(spec.HasPosts.Value ? "archive.withPosts" : "archive.withoutPosts")));
        if (spec.WorkFilter != MeowChan.Archive.ArchiveWorkFilter.All) tags.Add(("work", null, Texts.T(new[] { "gallery.allWork", "gallery.watching", "gallery.followup", "gallery.intervention", "gallery.completed", "gallery.stopped" }[WorkIndex(spec.WorkFilter)])));
        if (spec.DateRange is { } date) tags.Add(("date", null, Texts.T("archive.dateChip",
            date.StartDate?.ToString("d", Texts.Culture) ?? "…", date.EndDate?.ToString("d", Texts.Culture) ?? "…", date.TimeZoneId)));
        if (spec.Tags is { } ownTags)
        {
            // A tag that no longer exists stays visible as such, so it can be removed deliberately.
            string Name(Guid id) => tagName?.Invoke(id) ?? Texts.T("tags.filter.deleted");
            tags.AddRange(ownTags.Include.Select(id => ("tagInclude", (string?)id.ToString("D"), Texts.T("tags.filter.includeChip", Name(id)))));
            tags.AddRange(ownTags.Exclude.Select(id => ("tagExclude", (string?)id.ToString("D"), Texts.T("tags.filter.excludeChip", Name(id)))));
            if (ownTags.Match == ArchiveTagMatch.Any) tags.Add(("tagMatch", null, Texts.T("tags.filter.anyChip")));
        }
        return tags.AsReadOnly();
    }
    // S7: "Reset all" follows the last applied chip, like on Media. It is the empty state's reset (search text and every filter;
    // the sort stays), so the chips and the link always agree on what is cleared.
    private readonly Button _appliedResetAll = new()
    {
        Name = "ArchiveAppliedResetAll", Classes = { "link" }, MinHeight = 24, Padding = new Avalonia.Thickness(8, 2),
        BorderThickness = new Avalonia.Thickness(0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };
    private bool _appliedResetAllWired;
    private int _appliedFilterCount;

    private void UpdateAppliedFilters()
    {
        WrapPanel panel = ArchiveAppliedFilters; panel.Children.Clear();
        if (!_appliedResetAllWired)
        {
            _appliedResetAllWired = true;
            _appliedResetAll.Click += (_, e) => OnResetArchiveFiltersClick(_appliedResetAll, e);
        }
        _appliedFilterCount = 0;
        if (_appliedSearch == null) { UpdateArchiveFilterToggle(); SyncSearchPanelGaps(); return; }
        var tags = AppliedFilterLabels(_appliedSearch.Spec, TagFilterName);
        _appliedFilterCount = tags.Count;
        if (tags.Count > 0)
        {
            TextBlock label = new() { Text = Texts.T("archive.appliedFilters"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            label.Classes.Add("workspace-meta");
            panel.Children.Add(label);
        }
        for (int i = 0; i < tags.Count; i++)
        {
            var item = tags[i]; int index = i;
            ClosableTag chip = CreateAppliedChip(item, index);
            // An own-tag chip got the chip menu from CreateTagFilterChip (one menu, one Copy tag name); any other applied chip gets it here.
            if (!WorkspaceChips.HasMenu(chip)) AttachFilterChipMenu(chip, async () => await RemoveAppliedFilter(item.Field, item.Value, index), applied: true);
            panel.Children.Add(chip);
        }
        if (tags.Count > 0)
        {
            _appliedResetAll.Content = Texts.T("media.filter.resetAll");
            ToolTip.SetTip(_appliedResetAll, Texts.T("media.filter.resetAllHint"));
            AutomationProperties.SetName(_appliedResetAll, Texts.T("media.filter.resetAllHint"));
            panel.Children.Add(_appliedResetAll);
        }
        UpdateArchiveFilterToggle();
        SyncSearchPanelGaps();
    }
    /// <summary>The search panel's rows carry no gap of their own; a row that shows something gets its 8 dip above it.</summary>
    private void SyncSearchPanelGaps()
    {
        ArchiveAppliedFilters.Margin = ArchiveAppliedFilters.Children.Count > 0 ? new Thickness(0, 8, 0, 0) : default;
        ArchiveSearchErrorHost.Margin = ArchiveSearchError.IsVisible || ArchiveRetrySearch.IsVisible ? new Thickness(0, 8, 0, 0) : default;
    }
    private static string CanonicalField(ArchiveSearchSpec spec, string field) => ArchiveSearchSpecJson.Serialize(field switch
    {
        "text" => ArchiveSearchSpec.FromExactIdentities(text: spec.Text),
        "source" => ArchiveSearchSpec.FromExactIdentities(sources: spec.Sources),
        "board" => ArchiveSearchSpec.FromExactIdentities(boards: spec.Boards),
        "state" => ArchiveSearchSpec.FromExactIdentities(sourceStates: spec.SourceStates),
        "capture" => ArchiveSearchSpec.FromExactIdentities(captureEnabled: spec.CaptureEnabled),
        "posts" => ArchiveSearchSpec.FromExactIdentities(hasPosts: spec.HasPosts),
        "work" => ArchiveSearchSpec.FromExactIdentities(workFilter: spec.WorkFilter),
        "date" => ArchiveSearchSpec.FromExactIdentities(dateRange: spec.DateRange),
        "tags" => ArchiveSearchSpec.FromExactIdentities().WithTags(spec.Tags),
        "scope" => ArchiveSearchSpec.FromExactIdentities().WithScope(spec.Scope),
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    });
    private bool DraftFieldMatches(ArchiveSearchSpec spec, string field)
    {
        // Read only this category: invalid raw input in a different field must
        // neither be applied nor prevent clearing the matching category.
        bool? Choice(string name) => Control<ComboBox>(name).SelectedIndex switch { 1 => true, 2 => false, _ => null };
        try
        {
            ArchiveSearchSpec draft = field switch
            {
                "text" => ArchiveSearchSpec.FromExactIdentities(text: ArchiveSearchText.Text ?? ""),
                "source" when string.IsNullOrWhiteSpace(IdentityText("ArchiveSourceFilter")) => ArchiveSearchSpec.FromExactIdentities(sources: _draftSources),
                "board" when string.IsNullOrWhiteSpace(IdentityText("ArchiveBoardFilter")) => ArchiveSearchSpec.FromExactIdentities(boards: _draftBoards),
                "state" when ArchiveStateFilter.SelectedIndex <= 0 => ArchiveSearchSpec.FromExactIdentities(sourceStates: _draftStates),
                "capture" => ArchiveSearchSpec.FromExactIdentities(captureEnabled: Choice("ArchiveCaptureFilter")),
                "posts" => ArchiveSearchSpec.FromExactIdentities(hasPosts: Choice("ArchivePostsFilter")),
                "work" => ArchiveSearchSpec.FromExactIdentities(workFilter: WorkChoice(ArchiveWorkFilter.SelectedIndex)),
                "date" => ArchiveSearchSpec.FromExactIdentities(dateRange: new ArchiveDateRange(ArchiveStartDate.ParseRaw(false),
                    ArchiveEndDate.ParseRaw(false), _draftTimeZone ?? "Etc/UTC")),
                "tags" when !HasPendingTagChoice => ArchiveSearchSpec.FromExactIdentities().WithTags(ReadTagFilterDraft()),
                "scope" => ArchiveSearchSpec.FromExactIdentities().WithScope(DraftTextScope),
                _ => throw new InvalidOperationException()
            };
            return CanonicalField(spec, field) == CanonicalField(draft, field);
        }
        catch { return false; }
    }
    private void WriteSearchField(ArchiveSearchSpec next, string field)
    {
        switch (field)
        {
            case "text": ArchiveSearchText.Text = next.Text; break;
            case "source": _draftSources.Clear(); _draftSources.AddRange(next.Sources); break;
            case "board": _draftBoards.Clear(); _draftBoards.AddRange(next.Boards); break;
            case "state": _draftStates.Clear(); _draftStates.AddRange(next.SourceStates); break;
            case "capture": ArchiveCaptureFilter.SelectedIndex = next.CaptureEnabled switch { true => 1, false => 2, _ => 0 }; break;
            case "posts": ArchivePostsFilter.SelectedIndex = next.HasPosts switch { true => 1, false => 2, _ => 0 }; break;
            case "work": ArchiveWorkFilter.SelectedIndex = WorkIndex(next.WorkFilter); break;
            case "date":
                _draftTimeZone = null;
                _writingDraft = true;
                try { ArchiveStartDate.RawText = ""; ArchiveEndDate.RawText = ""; }
                finally { _writingDraft = false; }
                UpdateTimeZoneLabel(); break;
            case "tags": WriteTagFilterDraft(next.Tags); break;
            case "scope": ArchiveTextScope.SelectedIndex = TextScopeToIndex(next.Scope); break;
        }
        DraftEdited(field);
        // A sourcechip removal must not revise an unrelated board draft/scope.
        if (field is "source" or "board" or "state" or "tags") RenderDraftSets();
    }
    internal async Task<bool> RemoveAppliedFilter(string field, string? value = null, int focusIndex = 0)
    {
        if (_busy || _ended || !CanRead || _appliedSearch == null) return false;
        bool success = await SearchFromAppliedChip(CopySearch(_appliedSearch.Spec, field, value), DraftCategory(field));
        if (success)
        {
            var tags = ArchiveAppliedFilters.Children.OfType<ClosableTag>().ToArray();
            if (tags.Length > 0) tags[Math.Clamp(focusIndex, 0, tags.Length - 1)].Focus();
            else ArchiveScopedBox.Focus();
        }
        return success;
    }
    /// <summary>
    /// Searches again with a changed copy of the applied definition (a removed or toggled chip). The draft category follows only
    /// when it still showed what was applied and was not edited meanwhile; a refused definition explains itself and keeps the list.
    /// </summary>
    private async Task<bool> SearchFromAppliedChip(ArchiveSearchSpec next, string category)
    {
        ArchiveSearchSpec applied = _appliedSearch!.Spec;
        long revision = _editorRevisions.GetValueOrDefault(category);
        bool matches = DraftFieldMatches(applied, category);
        ArchiveSearchRequest request;
        try { request = CreateSearchRequest(next); }
        catch (InvalidOperationException exception) { ShowTagFilterError(exception.Message); return false; }
        return await LoadListAsync(NewListLoad(request, afterCommit: () =>
        {
            if (matches && _editorRevisions.GetValueOrDefault(category) == revision) WriteSearchField(next, category);
        }));
    }
}
