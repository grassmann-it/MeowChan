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
    private static ClosableTag CreateFilterTag(string label, Action remove)
    {
        string accessible = Texts.T("archive.removeFilter", label);
        ClosableTag tag = new()
        {
            Content = new TextBlock { Text = label, MaxWidth = 235, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
            Command = new FilterTagCommand(remove), Focusable = true
        };
        ToolTip.SetTip(tag, accessible); AutomationProperties.SetName(tag, accessible);
        MeowChan.Desktop.Commands.CommandPresentation.SetId(tag, "archive.clear-filter");
        tag.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; remove(); } };
        return tag;
    }
    internal static IReadOnlyList<(string Field, string? Value, string Label)> AppliedFilterLabels(ArchiveSearchSpec spec)
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
        return tags.AsReadOnly();
    }
    private void UpdateAppliedFilters()
    {
        WrapPanel panel = Control<WrapPanel>("ArchiveAppliedFilters"); panel.Children.Clear();
        if (_appliedSearch == null) return;
        var tags = AppliedFilterLabels(_appliedSearch.Spec);
        for (int i = 0; i < tags.Count; i++)
        {
            var item = tags[i]; int index = i;
            panel.Children.Add(CreateFilterTag(item.Label, async () => await RemoveAppliedFilter(item.Field, item.Value, index)));
        }
    }
    private static string CanonicalField(ArchiveSearchSpec spec, string field) => ArchiveSearchSpecJson.Serialize(field switch
    {
        "text" => new ArchiveSearchSpec(text: spec.Text),
        "source" => new ArchiveSearchSpec(sources: spec.Sources),
        "board" => new ArchiveSearchSpec(boards: spec.Boards),
        "state" => new ArchiveSearchSpec(sourceStates: spec.SourceStates),
        "capture" => new ArchiveSearchSpec(captureEnabled: spec.CaptureEnabled),
        "posts" => new ArchiveSearchSpec(hasPosts: spec.HasPosts),
        "work" => new ArchiveSearchSpec(workFilter: spec.WorkFilter),
        "date" => new ArchiveSearchSpec(dateRange: spec.DateRange),
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
                "text" => new(text: Control<TextBox>("ArchiveSearchText").Text ?? ""),
                "source" when string.IsNullOrWhiteSpace(IdentityText("ArchiveSourceFilter")) => new(sources: _draftSources),
                "board" when string.IsNullOrWhiteSpace(IdentityText("ArchiveBoardFilter")) => new(boards: _draftBoards),
                "state" when Control<ComboBox>("ArchiveStateFilter").SelectedIndex <= 0 => new(sourceStates: _draftStates),
                "capture" => new(captureEnabled: Choice("ArchiveCaptureFilter")),
                "posts" => new(hasPosts: Choice("ArchivePostsFilter")),
                "work" => new(workFilter: WorkChoice(Control<ComboBox>("ArchiveWorkFilter").SelectedIndex)),
                "date" => new(dateRange: new ArchiveDateRange(Control<ArchiveDateEditor>("ArchiveStartDate").ParseRaw(false),
                    Control<ArchiveDateEditor>("ArchiveEndDate").ParseRaw(false), _draftTimeZone ?? "Etc/UTC")),
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
            case "text": Control<TextBox>("ArchiveSearchText").Text = next.Text; break;
            case "source": _draftSources.Clear(); _draftSources.AddRange(next.Sources); break;
            case "board": _draftBoards.Clear(); _draftBoards.AddRange(next.Boards); break;
            case "state": _draftStates.Clear(); _draftStates.AddRange(next.SourceStates); break;
            case "capture": Control<ComboBox>("ArchiveCaptureFilter").SelectedIndex = next.CaptureEnabled switch { true => 1, false => 2, _ => 0 }; break;
            case "posts": Control<ComboBox>("ArchivePostsFilter").SelectedIndex = next.HasPosts switch { true => 1, false => 2, _ => 0 }; break;
            case "work": Control<ComboBox>("ArchiveWorkFilter").SelectedIndex = WorkIndex(next.WorkFilter); break;
            case "date":
                _draftTimeZone = null;
                _writingDraft = true;
                try { Control<ArchiveDateEditor>("ArchiveStartDate").RawText = ""; Control<ArchiveDateEditor>("ArchiveEndDate").RawText = ""; }
                finally { _writingDraft = false; }
                UpdateTimeZoneLabel(); break;
        }
        DraftEdited(field);
        // A sourcechip removal must not revise an unrelated board draft/scope.
        if (field is "source" or "board" or "state") RenderDraftSets();
    }
    internal async Task<bool> RemoveAppliedFilter(string field, string? value = null, int focusIndex = 0)
    {
        if (_busy || _ended || !_available || _appliedSearch == null) return false;
        ArchiveSearchSpec applied = _appliedSearch.Spec;
        ArchiveSearchSpec next = CopySearch(applied, field, value);
        long revision = _editorRevisions.GetValueOrDefault(field);
        bool matches = DraftFieldMatches(applied, field);
        bool success = await LoadListAsync(NewListLoad(ArchiveSearchRequest.Create(next), afterCommit: () =>
        {
            if (matches && _editorRevisions.GetValueOrDefault(field) == revision) WriteSearchField(next, field);
        }));
        if (success)
        {
            var tags = Control<WrapPanel>("ArchiveAppliedFilters").Children;
            if (tags.Count > 0) tags[Math.Clamp(focusIndex, 0, tags.Count - 1)].Focus();
            else Control<TextBox>("ArchiveSearchText").Focus();
        }
        return success;
    }
}
