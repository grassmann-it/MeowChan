using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal static class SavedArchiveSearchDescription
{
    internal static string Describe(ArchiveSearchSpec spec, Func<Guid, string?>? tagName = null)
    {
        List<string> fields = new();
        void Add(string key, string value) => fields.Add(Texts.T(key) + ": " + value);
        if (spec.Text.Length > 0) Add("savedSearch.text", spec.Text);
        if (spec.Scope != ArchiveSearchScope.AllPosts) Add("archive.filter.textScope", ArchivePage.TextScopeLabel(spec.Scope));
        if (spec.Sources.Count > 0) Add("savedSearch.sources", string.Join(", ", spec.Sources));
        if (spec.Boards.Count > 0) Add("savedSearch.boards", string.Join(", ", spec.Boards.Select(b => (b.Source ?? "*") + "/" + b.Board)));
        if (spec.SourceStates.Count > 0) Add("savedSearch.states", string.Join(", ", spec.SourceStates.Select(s => Texts.T("savedSearch.state." + s))));
        if (spec.CaptureEnabled is bool capture) Add("savedSearch.capture", Texts.T(capture ? "savedSearch.yes" : "savedSearch.no"));
        if (spec.HasPosts is bool posts) Add("savedSearch.posts", Texts.T(posts ? "savedSearch.yes" : "savedSearch.no"));
        Add("savedSearch.work", Texts.T("savedSearch.work." + spec.WorkFilter));
        Add("savedSearch.sort", Texts.T("savedSearch.sort." + spec.Sort));
        if (spec.DateRange is { } date)
            Add("savedSearch.date", (date.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "…") + " – " + (date.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "…") + " · " + date.TimeZoneId);
        if (spec.Tags is { } tags)
        {
            // References are IDs; a tag that no longer exists is named as deleted, never left out.
            string Names(IEnumerable<Guid> ids) => string.Join(", ", ids.Select(id => tagName?.Invoke(id) ?? Texts.T("tags.filter.deleted")));
            if (tags.Include.Count > 0) Add(tags.Match == ArchiveTagMatch.Any ? "tags.filter.describeAny" : "tags.filter.describeAll", Names(tags.Include));
            if (tags.Exclude.Count > 0) Add("tags.filter.describeExclude", Names(tags.Exclude));
        }
        return string.Join(" · ", fields);
    }
}
