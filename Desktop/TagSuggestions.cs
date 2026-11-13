using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>How a typed name matched a tag: the whole name, its start, the start of a later word, or anywhere.</summary>
internal enum TagMatchKind { Exact, Prefix, WordPrefix, Substring }

/// <summary>One ranked match. <see cref="Id"/> is the own tag; null for a provider source tag, which is only a name.</summary>
internal sealed record TagSuggestion(PrivateTagName Name, TagId? Id, int Uses, TagMatchKind Kind);

/// <summary>
/// In-memory ranking of tag names for the tag fields, built once and matched per keystroke without any read.
/// Ranking: the exact name, then its start, then the start of a later word, then anywhere; within each, more uses first, then
/// alphabetical. Matching uses the own-tag comparison key (<see cref="PrivateTagName.Key"/>), so case and Unicode
/// composition never hide a name. Own tags are indexed per published tag snapshot (a tag change publishes a new
/// snapshot, so nothing stale is served); provider source tags get their own index, never mixed with own tags.
/// No UI, no storage.
/// </summary>
internal sealed class TagSuggestionIndex
{
    /// <summary>Own tags shown at most, then provider source tags below them, then the one "New tag" row: the list's cap.</summary>
    internal const int OwnLimit = 8, SourceLimit = 4, VisibleLimit = OwnLimit + SourceLimit + 1;
    private static readonly ConditionalWeakTable<PrivateTagStoreSnapshot, TagSuggestionIndex> Cache = new();
    private readonly TagSuggestion[] _ranked;
    private readonly Dictionary<string, TagSuggestion> _byKey;
    private HashSet<string>? _keys;

    private TagSuggestionIndex(IEnumerable<TagSuggestion> items)
    {
        _ranked = items.OrderByDescending(item => item.Uses)
            .ThenBy(item => item.Name.Display, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(item => item.Name.Key, StringComparer.Ordinal)
            .ToArray();
        _byKey = new Dictionary<string, TagSuggestion>(StringComparer.Ordinal);
        foreach (TagSuggestion item in _ranked) _byKey.TryAdd(item.Name.Key, item);
    }

    internal TagSuggestionIndex(IEnumerable<(PrivateTagDefinition Tag, int Uses)> tags)
        : this(tags.Select(item => new TagSuggestion(item.Tag.Name, item.Tag.Id, Math.Max(0, item.Uses), TagMatchKind.Prefix))) { }

    internal int Count => _ranked.Length;
    /// <summary>Comparison keys of every indexed name, to keep a source tag that already is an own tag out of the list.</summary>
    internal IReadOnlySet<string> Keys => _keys ??= _byKey.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>The most used names, for an explicitly opened list on an empty field.</summary>
    internal IReadOnlyList<TagSuggestion> MostUsed(IReadOnlySet<TagId>? excluded, int limit) =>
        _ranked.Where(item => item.Id is not { } id || excluded?.Contains(id) != true).Take(Math.Max(0, limit)).ToArray();

    internal static TagSuggestionIndex For(PrivateTagStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Cache.GetValue(snapshot, Build);
    }

    private static TagSuggestionIndex Build(PrivateTagStoreSnapshot snapshot)
    {
        IReadOnlyDictionary<TagId, int> uses = UsesOf(snapshot);
        return new TagSuggestionIndex(snapshot.Tags.Select(tag => (tag, uses.GetValueOrDefault(tag.Id))));
    }

    private static readonly ConditionalWeakTable<PrivateTagStoreSnapshot, IReadOnlyDictionary<TagId, int>> UsesCache = new();

    /// <summary>
    /// Uses per own tag in a snapshot: distinct threads plus distinct media files; unused tags are absent. One pass over the
    /// assignments, no sorting, cached per snapshot (the tag dialogs ask after every write).
    /// </summary>
    internal static IReadOnlyDictionary<TagId, int> UsesOf(PrivateTagStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return UsesCache.GetValue(snapshot, static current =>
        {
            Dictionary<TagId, int> uses = current.ThreadAssignments.GroupBy(item => (TagId)item.TagId)
                .ToDictionary(group => group.Key, group => group.Select(item => item.Thread).Distinct().Count());
            foreach ((TagId tag, int media) in current.DistinctMediaCounts()) uses[tag] = uses.GetValueOrDefault(tag) + media;
            return uses;
        });
    }

    /// <summary>
    /// Provider source tag names with the number of saved posts that carry them. Names that cannot be own tag names
    /// are left out; spellings that only differ in case collapse to the most used one.
    /// </summary>
    internal static TagSuggestionIndex FromSourceTags(IEnumerable<(string Name, int Posts)> names)
    {
        Dictionary<string, (PrivateTagName Name, int Best, int Total)> byKey = new(StringComparer.Ordinal);
        foreach ((string raw, int count) in names)
        {
            if (!PrivateTagName.TryCreate(raw, out PrivateTagName? name, out _) || name == null) continue;
            int posts = Math.Max(0, count);
            byKey[name.Key] = byKey.TryGetValue(name.Key, out var known)
                ? (posts > known.Best ? name : known.Name, Math.Max(posts, known.Best), known.Total + posts)
                : (name, posts, posts);
        }
        return new TagSuggestionIndex(byKey.Values.Select(item => new TagSuggestion(item.Name, null, item.Total, TagMatchKind.Prefix)));
    }

    /// <summary>Source tags of the saved Danbooru posts of a catalog: one count per post, whatever the category.</summary>
    internal static TagSuggestionIndex FromCatalog(BooruCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Dictionary<string, int> posts = new(StringComparer.Ordinal);
        foreach (BooruPostRecord record in catalog.Posts)
        {
            if (record.SourceId != BooruMediaIdentity.Source) continue;
            foreach (string tag in record.Post.Tags.Select(item => item.Name).Distinct(StringComparer.Ordinal))
                posts[tag] = posts.GetValueOrDefault(tag) + 1;
        }
        return FromSourceTags(posts.Select(item => (item.Key, item.Value)));
    }

    /// <summary>
    /// The comparison key of typed text, the same as <see cref="PrivateTagName.Key"/> for a valid name. Null when the
    /// text cannot match any tag (blank, too long or not valid Unicode).
    /// </summary>
    internal static string? QueryKey(string? text)
    {
        if (text == null || text.Length > PrivateTagName.MaximumUtf16CodeUnits) return null;
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        try { return trimmed.Normalize(NormalizationForm.FormC).ToUpperInvariant().Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// The best <paramref name="limit"/> names matching <paramref name="text"/> in rank order, without excluded own tags
    /// (already assigned) and excluded keys (names that already are own tags). Stops scanning once the first group is
    /// full, so a large index costs no more than needed.
    /// </summary>
    internal IReadOnlyList<TagSuggestion> Match(string? text, IReadOnlySet<TagId>? excluded = null, int limit = int.MaxValue,
        IReadOnlySet<string>? excludedKeys = null)
    {
        string? key = QueryKey(text);
        if (key == null || limit <= 0) return Array.Empty<TagSuggestion>();
        bool Excluded(TagSuggestion item) => item.Id is { } id && excluded?.Contains(id) == true
            || excludedKeys?.Contains(item.Name.Key) == true;
        List<TagSuggestion>[] kinds = { new(), new(), new(), new() };
        // Names are unique per key, so the exact match is a lookup, not part of the scan.
        if (_byKey.TryGetValue(key, out TagSuggestion? exact) && !Excluded(exact)) kinds[0].Add(exact with { Kind = TagMatchKind.Exact });
        foreach (TagSuggestion item in _ranked)
        {
            if (Kind(item.Name.Key, key) is not { } kind || kind == TagMatchKind.Exact || kinds[(int)kind].Count >= limit || Excluded(item)) continue;
            kinds[(int)kind].Add(item with { Kind = kind });
            // Items arrive in rank order: once exact and prefix matches fill the list, nothing later can be shown.
            if (kinds[0].Count + kinds[1].Count >= limit) break;
        }
        return kinds.SelectMany(list => list).Take(limit).ToArray();
    }

    private static TagMatchKind? Kind(string name, string key)
    {
        int at = name.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        if (at == 0) return name.Length == key.Length ? TagMatchKind.Exact : TagMatchKind.Prefix;
        // A later word starts after anything that is not a letter or digit ("_", "-", "/", space, …).
        for (; at > 0; at = name.IndexOf(key, at + 1, StringComparison.Ordinal))
            if (!char.IsLetterOrDigit(name[at - 1])) return TagMatchKind.WordPrefix;
        return TagMatchKind.Substring;
    }

    /// <summary>
    /// A comma typed at the end of a draft adds the draft as a tag, unless an existing own tag continues the draft with
    /// that comma: names may contain commas, so "a, b" stays typeable when such a tag exists.
    /// </summary>
    internal bool CommitsOnComma(string? text)
    {
        string? key = QueryKey(text);
        if (key == null) return false;
        string continued = key + ",";
        return !_ranked.Any(item => item.Name.Key.StartsWith(continued, StringComparison.Ordinal));
    }

    /// <summary>
    /// Own tags first, then the provider's source tags as their own labelled group (never one mixed ranking), then
    /// "New tag" for a valid name that matches no tag exactly. An empty field lists nothing unless the list was opened
    /// explicitly (<paramref name="browse"/>), which shows the most used own tags.
    /// </summary>
    internal static IdentitySuggestion[] Combine(TagSuggestionIndex own, TagSuggestionIndex? sources, string? text,
        IReadOnlySet<TagId>? assigned, bool browse = false)
    {
        if (QueryKey(text) is not { } key)
            return browse && string.IsNullOrWhiteSpace(text) ? own.MostUsed(assigned, OwnLimit).Select(Present).ToArray() : Array.Empty<IdentitySuggestion>();
        IdentitySuggestion[] mine = own.Match(text, assigned, OwnLimit).Select(Present).ToArray();
        TagSuggestion[] sourceMatches = sources?.Match(text, null, SourceLimit, own.Keys).ToArray() ?? Array.Empty<TagSuggestion>();
        IdentitySuggestion[] provided = sourceMatches.Select(PresentSource).ToArray();
        if (provided.Length > 0) provided[0] = provided[0] with { GroupHeader = Texts.T("tags.suggest.sourceGroup") };
        // An exact own tag (even one already on the target) or source tag already is this name: nothing new to create.
        bool exact = own.Keys.Contains(key) || sourceMatches.Any(item => item.Kind == TagMatchKind.Exact);
        IdentitySuggestion[] create = !exact && PrivateTagName.TryCreate(text, out PrivateTagName? name, out _) && name != null
            ? new[] { new IdentitySuggestion(name.Display, CreateGroup, Texts.T("tags.suggest.create", name.Display),
                Texts.T("tags.suggest.createName", name.Display)) }
            : Array.Empty<IdentitySuggestion>();
        return mine.Concat(provided).Concat(create).ToArray();
    }

    internal static IdentitySuggestion Present(TagSuggestion item)
    {
        string name = item.Name.Display;
        return new IdentitySuggestion(name, null,
            item.Uses > 0 ? Texts.T("tags.suggest.label", name, item.Uses) : name,
            item.Uses > 0 ? Texts.T("tags.suggest.used", name, item.Uses) : Texts.T("tags.suggest.unused", name));
    }

    /// <summary>A provider tag row: the name is the value, and its label and accessible name say that choosing it creates an own tag.</summary>
    internal static IdentitySuggestion PresentSource(TagSuggestion item)
    {
        string name = item.Name.Display;
        return new IdentitySuggestion(name, SourceGroup, Texts.T("tags.suggest.sourceLabel", name, item.Uses),
            Texts.T("tags.suggest.sourceName", name, item.Uses));
    }

    /// <summary><see cref="IdentitySuggestion.Source"/> of a provider source tag row and of the "New tag" row.</summary>
    internal const string SourceGroup = "booru-source-tag", CreateGroup = "new-tag";
}

/// <summary>
/// Provider source tags for one tag field, read once in the background each time the field's owner opens, so typing
/// never waits for or repeats a catalog read. Unavailable (no Danbooru catalog, unreadable) simply means no source group.
/// </summary>
internal sealed class SourceTagSuggestions
{
    private int _generation;
    internal SourceTagSuggestions(Func<BooruCatalogSnapshot?>? read = null) => Read = read;
    /// <summary>The Danbooru catalog reader; null offers no source group.</summary>
    internal Func<BooruCatalogSnapshot?>? Read { get; set; }
    internal TagSuggestionIndex? Current { get; private set; }
    /// <summary>Raised on the UI thread when a read completed with source tags.</summary>
    internal event Action? Loaded;
    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    internal void Load()
    {
        if (Read is not { } read) return;
        int generation = ++_generation;
        LoadTask = LoadAsync(generation, read);
    }

    private async Task LoadAsync(int generation, Func<BooruCatalogSnapshot?> read)
    {
        TagSuggestionIndex? index;
        try { index = await Task.Run(() => read() is { } catalog ? TagSuggestionIndex.FromCatalog(catalog) : null); }
        // Best effort: an unreadable or closing catalog only means no source group, never a faulted field.
        catch (Exception) { index = null; }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _generation) return;
            Current = index is { Count: > 0 } ? index : null;
            if (Current != null) Loaded?.Invoke();
        });
    }

    internal void End() { ++_generation; Current = null; Loaded = null; }
}

/// <summary>What a key in a tag field means to the bar or dialog around it.</summary>
internal enum TagFieldKey { None, CloseList, AcceptSuggestion, CommitTyped, CloseOwner }

/// <summary>
/// Wires an <see cref="IdentitySuggestionBox"/> as a tag field: own tags as suggestions (provider source tags in a
/// labelled group below them), Tab completes, Enter or a click on a suggestion adds it, a comma at the end adds the
/// draft. The owner keeps its own add path for free text, so accepting a source tag creates an own tag of that name.
/// </summary>
internal static class TagSuggestionInput
{
    /// <summary>
    /// The owner's view of a key in the field. Escape closes an open list first and only then the owner; Enter takes
    /// the highlighted suggestion when there is one, else adds the typed text. Modified keys stay with the editor.
    /// </summary>
    internal static TagFieldKey Route(Key key, KeyModifiers modifiers, bool listOpen, bool highlighted) =>
        modifiers != KeyModifiers.None ? TagFieldKey.None
        : key == Key.Escape ? (listOpen ? TagFieldKey.CloseList : TagFieldKey.CloseOwner)
        : key == Key.Enter ? (listOpen && highlighted ? TagFieldKey.AcceptSuggestion : TagFieldKey.CommitTyped)
        : TagFieldKey.None;

    internal static TagFieldKey Route(IdentitySuggestionBox box, KeyEventArgs e) =>
        Route(e.Key, e.KeyModifiers, box.IsDropDownOpen, box.HighlightedCandidate != null);

    /// <param name="snapshot">The tags the owner shows now; null while they are unavailable.</param>
    /// <param name="assigned">Tags the target already carries; they are not suggested.</param>
    /// <param name="commit">Adds the field's current text as a tag, exactly like the owner's Add button.</param>
    /// <param name="sources">Provider source tags, if the owner offers them; the owner decides when they load.</param>
    internal static void Attach(IdentitySuggestionBox box, Func<PrivateTagStoreSnapshot?> snapshot,
        Func<IReadOnlySet<TagId>> assigned, Action commit, SourceTagSuggestions? sources = null)
    {
        box.ReleaseUnselectedEnter = true;
        box.TabCompletes = true;
        box.VisibleLimit = TagSuggestionIndex.VisibleLimit;
        box.CountHelpKey = "tags.suggest.count";
        box.Candidates = (text, _) =>
        {
            PrivateTagStoreSnapshot? current = snapshot();
            if (current == null || !box.IsEffectivelyEnabled) return Task.FromResult(Array.Empty<IdentitySuggestion>());
            return Task.FromResult(TagSuggestionIndex.Combine(TagSuggestionIndex.For(current), sources?.Current, text, assigned(),
                browse: box.IsExplicitRequest));
        };
        if (sources != null) sources.Loaded += box.RefreshSuggestions;
        // Like the archive identity editors: add once the box has finished its selection update, and only while
        // that exact accepted value is still in the field.
        box.CandidateAccepted += item => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(IdentitySuggestions.CurrentSelection(box.Text, box.AcceptedCandidate), item)) commit();
        }, DispatcherPriority.Send);
        box.AddHandler(InputElement.TextInputEvent, (_, e) =>
        {
            if (e.Handled || e.Text != "," || !box.IsEffectivelyEnabled) return;
            string text = box.Text ?? string.Empty;
            TextBox? editor = box.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            bool atEnd = editor == null || (editor.CaretIndex >= text.Length && editor.SelectionStart == editor.SelectionEnd);
            PrivateTagStoreSnapshot? current = snapshot();
            if (!atEnd || current == null) return;
            // Like Enter: a highlighted row is taken; otherwise the draft, unless an own name continues it with a comma.
            if (box.HighlightedCandidate is { } highlighted) box.Text = highlighted.Value;
            else if (!TagSuggestionIndex.For(current).CommitsOnComma(text)) return;
            e.Handled = true;
            box.Dismiss();
            commit();
        }, RoutingStrategies.Tunnel);
    }

    internal static void Localize(IdentitySuggestionBox box)
    {
        box.PlaceholderText = Texts.T("tags.input");
        AutomationProperties.SetName(box, Texts.T("tags.input"));
        AutomationProperties.SetHelpText(box, Texts.T("tags.suggest.hint"));
        ToolTip.SetTip(box, Texts.T("tags.suggest.hint"));
    }
}
