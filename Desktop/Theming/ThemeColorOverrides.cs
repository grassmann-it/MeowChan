using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Serilog;

namespace MeowChan.Desktop.Theming;

/// <summary>
/// TH2: the user's colour overrides (<c>Config.ThemeOverrides</c>), per base theme a map from a colour seed id to a colour.
/// Stored, exported and imported in exactly this shape:
/// <code>"ThemeOverrides": { "Light": { "Workspace.Accent": "#RRGGBB" }, "Dark": { "Workspace.Panel": "#AARRGGBB" } }</code>
/// Each variant object has the shape a future <c>theme.json</c> "seeds" object holds (TH5): <see cref="ThemeTokens.Seeds"/> ids
/// to <c>#RRGGBB</c> (opaque only, N9: the colour dialog offers no transparency and the contrast gate cannot judge it). Keep both
/// identical, so "save as theme" and a theme import are plain copies.
/// <para>
/// Immutable and always normalized: only seed ids, upper-case hex, an opaque <c>#FFRRGGBB</c> written as <c>#RRGGBB</c>. Reading
/// drops unknown or non-seed keys, malformed colours and translucent colours (with one log warning) instead of failing the profile. Related seeds a
/// role derives (<see cref="ThemeUserColors"/>) are not stored; they follow at apply time. Not contrast-checked here: the gate
/// is <see cref="ThemeCustomization.Preview"/> / <see cref="ThemeRuntime.ApplyOverrides"/>.
/// </para>
/// </summary>
public sealed class ThemeColorOverrides : IEquatable<ThemeColorOverrides>
{
    internal const string LightKey = "Light", DarkKey = "Dark";

    private readonly FrozenDictionary<string, string> _light, _dark;

    private ThemeColorOverrides(FrozenDictionary<string, string> light, FrozenDictionary<string, string> dark)
    {
        _light = light;
        _dark = dark;
    }

    /// <summary>No overrides: the built-in palettes (also "reset all").</summary>
    public static ThemeColorOverrides Empty { get; } =
        new(FrozenDictionary<string, string>.Empty, FrozenDictionary<string, string>.Empty);

    /// <summary>The Light overrides: seed id → normalized hex.</summary>
    public IReadOnlyDictionary<string, string> Light => _light;

    /// <summary>The Dark overrides: seed id → normalized hex.</summary>
    public IReadOnlyDictionary<string, string> Dark => _dark;

    public bool IsEmpty => _light.Count == 0 && _dark.Count == 0;

    internal IReadOnlyDictionary<string, string> For(ThemeTokenTheme theme) => theme switch
    {
        ThemeTokenTheme.Light => _light,
        ThemeTokenTheme.Dark => _dark,
        _ => throw new ArgumentOutOfRangeException(nameof(theme))
    };

    /// <summary>The overrides of one base as colours, the input of <see cref="ThemeCustomization.Preview"/>.</summary>
    internal IReadOnlyDictionary<string, Color> ColorsFor(ThemeTokenTheme theme) =>
        For(theme).ToDictionary(pair => pair.Key, pair => ThemeCustomization.ParseHex(pair.Value), StringComparer.Ordinal);

    /// <summary>The explicit override of a seed, or null.</summary>
    internal Color? Get(ThemeTokenTheme theme, string seed) =>
        For(theme).TryGetValue(seed, out string? hex) ? ThemeCustomization.ParseHex(hex) : null;

    /// <summary>
    /// Builds normalized overrides from raw maps (a settings file, an import, a theme's seeds). Entries that are not a colour seed
    /// or not a valid colour are left out and described in <paramref name="dropped"/> (key only; values are never echoed).
    /// </summary>
    internal static ThemeColorOverrides Create(IEnumerable<KeyValuePair<string, string?>>? light, IEnumerable<KeyValuePair<string, string?>>? dark,
        ICollection<string>? dropped = null)
    {
        FrozenDictionary<string, string> Normalize(IEnumerable<KeyValuePair<string, string?>>? raw, string variant)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in raw ?? [])
            {
                if (key == null || !ThemeTokens.Seeds.Contains(key)) dropped?.Add(variant + ": unknown seed " + Describe(key));
                else if (!TryNormalizeOpaqueHex(value, out string hex)) dropped?.Add(variant + ": invalid colour for " + key);
                else result[key] = hex;
            }
            return result.ToFrozenDictionary(StringComparer.Ordinal);
        }
        FrozenDictionary<string, string> l = Normalize(light, LightKey), d = Normalize(dark, DarkKey);
        return l.Count == 0 && d.Count == 0 ? Empty : new ThemeColorOverrides(l, d);
    }

    /// <summary>The same overrides with one seed of one base set (<paramref name="color"/>) or removed (null).</summary>
    internal ThemeColorOverrides WithSeed(ThemeTokenTheme theme, string seed, Color? color)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (!ThemeTokens.Seeds.Contains(seed)) throw new ArgumentException($"\"{seed}\" is not a colour seed.", nameof(seed));
        var next = new Dictionary<string, string>(For(theme), StringComparer.Ordinal);
        if (color is { } value)
        {
            if (value.A != byte.MaxValue) throw new ArgumentException("A colour override must be opaque.", nameof(color)); // N9
            next[seed] = ThemePalette.Hex(value);
        }
        else next.Remove(seed);
        return Replace(theme, next);
    }

    /// <summary>Sets a role's primary seed (<see cref="ThemeUserColor.Seed"/>); its related seeds then follow unless set explicitly.</summary>
    internal ThemeColorOverrides WithRole(ThemeTokenTheme theme, string roleId, Color color) =>
        WithSeed(theme, ThemeUserColors.Require(roleId).Seed, color);

    /// <summary>Reset one role of one base: its primary seed and every related seed lose their explicit override.</summary>
    internal ThemeColorOverrides ResetRole(ThemeTokenTheme theme, string roleId)
    {
        ThemeUserColor role = ThemeUserColors.Require(roleId);
        var next = new Dictionary<string, string>(For(theme), StringComparer.Ordinal);
        foreach (string seed in role.Seeds) next.Remove(seed);
        return Replace(theme, next);
    }

    /// <summary>Reset one base (Light or Dark) to the built-in palette; the other base keeps its overrides.</summary>
    internal ThemeColorOverrides ResetTheme(ThemeTokenTheme theme) => Replace(theme, new Dictionary<string, string>());

    private ThemeColorOverrides Replace(ThemeTokenTheme theme, Dictionary<string, string> values)
    {
        FrozenDictionary<string, string> frozen = values.ToFrozenDictionary(StringComparer.Ordinal);
        FrozenDictionary<string, string> light = theme == ThemeTokenTheme.Light ? frozen : _light;
        FrozenDictionary<string, string> dark = theme == ThemeTokenTheme.Dark ? frozen : _dark;
        return light.Count == 0 && dark.Count == 0 ? Empty : new ThemeColorOverrides(light, dark);
    }

    /// <summary>
    /// <c>#RRGGBB</c> or <c>#AARRGGBB</c> (hex digits in either case, surrounding blanks ignored) → upper-case, an opaque
    /// <c>#FFRRGGBB</c> shortened to <c>#RRGGBB</c>, as <see cref="ThemePalette.Hex"/> writes it. Anything else is invalid.
    /// </summary>
    internal static bool TryNormalizeHex(string? value, out string hex)
    {
        hex = string.Empty;
        string? text = value?.Trim();
        if (text is not { Length: 7 or 9 } || text[0] != '#' || text.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0) return false;
        text = text.ToUpperInvariant();
        hex = text.Length == 9 && text.StartsWith("#FF", StringComparison.Ordinal) ? "#" + text[3..] : text;
        return true;
    }

    /// <summary>N9: as <see cref="TryNormalizeHex"/>, but only an opaque colour (<c>#RRGGBB</c> or <c>#FFRRGGBB</c>) is a valid override; <c>#AARRGGBB</c> with another alpha is not.</summary>
    internal static bool TryNormalizeOpaqueHex(string? value, out string hex) =>
        TryNormalizeHex(value, out hex) && hex.Length == 7;

    private static string Describe(string? key) =>
        key == null ? "(null)" : "\"" + new string(key.Take(64).Select(c => char.IsControl(c) ? '?' : c).ToArray()) + (key.Length > 64 ? "…\"" : "\"");

    public bool Equals(ThemeColorOverrides? other) =>
        other != null && Same(_light, other._light) && Same(_dark, other._dark);

    public override bool Equals(object? obj) => Equals(obj as ThemeColorOverrides);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in _light.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { hash.Add(1); hash.Add(key); hash.Add(value); }
        foreach (var (key, value) in _dark.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { hash.Add(2); hash.Add(key); hash.Add(value); }
        return hash.ToHashCode();
    }

    internal static bool Same(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out string? value) && value == pair.Value);
}

/// <summary>
/// Reads <c>Config.ThemeOverrides</c> tolerantly: a missing, null or non-object value is no override; unknown variants, non-seed
/// keys, non-string values and malformed colours are dropped with one log warning; the rest of the profile is unaffected.
/// Writes both variants with sorted keys.
/// </summary>
internal sealed class ThemeColorOverridesConverter : JsonConverter<ThemeColorOverrides>
{
    public ThemeColorOverridesConverter() { }

    public override bool HandleNull => true;

    public override ThemeColorOverrides Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dropped = new List<string>();
        if (reader.TokenType == JsonTokenType.Null) return ThemeColorOverrides.Empty;
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            Log.Warning("The theme colour overrides in the settings are not an object and were ignored.");
            return ThemeColorOverrides.Empty;
        }
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        List<KeyValuePair<string, string?>>? light = null, dark = null;
        foreach (JsonProperty variant in document.RootElement.EnumerateObject())
        {
            List<KeyValuePair<string, string?>>? target = variant.Name switch
            {
                ThemeColorOverrides.LightKey => light = [],
                ThemeColorOverrides.DarkKey => dark = [],
                _ => null
            };
            if (target == null || variant.Value.ValueKind != JsonValueKind.Object)
            {
                dropped.Add(target == null ? "unknown variant" : variant.Name + ": not an object");
                continue;
            }
            foreach (JsonProperty entry in variant.Value.EnumerateObject())
                target.Add(new(entry.Name, entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null));
        }
        ThemeColorOverrides result = ThemeColorOverrides.Create(light, dark, dropped);
        if (dropped.Count > 0)
            Log.Warning("Ignored {Count} theme colour override entries in the settings: {Entries}", dropped.Count,
                string.Join("; ", dropped.Take(8)) + (dropped.Count > 8 ? "; …" : ""));
        return result;
    }

    public override void Write(Utf8JsonWriter writer, ThemeColorOverrides value, JsonSerializerOptions options)
    {
        value ??= ThemeColorOverrides.Empty;
        writer.WriteStartObject();
        foreach (var (name, map) in new[] { (ThemeColorOverrides.LightKey, value.Light), (ThemeColorOverrides.DarkKey, value.Dark) })
        {
            writer.WriteStartObject(name);
            foreach (var (seed, hex) in map.OrderBy(pair => pair.Key, StringComparer.Ordinal)) writer.WriteString(seed, hex);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}
