using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan;

public partial class ArchivePage
{
    private sealed class FilterTagCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
    /// <summary>A removable filter value; C1 styles it as <c>chip chip-filter</c>, an exclusion additionally as <c>chip-exclude</c>.</summary>
    internal static ClosableTag CreateFilterTag(string label, Action remove, bool exclude = false)
    {
        string accessible = Texts.T("archive.removeFilter", label);
        ClosableTag tag = new()
        {
            Content = new TextBlock { Text = label, MaxWidth = 235, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
            Command = new FilterTagCommand(remove), Focusable = true
        };
        WorkspaceChips.MarkFilter(tag, exclude);
        ToolTip.SetTip(tag, accessible); AutomationProperties.SetName(tag, accessible);
        MeowChan.Desktop.Commands.CommandPresentation.SetId(tag, "archive.clear-filter");
        tag.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; remove(); } };
        return tag;
    }
    internal static IReadOnlyList<(string Field, string? Value, string Label)> AppliedFilterLabels(ArchiveSearchSpec spec,
        Func<Guid, string?>? tagName = null)
    {
        List<(string, string?, string)> tags = new();
        if (!string.IsNullOrWhiteSpace(spec.Text)) tags.Add(("text", null, Texts.T("archive.textChip", spec.Text)));
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
    private void UpdateAppliedFilters()
    {
        WrapPanel panel = ArchiveAppliedFilters; panel.Children.Clear();
        if (_appliedSearch == null) return;
        var tags = AppliedFilterLabels(_appliedSearch.Spec, TagFilterName);
        if (tags.Count > 0)
        {
            TextBlock label = new() { Text = Texts.T("archive.appliedFilters"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            label.Classes.Add("workspace-meta");
            panel.Children.Add(label);
        }
        for (int i = 0; i < tags.Count; i++)
        {
            var item = tags[i]; int index = i;
            panel.Children.Add(CreateFilterTag(item.Label, async () => await RemoveAppliedFilter(item.Field, item.Value, index), item.Field == "tagExclude"));
        }
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
        }
        DraftEdited(field);
        // A sourcechip removal must not revise an unrelated board draft/scope.
        if (field is "source" or "board" or "state" or "tags") RenderDraftSets();
    }
    internal async Task<bool> RemoveAppliedFilter(string field, string? value = null, int focusIndex = 0)
    {
        if (_busy || _ended || !CanRead || _appliedSearch == null) return false;
        ArchiveSearchSpec applied = _appliedSearch.Spec;
        ArchiveSearchSpec next = CopySearch(applied, field, value);
        string category = DraftCategory(field);
        long revision = _editorRevisions.GetValueOrDefault(category);
        bool matches = DraftFieldMatches(applied, category);
        ArchiveSearchRequest request;
        try { request = CreateSearchRequest(next); }
        catch (InvalidOperationException exception) { ShowTagFilterError(exception.Message); return false; }
        bool success = await LoadListAsync(NewListLoad(request, afterCommit: () =>
        {
            if (matches && _editorRevisions.GetValueOrDefault(category) == revision) WriteSearchField(next, category);
        }));
        if (success)
        {
            var tags = ArchiveAppliedFilters.Children.OfType<ClosableTag>().ToArray();
            if (tags.Length > 0) tags[Math.Clamp(focusIndex, 0, tags.Length - 1)].Focus();
            else ArchiveSearchText.Focus();
        }
        return success;
    }
}
