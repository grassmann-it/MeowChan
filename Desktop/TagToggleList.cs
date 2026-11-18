using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>How many of the dialog's files carry an own tag: every one, some of them, or none.</summary>
internal enum TagToggleState { None, Some, All }

/// <summary>The labelled parts of the tag list. Each tag appears in exactly one of them.</summary>
internal enum TagListSection { Assigned, Frequent, Other, Matching }

/// <summary>
/// Order of the tag list: alphabetical (with the most used tags in their own section on top) or most used first. The store
/// keeps no time of use, so there is no "recently used" order.
/// </summary>
internal enum TagListSort { Alphabetical, Uses }

/// <summary>
/// How the dialog's files carry each own tag: <see cref="Total"/> distinct files (1 for a single medium), and per tag
/// how many of them carry it. A tag missing from <see cref="Carriers"/> is on none of them.
/// </summary>
internal sealed record TagCoverage(IReadOnlyDictionary<TagId, int> Carriers, int Total)
{
    internal static readonly TagCoverage Empty = new(new Dictionary<TagId, int>(), 0);

    internal TagToggleState StateOf(TagId tag)
    {
        int carriers = Carriers.GetValueOrDefault(tag);
        return carriers <= 0 || Total <= 0 ? TagToggleState.None : carriers >= Total ? TagToggleState.All : TagToggleState.Some;
    }
}

/// <summary>
/// One row of the tag list: a section label (not focusable, never toggled) or one own tag with its state. A tag row
/// changes in place when its state changes, so the list never rebuilds for a toggle.
/// </summary>
internal sealed class TagToggleRow : INotifyPropertyChanged
{
    private TagToggleRow(TagListSection section, PrivateTagDefinition? tag, bool many, TagListSort sort = TagListSort.Alphabetical)
    {
        Section = section;
        Tag = tag;
        Many = many;
        Sort = sort;
    }

    internal static TagToggleRow Header(TagListSection section, bool many, TagListSort sort = TagListSort.Alphabetical) => new(section, null, many, sort);
    internal static TagToggleRow For(PrivateTagDefinition tag, bool many) => new(TagListSection.Other, tag ?? throw new ArgumentNullException(nameof(tag)), many);

    internal bool IsHeader => Tag == null;
    /// <summary>The section a label row names; meaningless for a tag row.</summary>
    internal TagListSection Section { get; }
    internal PrivateTagDefinition? Tag { get; }
    /// <summary>True in the selection dialog: several files, tri-state rows.</summary>
    internal bool Many { get; }
    /// <summary>The order a section label was laid out in (its text says it).</summary>
    internal TagListSort Sort { get; }
    internal TagToggleState State { get; private set; }
    internal int Carriers { get; private set; }
    internal int Total { get; private set; }
    internal int Uses { get; private set; }

    /// <summary>CheckBox value: on all, on none, or the indeterminate "some".</summary>
    internal bool? IsChecked => State switch { TagToggleState.All => true, TagToggleState.None => false, _ => null };

    internal string Label => Tag?.Name.Display ?? HeaderText(Section, Many, Sort);

    /// <summary>"{carriers} of {total}" in the selection dialog; empty for a single medium and for labels.</summary>
    internal string CountText => IsHeader || !Many ? string.Empty : Texts.T("tags.dialog.carriers", Carriers, Total);

    /// <summary>How often the tag is used (threads plus distinct media files, as in the tag field's suggestions).</summary>
    internal string UsesText => IsHeader ? string.Empty : Uses.ToString(Texts.Culture);

    internal string AccessibleName => Tag is null ? Label : StateName + ", "
        + (Uses > 0 ? Texts.Count("tags.dialog.usesName", Uses) : Texts.T("tags.dialog.unusedName"));

    private string StateName => Tag is not { } tag ? Label : (Many, State) switch
    {
        (false, TagToggleState.All) => Texts.T("tags.dialog.row.on", tag.Name.Display),
        (false, _) => Texts.T("tags.dialog.row.off", tag.Name.Display),
        (true, TagToggleState.All) => Texts.Count("tags.dialog.row.all", Total, tag.Name.Display, Total),
        (true, TagToggleState.Some) => Texts.T("tags.dialog.row.some", tag.Name.Display, Carriers, Total),
        _ => Texts.T("tags.dialog.row.none", tag.Name.Display)
    };

    internal string ToolTipText => Tag is not { } tag ? Label
        : Uses > 0 ? Texts.T("tags.suggest.used", tag.Name.Display, Uses) : Texts.T("tags.suggest.unused", tag.Name.Display);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Takes the new state; raises a change only when something shown differs. Returns whether it did.</summary>
    internal bool Update(TagToggleState state, int carriers, int total, int uses)
    {
        if (State == state && Carriers == carriers && Total == total && Uses == uses) return false;
        State = state; Carriers = carriers; Total = total; Uses = uses;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        return true;
    }

    /// <summary>The language changed: every text of the row is new.</summary>
    internal void Relocalize() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    internal static string HeaderText(TagListSection section, bool many, TagListSort sort = TagListSort.Alphabetical) => section switch
    {
        TagListSection.Assigned => Texts.T(many ? "tags.dialog.section.assignedMany" : "tags.media.assigned"),
        TagListSection.Frequent => Texts.T("tags.dialog.section.frequent"),
        TagListSection.Matching => Texts.T("tags.dialog.section.matching"),
        _ => Texts.T(sort == TagListSort.Uses ? "tags.dialog.section.otherByUse" : "tags.dialog.section.other")
    };

    /// <summary>
    /// Most used first, then the given alphabetical rank: the "most used" order of every tag list (dialog rows, tag
    /// manager), so all of them agree with the tag field's suggestions.
    /// </summary>
    internal static IEnumerable<T> OrderByUses<T>(IEnumerable<T> items, Func<T, int> uses, Func<T, string> name)
    {
        StringComparer names = StringComparer.Create(Texts.Culture, ignoreCase: true);
        return items.OrderByDescending(uses).ThenBy(name, names).ThenBy(name, StringComparer.Ordinal);
    }
}

/// <summary>
/// The rows of the tag dialogs' one list. Without a filter: the tags on the files, then the most used tags, then every other
/// tag alphabetically; or, sorted by use, the tags on the files and then every other tag, most used first. With a filter:
/// the matching tags on the files, then the other matching tags, in the chosen order. A toggle
/// only updates the affected row (<see cref="Apply"/>); the sections are laid out again only for a new filter, a changed
/// vocabulary or a new language, so a row never jumps away from the pointer or the keyboard. No UI and no storage.
/// </summary>
internal sealed class TagToggleList
{
    internal const int FrequentLimit = 8;
    private readonly bool _many;
    private readonly Dictionary<TagId, TagToggleRow> _byId = new();
    private PrivateTagStoreSnapshot? _ordered;
    private string? _orderCulture;
    private PrivateTagDefinition[] _alphabetical = Array.Empty<PrivateTagDefinition>();
    private (TagId Id, string Name)[] _vocabulary = Array.Empty<(TagId, string)>();

    private int[] _byUses = Array.Empty<int>();
    private PrivateTagStoreSnapshot? _usesOrdered;

    internal TagToggleList(bool many) => _many = many;

    internal TagListSort Sort { get; private set; }

    internal IReadOnlyList<TagToggleRow> Rows { get; private set; } = Array.Empty<TagToggleRow>();
    internal string Filter { get; private set; } = string.Empty;
    /// <summary>All own tags of the shown snapshot, whatever the filter.</summary>
    internal int TagCount => _vocabulary.Length;
    internal PrivateTagStoreSnapshot? Snapshot { get; private set; }
    internal TagCoverage Coverage { get; private set; } = TagCoverage.Empty;

    internal TagToggleRow? RowOf(TagId tag) => _byId.TryGetValue(tag, out TagToggleRow? row) && Rows.Contains(row) ? row : null;

    /// <summary>Tags on every file, and tags on only some of them.</summary>
    internal (int All, int Some) Counts()
    {
        int all = 0, some = 0;
        foreach (int carriers in Coverage.Carriers.Values)
        {
            if (carriers <= 0) continue;
            if (carriers >= Coverage.Total) all++; else some++;
        }
        return (all, some);
    }

    /// <summary>Lays the sections out again for the snapshot, coverage and filter.</summary>
    internal void Build(PrivateTagStoreSnapshot? snapshot, TagCoverage coverage, string? filter, TagListSort sort = TagListSort.Alphabetical)
    {
        Snapshot = snapshot;
        Sort = sort;
        Coverage = coverage ?? TagCoverage.Empty;
        Filter = filter ?? string.Empty;
        if (snapshot == null)
        {
            _vocabulary = Array.Empty<(TagId, string)>();
            _alphabetical = Array.Empty<PrivateTagDefinition>();
            _ordered = null;
            _byId.Clear();
            Rows = Array.Empty<TagToggleRow>();
            return;
        }
        Order(snapshot);
        IReadOnlyDictionary<TagId, int> uses = TagSuggestionIndex.UsesOf(snapshot);
        string? key = TagSuggestionIndex.QueryKey(Filter);
        // Rows are kept per tag across layouts, so a state survives a new filter without a new object.
        HashSet<TagId> present = new(_vocabulary.Length);
        foreach ((TagId id, _) in _vocabulary) present.Add(id);
        foreach (TagId gone in _byId.Keys.Where(id => !present.Contains(id)).ToArray()) _byId.Remove(gone);

        List<TagToggleRow> rows = new(_alphabetical.Length + 3);
        HashSet<TagId> placed = new();
        void Section(TagListSection section, IEnumerable<PrivateTagDefinition> tags)
        {
            int header = rows.Count;
            foreach (PrivateTagDefinition tag in tags)
            {
                if (!placed.Add(tag.Id)) continue;
                if (rows.Count == header) rows.Add(TagToggleRow.Header(section, _many, sort));
                rows.Add(Row(tag, uses));
            }
        }
        bool Matches(PrivateTagDefinition tag) => key == null || tag.Name.Key.Contains(key, StringComparison.Ordinal);

        IEnumerable<PrivateTagDefinition> ordered = sort == TagListSort.Uses ? ByUses(snapshot, uses) : _alphabetical;
        Section(TagListSection.Assigned, ordered.Where(tag => Matches(tag) && Coverage.StateOf(tag.Id) != TagToggleState.None));
        if (key != null) Section(TagListSection.Matching, ordered.Where(Matches));
        else if (sort == TagListSort.Uses) Section(TagListSection.Other, ordered);
        else
        {
            // The most used tags (only used ones, so this sorts few) above the full A–Z list.
            Section(TagListSection.Frequent, ByUses(snapshot, uses).Where(tag => uses.GetValueOrDefault(tag.Id) > 0 && !placed.Contains(tag.Id)).Take(FrequentLimit));
            Section(TagListSection.Other, _alphabetical);
        }
        Rows = rows;
    }

    private TagToggleRow Row(PrivateTagDefinition tag, IReadOnlyDictionary<TagId, int> uses)
    {
        if (!_byId.TryGetValue(tag.Id, out TagToggleRow? row) || row.Tag != tag)
            _byId[tag.Id] = row = TagToggleRow.For(tag, _many);
        row.Update(Coverage.StateOf(tag.Id), Coverage.Carriers.GetValueOrDefault(tag.Id), Coverage.Total, uses.GetValueOrDefault(tag.Id));
        return row;
    }

    // Uses change with every write, so this order belongs to one snapshot; ties keep the alphabetical order.
    private IEnumerable<PrivateTagDefinition> ByUses(PrivateTagStoreSnapshot snapshot, IReadOnlyDictionary<TagId, int> uses)
    {
        if (!ReferenceEquals(_usesOrdered, snapshot) || _byUses.Length != _alphabetical.Length)
        {
            int[] order = Enumerable.Range(0, _alphabetical.Length).ToArray();
            int[] counts = _alphabetical.Select(tag => uses.GetValueOrDefault(tag.Id)).ToArray();
            Array.Sort(order, (x, y) => counts[x] != counts[y] ? counts[y].CompareTo(counts[x]) : x.CompareTo(y));
            _byUses = order;
            _usesOrdered = snapshot;
        }
        return _byUses.Select(at => _alphabetical[at]);
    }

    private void Order(PrivateTagStoreSnapshot snapshot)
    {
        string culture = Texts.Culture.Name;
        if (ReferenceEquals(_ordered, snapshot) && _orderCulture == culture) return;
        StringComparer names = StringComparer.Create(Texts.Culture, ignoreCase: true);
        _alphabetical = snapshot.Tags.OrderBy(tag => tag.Name.Display, names).ThenBy(tag => tag.Name.Key, StringComparer.Ordinal).ToArray();
        _vocabulary = snapshot.Tags.Select(tag => (tag.Id, tag.Name.Display)).ToArray();
        _ordered = snapshot;
        _orderCulture = culture;
    }

    /// <summary>The language changed: every kept row has new texts.</summary>
    internal void Relocalize() { foreach (TagToggleRow row in _byId.Values) row.Relocalize(); }

    /// <summary>True when the snapshot names other tags than the laid-out one (added, removed or renamed).</summary>
    internal bool VocabularyChanged(PrivateTagStoreSnapshot? snapshot)
    {
        if (snapshot == null || Snapshot == null) return snapshot != Snapshot;
        if (ReferenceEquals(snapshot, Snapshot) || ReferenceEquals(snapshot.Tags, Snapshot.Tags)) return false;
        IReadOnlyList<PrivateTagDefinition> tags = snapshot.Tags;
        if (tags.Count != _vocabulary.Length) return true;
        for (int at = 0; at < tags.Count; at++)
            if (tags[at].Id != _vocabulary[at].Id || !string.Equals(tags[at].Name.Display, _vocabulary[at].Name, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Takes a new snapshot and coverage of the same vocabulary: only rows whose state changed raise a change. Returns
    /// how many did, or -1 when the vocabulary changed and the caller must <see cref="Build"/> instead.
    /// </summary>
    internal int Apply(PrivateTagStoreSnapshot? snapshot, TagCoverage coverage)
    {
        if (VocabularyChanged(snapshot)) return -1;
        coverage ??= TagCoverage.Empty;
        if (snapshot == null) { Coverage = coverage; return 0; }
        IReadOnlyDictionary<TagId, int> uses = TagSuggestionIndex.UsesOf(snapshot);
        Snapshot = snapshot;
        Coverage = coverage;
        int changed = 0;
        foreach (TagToggleRow row in Rows)
            if (row.Tag is { } tag && row.Update(coverage.StateOf(tag.Id), coverage.Carriers.GetValueOrDefault(tag.Id), coverage.Total, uses.GetValueOrDefault(tag.Id)))
                changed++;
        return changed;
    }

    /// <summary>
    /// The next tag row from <paramref name="from"/> in <paramref name="step"/> direction, skipping section labels; the
    /// first or last tag row for a start outside the list. Stays put at either end.
    /// </summary>
    internal static int Step(IReadOnlyList<TagToggleRow> rows, int from, int step)
    {
        if (rows.Count == 0) return -1;
        if (from < 0 || from >= rows.Count) return step >= 0 ? Edge(rows, first: true) : Edge(rows, first: false);
        for (int at = from + Math.Sign(step); at >= 0 && at < rows.Count; at += Math.Sign(step))
            if (!rows[at].IsHeader) return at;
        return rows[from].IsHeader ? Edge(rows, first: step < 0) : from;
    }

    internal static int Edge(IReadOnlyList<TagToggleRow> rows, bool first)
    {
        if (first) { for (int at = 0; at < rows.Count; at++) if (!rows[at].IsHeader) return at; }
        else for (int at = rows.Count - 1; at >= 0; at--) if (!rows[at].IsHeader) return at;
        return -1;
    }
}
