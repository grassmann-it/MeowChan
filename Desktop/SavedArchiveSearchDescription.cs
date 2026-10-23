using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal static class SavedArchiveSearchDescription
{
    internal static string Describe(ArchiveSearchSpec spec)
    {
        List<string> fields = new();
        void Add(string key, string value) => fields.Add(Texts.T(key) + ": " + value);
        if (spec.Text.Length > 0) Add("savedSearch.text", spec.Text);
        if (spec.Sources.Count > 0) Add("savedSearch.sources", string.Join(", ", spec.Sources));
        if (spec.Boards.Count > 0) Add("savedSearch.boards", string.Join(", ", spec.Boards.Select(b => (b.Source ?? "*") + "/" + b.Board)));
        if (spec.SourceStates.Count > 0) Add("savedSearch.states", string.Join(", ", spec.SourceStates.Select(s => Texts.T("savedSearch.state." + s))));
        if (spec.CaptureEnabled is bool capture) Add("savedSearch.capture", Texts.T(capture ? "savedSearch.yes" : "savedSearch.no"));
        if (spec.HasPosts is bool posts) Add("savedSearch.posts", Texts.T(posts ? "savedSearch.yes" : "savedSearch.no"));
        Add("savedSearch.work", Texts.T("savedSearch.work." + spec.WorkFilter));
        Add("savedSearch.sort", Texts.T("savedSearch.sort." + spec.Sort));
        if (spec.DateRange is { } date)
            Add("savedSearch.date", (date.StartDate?.ToString("yyyy-MM-dd") ?? "…") + " – " + (date.EndDate?.ToString("yyyy-MM-dd") ?? "…") + " · " + date.TimeZoneId);
        return string.Join(" · ", fields);
    }
}
