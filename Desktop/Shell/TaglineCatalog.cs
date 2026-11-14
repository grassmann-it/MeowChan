using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>How a quip is meant: a joke or a plain statement. Kept as data so a provider (or a later setting) can filter by it.</summary>
internal enum TaglineTone { Playful, Plain }

/// <summary>
/// One quip, ready to show: its stable <paramref name="Id"/> (what the "Fixed" setting stores), the version of MeowChan that introduced it
/// (<paramref name="Since"/>, what the release highlight compares with) and the <paramref name="Text"/> in the language it was asked for.
/// </summary>
internal sealed record TaglineEntry(string Id, Version Since, TaglineTone Tone, string Text);

/// <summary>
/// A source of quips. The built-in one reads the language packs; a theme (or any later feature) can add its own through
/// <see cref="TaglineCatalog.Register"/> without touching the picker, the setting or the header. A provider returns only the entries it can
/// show in the active UI language (<see cref="Texts.Language"/>), so no English quip ever stands behind a translated description.
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
    private sealed record Definition(string Id, Version Since, TaglineTone Tone);

    // The release quips of 1.1.0 are the ones a first start after the update shows. Ids are persisted (Config.HeaderTaglineFixedId): never rename one.
    private static readonly Definition[] Definitions =
    [
        new("cats", new Version(1, 0, 0), TaglineTone.Playful),
        new("plain", new Version(1, 0, 0), TaglineTone.Plain),
        new("offline", new Version(1, 0, 0), TaglineTone.Plain),
        new("notfound", new Version(1, 0, 0), TaglineTone.Playful),
        new("hoard", new Version(1, 0, 0), TaglineTone.Playful),
        new("reader", new Version(1, 0, 0), TaglineTone.Plain),
        new("units", new Version(1, 0, 0), TaglineTone.Playful),
        new("tray", new Version(1, 0, 0), TaglineTone.Plain),
        new("booru", new Version(1, 1, 0), TaglineTone.Playful),
        new("flashbang", new Version(1, 1, 0), TaglineTone.Playful),
        new("workviews", new Version(1, 1, 0), TaglineTone.Playful),
        new("tags", new Version(1, 1, 0), TaglineTone.Playful),
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
            if (text != null) result.Add(new TaglineEntry(definition.Id, definition.Since, definition.Tone, text));
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
