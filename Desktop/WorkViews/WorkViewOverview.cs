using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.WorkViews;

/// <summary>
/// Read-only WV11 projection of the current policy, not another visibility policy or a custom-view editor.
/// No Avalonia/control access: the UI may supply an already captured id-to-accessible-name lookup.
/// </summary>
internal static class WorkViewOverview
{
    internal enum LabelSource { Command, RegisteredControl, HumanizedId }

    internal sealed record Row(WorkViewEntry Entry, string Label, LabelSource Source, string PageLabel, string? AlternativeLabel)
    {
        public string Id => Entry.Id;
        public bool Protected => Entry.Safety || WorkViewSetting.All.Any(view => Entry.For(view) == Presence.Required);
        public string ElementText => Label + (Protected ? "\n" + T(Entry.Safety ? "workview.overview.safety" : "workview.overview.required") : "");
        public string AccessibleName => PageLabel + ": " + ElementText;

        public string Cell(WorkView view)
        {
            Presence presence = Entry.For(view);
            // Required columns are still columns: On, not an invitation to hide them. The element's protection note explains it.
            if (Entry.Kind == WorkViewKind.Column)
                return T(presence switch
                {
                    Presence.Hidden => "workview.column.notOffered",
                    Presence.Overflow or Presence.Collapsed => "workview.column.off",
                    _ => "workview.column.on"
                });
            return presence switch
            {
                Presence.Required => T("workview.presence.required"),
                Presence.Direct => T("workview.presence.direct"),
                Presence.Overflow => T("workview.presence.overflow"),
                Presence.Collapsed => T("workview.presence.collapsed"),
                _ => string.IsNullOrWhiteSpace(AlternativeLabel) ? T("workview.overview.noAlternative")
                    : T("workview.presence.hidden", AlternativeLabel)
            };
        }

        public string CellName(WorkView view, bool active) => AccessibleName + "; " + ViewLabel(view, active) + ": " + Cell(view);
    }

    /// <summary>One row for EVERY policy entry, in policy page order. No row cap or pinned release count.</summary>
    internal static IReadOnlyList<Row> Build(Func<string, string?>? registeredName = null) =>
        WorkViewPolicy.Entries.OrderBy(entry => entry.Page).Select(entry => Project(entry, registeredName)).ToArray();

    internal static Row Project(WorkViewEntry entry, Func<string, string?>? registeredName = null)
    {
        var label = Label(entry, registeredName);
        string? alternative = null;
        if (!string.IsNullOrWhiteSpace(entry.Alternative))
        {
            if (WorkViewPolicy.Find(entry.Alternative) is { } other) alternative = Label(other, registeredName).Text;
            else if (CommandCatalog.TryGet(entry.Alternative, out _)) alternative = CommandCatalog.Label(entry.Alternative);
            // Never claim a made-up route is real. Existing policy tests enforce reachable alternatives.
        }
        return new(entry, label.Text, label.Source, PageLabel(entry.Page), alternative);
    }

    internal static (string Text, LabelSource Source) Label(WorkViewEntry entry, Func<string, string?>? registeredName = null)
    {
        if (entry.CommandId is { } command && CommandCatalog.TryGet(command, out _))
            return (CommandCatalog.Label(command), LabelSource.Command);
        string? name = registeredName?.Invoke(entry.Id);
        if (!string.IsNullOrWhiteSpace(name)) return (name.Trim(), LabelSource.RegisteredControl);
        string id = entry.Id[(entry.Id.IndexOf('.') + 1)..];
        string human = Humanize(id);
        return (entry.Kind == WorkViewKind.Column ? T("workview.overview.column", human) : human, LabelSource.HumanizedId);
    }

    /// <summary>Literal, case-insensitive all-word filter; ids remain searchable even when labels are translated.</summary>
    internal static bool Matches(Row row, WorkViewPage? page, string? text)
    {
        if (page.HasValue && row.Entry.Page != page.Value) return false;
        string searchable = string.Join(" ", row.Id, row.Label, row.PageLabel, row.AlternativeLabel, row.Entry.Alternative);
        return (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(word => searchable.Contains(word, StringComparison.CurrentCultureIgnoreCase));
    }

    internal static string ViewLabel(WorkView view, bool active = false)
    {
        string label = Texts.T("mode." + view.ToSetting().ToLowerInvariant());
        return active ? T("workview.overview.active", label) : label;
    }

    internal static string PageLabel(WorkViewPage page)
    {
        string key = "workview.overview.page." + page.ToString().ToLowerInvariant();
        string label = T(key);
        return label == key ? Humanize(page.ToString()) : label;
    }

    internal static string Humanize(string id)
    {
        string words = Regex.Replace(id, @"([A-Z]+)([A-Z][a-z])", "$1 $2");
        words = Regex.Replace(words, @"([a-z0-9])([A-Z])", "$1 $2");
        return string.Join(" / ", words.Split('.').Select(part =>
        {
            if (part == "*") return T("workview.overview.otherColumns");
            string phrase = Regex.Replace(part, @"[-_\s]+", " ").Trim();
            return phrase.Length == 0 ? phrase : char.ToUpperInvariant(phrase[0]) + phrase[1..];
        }));
    }

    private static string T(string key, params object?[] args) => WorkViewOverviewCatalog.T(key, args);
}
