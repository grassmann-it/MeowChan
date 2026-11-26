using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>How a quip is meant: a joke or a plain statement. Kept as data so a provider (or a later setting) can filter by it.</summary>
internal enum TaglineTone { Playful, Plain }

internal enum TaglineCategory { General, Release, Seasonal }

/// <summary>An inclusive, annually recurring date window. The end may fall in the next calendar year.</summary>
internal readonly record struct TaglineSeason((int Month, int Day) Start, (int Month, int Day) End)
{
    internal bool Includes(DateOnly date)
    {
        var day = (date.Month, date.Day);
        bool onOrAfterStart = day.CompareTo(Start) >= 0;
        bool onOrBeforeEnd = day.CompareTo(End) <= 0;
        return Start.CompareTo(End) <= 0 ? onOrAfterStart && onOrBeforeEnd : onOrAfterStart || onOrBeforeEnd;
    }
}

/// <summary>
/// One quip, ready to show: its stable <paramref name="Id"/> (what the "Fixed" setting stores), the version of MeowChan that introduced it
/// (<paramref name="Since"/>, what the release highlight compares with) and the <paramref name="Text"/> in the language it was asked for.
/// </summary>
internal sealed record TaglineEntry(string Id, Version Since, TaglineTone Tone, string Text,
    TaglineCategory Category = TaglineCategory.General, TaglineSeason? Season = null);

/// <summary>
/// A source of quips. The built-in one reads the language packs; a theme (or any later feature) can add its own through
/// <see cref="TaglineCatalog.Register"/> without touching the picker, the setting or the header. A provider returns only the entries it can
/// show in the active UI language (<see cref="Texts.Language"/>), including any shared English text chosen for that language.
/// </summary>
internal interface ITaglineProvider
{
    /// <summary>Names the source in diagnostics; entry ids must be unique across providers (the first provider registered wins a clash).</summary>
    string ProviderId { get; }

    /// <summary>The entries available in the active UI language. Called on the UI thread, at most when the pool is rebuilt.</summary>
    IReadOnlyList<TaglineEntry> Entries();
}

/// <summary>The quips that ship with MeowChan: ids, introducing version and tone here; the text of <c>x</c> is <c>shell.tagline.quip.x</c> in every pack.</summary>
internal sealed class BuiltInTaglineProvider : ITaglineProvider
{
    private sealed record Definition(string Id, Version Since, TaglineTone Tone,
        TaglineCategory Category = TaglineCategory.General, TaglineSeason? Season = null);

    private static readonly Version TaglinePackVersion = new(1, 1, 2);

    // Ids are persisted in Config.HeaderTaglineFixedId. Release eligibility is explicit and independent of Since and Tone.
    private static readonly Definition[] Definitions =
    [
        new("cats", new Version(1, 0, 0), TaglineTone.Playful),
        new("plain", new Version(1, 0, 0), TaglineTone.Plain),
        new("offline", new Version(1, 0, 0), TaglineTone.Plain),
        new("notfound", new Version(1, 0, 0), TaglineTone.Playful),
        new("hoard", new Version(1, 0, 0), TaglineTone.Playful),
        new("reader", new Version(1, 0, 0), TaglineTone.Plain),
        new("units", new Version(1, 0, 0), TaglineTone.Playful),
        new("booru", new Version(1, 1, 0), TaglineTone.Playful, TaglineCategory.Release),
        new("flashbang", new Version(1, 1, 0), TaglineTone.Playful, TaglineCategory.Release),
        new("workviews", new Version(1, 1, 0), TaglineTone.Playful, TaglineCategory.Release),
        new("tags", new Version(1, 1, 0), TaglineTone.Playful, TaglineCategory.Release),
        new("vibetested", new Version(1, 1, 0), TaglineTone.Playful, TaglineCategory.Release),
        new("pink", new Version(1, 1, 0), TaglineTone.Playful, TaglineCategory.Release),
        new("workflow", new Version(1, 1, 0), TaglineTone.Playful, TaglineCategory.Release),
        // Legacy self-deprecating quips retain their original Since values.
        new("jdownloader", new Version(1, 0, 0), TaglineTone.Playful),
        new("worksonmine", new Version(1, 0, 0), TaglineTone.Playful),
        new("noai", new Version(1, 0, 0), TaglineTone.Playful),
        new("whatbooru", new Version(1, 0, 0), TaglineTone.Playful),
        new("features", new Version(1, 0, 0), TaglineTone.Playful),
        // Legacy 1.1.1 additions: self-deprecating and UX lines, goals, and release quips.
        new("settingsforsettings", new Version(1, 0, 0), TaglineTone.Playful),
        new("tooltipflicker", new Version(1, 0, 0), TaglineTone.Playful),
        new("bugfeature", new Version(1, 0, 0), TaglineTone.Playful),
        new("greenhere", new Version(1, 0, 0), TaglineTone.Playful),
        new("pixelsbyhand", new Version(1, 0, 0), TaglineTone.Playful),
        new("scopecreep", new Version(1, 0, 0), TaglineTone.Playful),
        new("bugshide", new Version(1, 0, 0), TaglineTone.Playful),
        new("tidytoolbars", new Version(1, 0, 0), TaglineTone.Plain),
        new("keyboardfirst", new Version(1, 0, 0), TaglineTone.Plain),
        new("galleryreader", new Version(1, 0, 0), TaglineTone.Plain),
        new("evenspacing", new Version(1, 0, 0), TaglineTone.Playful),
        new("lightdark", new Version(1, 0, 0), TaglineTone.Plain),
        new("fewerclicks", new Version(1, 0, 0), TaglineTone.Plain),
        new("boorutabs", new Version(1, 1, 1), TaglineTone.Playful, TaglineCategory.Release),
        new("downloadall", new Version(1, 1, 1), TaglineTone.Playful, TaglineCategory.Release),
        new("threadswitch", new Version(1, 1, 1), TaglineTone.Playful, TaglineCategory.Release),
        new("taglinesfeature", new Version(1, 1, 1), TaglineTone.Playful, TaglineCategory.Release),
        new("yourarchive", new Version(1, 0, 0), TaglineTone.Plain),
        new("sortnothoard", new Version(1, 0, 0), TaglineTone.Plain),
        new("tabsbreed", new Version(1, 1, 2), TaglineTone.Playful),
        new("pawprint", new Version(1, 1, 2), TaglineTone.Playful),
        new("snowed", new Version(1, 1, 2), TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((12, 1), (1, 6))),
        new("trickorthread", new Version(1, 1, 2), TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((10, 24), (11, 1))),
        new("traypurr", TaglinePackVersion, TaglineTone.Playful),
        new("hiddenqueue", TaglinePackVersion, TaglineTone.Playful),
        new("traycurl", TaglinePackVersion, TaglineTone.Playful),
        new("toolbarperfect", TaglinePackVersion, TaglineTone.Playful),
        new("toolbarrewrite", TaglinePackVersion, TaglineTone.Playful),
        new("toolbarversion", TaglinePackVersion, TaglineTone.Playful),
        new("archiveemall", TaglinePackVersion, TaglineTone.Playful),
        new("zerolimit", TaglinePackVersion, TaglineTone.Playful),
        new("threadcowboy", TaglinePackVersion, TaglineTone.Playful),
        new("sitboy", TaglinePackVersion, TaglineTone.Playful),
        new("chevronseven", TaglinePackVersion, TaglineTone.Playful),
        new("over9000", TaglinePackVersion, TaglineTone.Playful),
        new("ninjaway", TaglinePackVersion, TaglineTone.Playful),
        new("nameofthemoon", TaglinePackVersion, TaglineTone.Playful),
        new("getinshinji", TaglinePackVersion, TaglineTone.Playful),
        new("thethreads", TaglinePackVersion, TaglineTone.Playful),
        new("threadripper", TaglinePackVersion, TaglineTone.Playful),
        new("riptag", TaglinePackVersion, TaglineTone.Playful),
        new("ripthread", TaglinePackVersion, TaglineTone.Playful),
        new("hangingthread", TaglinePackVersion, TaglineTone.Playful),
        new("lostthread", TaglinePackVersion, TaglineTone.Playful),
        new("threadbare", TaglinePackVersion, TaglineTone.Playful),
        new("curiosity", TaglinePackVersion, TaglineTone.Playful),
        new("ninelives", TaglinePackVersion, TaglineTone.Playful),
        new("knocksoff", TaglinePackVersion, TaglineTone.Playful),
        new("ifitfits", TaglinePackVersion, TaglineTone.Playful),
        new("threadevolved", TaglinePackVersion, TaglineTone.Playful),
        new("lasttab", TaglinePackVersion, TaglineTone.Playful),
        new("truthinhere", TaglinePackVersion, TaglineTone.Playful),
        new("jacktheripper", TaglinePackVersion, TaglineTone.Playful),
        new("pullthread", TaglinePackVersion, TaglineTone.Playful),
        new("threadlightly", TaglinePackVersion, TaglineTone.Playful),
        new("boardlostthread", TaglinePackVersion, TaglineTone.Playful),
        new("treatbag", TaglinePackVersion, TaglineTone.Playful),
        new("closeddoors", TaglinePackVersion, TaglineTone.Playful),
        new("heartsandstars", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("heartedstarred", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("roughhearts", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("fivestars", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("favefinal", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("firstclick", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("polishrelease", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("macugly", TaglinePackVersion, TaglineTone.Playful),
        new("gatekeeper", TaglinePackVersion, TaglineTone.Playful),
        new("cmdorctrl", TaglinePackVersion, TaglineTone.Playful),
        new("macthinking", TaglinePackVersion, TaglineTone.Playful),
        new("notnative", TaglinePackVersion, TaglineTone.Playful),
        new("macmenu", TaglinePackVersion, TaglineTone.Playful),
        new("blackcat", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((10, 24), (10, 31))),
        new("mirrorghost", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((10, 29), (10, 31))),
        new("giftbox", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((12, 18), (12, 26))),
        new("treefell", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((12, 18), (12, 26))),
        new("ribbon", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((12, 24), (12, 26))),
        new("newyearbacklog", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((12, 30), (1, 2))),
        new("underbed", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((12, 31), (1, 1))),
        new("mediaforall", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 1), (5, 1))),
        new("eighthours", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 1), (5, 1))),
        new("unpaidmods", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 1), (5, 1))),
        new("adalovelace", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((3, 8), (3, 8))),
        new("breadroses", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((3, 8), (3, 8))),
        new("bikeride", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((4, 19), (4, 19))),
        new("breathingthumbs", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((4, 19), (4, 19))),
        new("baselwheels", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((4, 19), (4, 19))),
        new("blazingfast", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((4, 20), (4, 20))),
        new("catsnacks", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((4, 20), (4, 20))),
        new("catnipresearch", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((4, 20), (4, 20))),
        new("cloudindependence", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((7, 4), (7, 4))),
        new("fireworksgif", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((7, 4), (7, 4))),
        new("vacuumindependence", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((7, 4), (7, 4))),
        new("unodostres", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 5), (5, 5))),
        new("pueblacover", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 5), (5, 5))),
        new("redheart", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((2, 14), (2, 14))),
        new("inmyfavorites", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((2, 10), (2, 14))),
        new("roseswilt", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((2, 14), (2, 14))),
        new("catday", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((8, 8), (8, 8))),
        new("catdaysearch", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((8, 8), (8, 8))),
        new("goodfolder", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((8, 8), (8, 8))),
        new("archivebewithyou", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 4), (5, 4))),
        new("notthethreads", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 4), (5, 4))),
        new("smallmoon", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 4), (5, 4))),
        new("pithreads", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((3, 14), (3, 14))),
        new("pistars", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((3, 14), (3, 14))),
        new("dontpanic", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 25), (5, 25))),
        new("forgottowel", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 25), (5, 25))),
        new("plunderboards", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((9, 19), (9, 19))),
        new("pirateparrot", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((9, 19), (9, 19))),
        new("treasuremisc", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((9, 19), (9, 19))),
        new("nocloud", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((9, 15), (9, 21))),
        new("linuxbirthday", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((8, 25), (8, 25))),
        new("worldbackup", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((3, 31), (3, 31))),
        new("lastyeardownload", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((12, 31), (1, 1))),
        new("queueovertime", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 1), (5, 1))),
        new("catsvote", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 1), (5, 1))),
    ];

    private readonly Func<string, string?> _lookup;

    /// <param name="lookup">Text of a key in the active language only, null when that language lacks it. Default: <see cref="Texts.Own"/>.</param>
    internal BuiltInTaglineProvider(Func<string, string?>? lookup = null) => _lookup = lookup ?? Texts.Own;

    public string ProviderId => "built-in";

    internal static IReadOnlyList<string> Ids => Definitions.Select(definition => definition.Id).ToArray();

    internal static string KeyFor(string id) => "shell.tagline.quip." + id;

    public IReadOnlyList<TaglineEntry> Entries()
    {
        List<TaglineEntry> result = new(Definitions.Length);
        foreach (Definition definition in Definitions)
        {
            string? text = TaglineCatalog.CleanText(_lookup(KeyFor(definition.Id)));
            if (text != null) result.Add(new TaglineEntry(definition.Id, definition.Since, definition.Tone, text, definition.Category, definition.Season));
        }
        return result;
    }
}

/// <summary>The pool of quips (built-in plus registered providers) and the small normalizers the settings use.</summary>
internal static class TaglineCatalog
{
    /// <summary>The classic quip: the default of the "Fixed" mode and the fallback when a stored id no longer exists.</summary>
    internal const string DefaultId = "cats";

    /// <summary>Fixed ceilings, documented and not settings: they only keep a hostile provider or settings file from costing memory. Longer ids read as unknown; longer texts are dropped.</summary>
    internal const int MaximumIdLength = 64, MaximumTextLength = 120;

    private static readonly object Gate = new();
    private static readonly List<ITaglineProvider> Extra = [];

    /// <summary>Adds a source of quips (a theme's fortunes). Returns what removes it again; registering the same instance twice is a no-op.</summary>
    internal static IDisposable Register(ITaglineProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
            if (!Extra.Contains(provider)) Extra.Add(provider);
        return new Registration(provider);
    }

    private sealed class Registration(ITaglineProvider provider) : IDisposable
    {
        public void Dispose() { lock (Gate) Extra.Remove(provider); }
    }

    /// <summary>
    /// Every quip available in the active language, sorted by id (a stable order, which the daily rule depends on). The built-in provider comes
    /// first and wins an id clash; a provider that throws or returns garbage is skipped, never fatal for the header.
    /// </summary>
    internal static IReadOnlyList<TaglineEntry> Available(IEnumerable<ITaglineProvider>? providers = null)
    {
        List<ITaglineProvider> sources;
        if (providers != null) sources = providers.ToList();
        else
        {
            sources = [new BuiltInTaglineProvider()];
            lock (Gate) sources.AddRange(Extra);
        }
        Dictionary<string, TaglineEntry> byId = new(StringComparer.Ordinal);
        foreach (ITaglineProvider source in sources)
        {
            IReadOnlyList<TaglineEntry> entries;
            try { entries = source.Entries(); }
            catch (Exception) { continue; }
            foreach (TaglineEntry entry in entries ?? [])
            {
                if (entry == null || !IsValidId(entry.Id) || entry.Since == null) continue;
                string? text = CleanText(entry.Text);
                if (text != null) byId.TryAdd(entry.Id, entry with { Text = text });
            }
        }
        return byId.Values.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Trimmed text, or null when empty, too long or containing a control character (a quip is one plain line).</summary>
    internal static string? CleanText(string? text)
    {
        string? trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaximumTextLength || trimmed.Any(char.IsControl)) return null;
        return trimmed;
    }

    internal static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaximumIdLength && id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-' or '/');

    /// <summary>A stored id as the setting keeps it: unchanged when well formed, else the classic one.</summary>
    internal static string NormalizeId(string? value)
    {
        string? trimmed = value?.Trim();
        return IsValidId(trimmed) ? trimmed! : DefaultId;
    }

    /// <summary>"1.1.0", "1.1.0+abc", "1.1.0-beta.2" and "1.1" read as 1.1.0; anything else is no version.</summary>
    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string core = text.Trim().TrimStart('v', 'V');
        int cut = core.IndexOfAny(['+', '-']);
        if (cut >= 0) core = core[..cut];
        if (!Version.TryParse(core, out Version? parsed)) return null;
        return new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
    }

    /// <summary>The stored form of a version: major.minor.build, or empty.</summary>
    internal static string NormalizeVersion(string? value) => ParseVersion(value)?.ToString(3) ?? string.Empty;
}
