using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace MeowChan.Desktop.Localization;

public sealed record LanguageInfo(string Code, string NativeName)
{
    public override string ToString() => NativeName;
}

/// <summary>
/// Data-only desktop translations. Catalog snapshots are immutable and safe to read from
/// workers. Changes notify on the caller's thread; UI subscribers dispatch and unsubscribe.
/// </summary>
public static class Texts
{
    // The culture is resolved once when the catalog is built, so formatting never looks it up per call.
    private sealed record Catalog(LanguageInfo Info, IReadOnlyDictionary<string, string> Strings)
    {
        public CultureInfo Culture { get; } = CultureInfo.GetCultureInfo(Info.Code);
    }
    private sealed record State(IReadOnlyDictionary<string, Catalog> Catalogs,
        IReadOnlyList<LanguageInfo> Languages, IReadOnlyList<string> Warnings, string Language);

    // Fixed technical ceilings for any pack, bundled or user-supplied. They are not user settings: they keep a
    // malformed or hostile file from costing unbounded memory and parse time at startup. The byte ceiling is the
    // real memory guard; the key ceiling stops a file of countless tiny entries that stays under the byte ceiling.
    // The bundled packs hold about 2,900 keys in about 250-340 KiB, so 8,000 keys / 2 MiB leaves room for the
    // catalog to roughly 2.7x its size. A bundled pack that exceeds a ceiling is rejected with a warning and its
    // language silently disappears from the choice, so BundledLanguagePackTests fail once a pack passes 75 % of
    // either ceiling, long before the loader would refuse it. Raise a ceiling together with that test's reason.
    internal const int MaximumPackBytes = 2_000_000;
    internal const int MaximumPackKeys = 8000;
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en");
    private static readonly object Gate = new object();
    private static State _state = CreateBuiltIns();

    public static event EventHandler? Changed;
    public static string Language => Volatile.Read(ref _state).Language;
    public static CultureInfo Culture
    {
        get
        {
            State state = Volatile.Read(ref _state);
            return state.Catalogs[state.Language].Culture;
        }
    }
    public static IReadOnlyList<LanguageInfo> Languages => Volatile.Read(ref _state).Languages;
    public static IReadOnlyList<string> Warnings => Volatile.Read(ref _state).Warnings;

    /// <summary>Reloads built-ins, bundled previews and optional user JSON packs. A missing directory is normal.</summary>
    public static void Initialize(string? directory = null) => InitializeCore(directory, includeBundles: true);

    // Isolates the original user-pack loader contract without changing normal startup defaults.
    internal static void InitializeWithoutBundlesForTests(string? directory = null) => InitializeCore(directory, includeBundles: false);

    private static void InitializeCore(string? directory, bool includeBundles)
    {
        State builtIns = CreateBuiltIns();
        Dictionary<string, Catalog> catalogs = new Dictionary<string, Catalog>(builtIns.Catalogs, StringComparer.OrdinalIgnoreCase);
        List<string> warnings = new List<string>();
        if (includeBundles)
        {
            foreach (string code in new[] { "es", "fr", "ru" })
            {
                string resource = "MeowChan.Desktop.Localization.Packs." + code + ".json";
                try
                {
                    using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(resource)
                        ?? throw new InvalidDataException("Bundled language resource is missing.");
                    Catalog catalog = ReadPack(stream, catalogs["en"].Strings);
                    if (!string.Equals(catalog.Info.Code, code, StringComparison.OrdinalIgnoreCase)
                        || !catalogs.TryAdd(catalog.Info.Code, catalog))
                        throw new InvalidDataException("Unexpected bundled language code: " + catalog.Info.Code);
                }
                catch (Exception exception) when (IsPackError(exception))
                {
                    warnings.Add("Bundled " + code + ": " + exception.Message);
                }
            }
        }
        HashSet<string> userCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    foreach (string path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
                    {
                        try
                        {
                            Catalog catalog = ReadPack(path, catalogs["en"].Strings);
                            // Only a fully validated user pack may replace a bundled preview. Never
                            // replace EN/DE or let a second user file silently win by filename order.
                            if (builtIns.Catalogs.ContainsKey(catalog.Info.Code) || !userCodes.Add(catalog.Info.Code))
                                throw new InvalidDataException("Duplicate language code: " + catalog.Info.Code);
                            catalogs[catalog.Info.Code] = catalog;
                        }
                        catch (Exception exception) when (IsPackError(exception))
                        {
                            warnings.Add(Path.GetFileName(path) + ": " + exception.Message);
                        }
                    }
                }
            }
            catch (Exception exception) when (IsPackError(exception))
            {
                warnings.Add("Language packs: " + exception.Message);
            }
        }

        lock (Gate)
        {
            string previous = _state.Language;
            string language = catalogs.TryGetValue(previous, out Catalog? selected) ? selected.Info.Code : "en";
            if (!catalogs.ContainsKey(previous))
                warnings.Add("Language '" + previous + "' is unavailable; using English.");
            Volatile.Write(ref _state, new State(new ReadOnlyDictionary<string, Catalog>(catalogs),
                Array.AsReadOnly(catalogs.Values.Select(catalog => catalog.Info).ToArray()),
                Array.AsReadOnly(warnings.ToArray()), language));
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Unavailable saved languages fall back to English without preventing startup.</summary>
    public static void SetLanguage(string code)
    {
        bool changed;
        lock (Gate)
        {
            State state = _state;
            string language;
            IReadOnlyList<string> warnings = state.Warnings;
            if (!string.IsNullOrWhiteSpace(code) && state.Catalogs.TryGetValue(code, out Catalog? catalog))
            {
                language = catalog.Info.Code;
            }
            else
            {
                language = "en";
                string warning = "Language '" + code + "' is unavailable; using English.";
                if (!warnings.Contains(warning, StringComparer.Ordinal))
                    warnings = Array.AsReadOnly(warnings.Append(warning).ToArray());
            }

            changed = state.Language != language;
            Volatile.Write(ref _state, state with { Language = language, Warnings = warnings });
        }
        if (changed)
            Changed?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string key, params object?[] arguments)
    {
        State state = Volatile.Read(ref _state);
        Catalog current = state.Catalogs[state.Language];
        // The active language first; English only when it lacks the key.
        if (!current.Strings.TryGetValue(key, out string? value))
            value = state.Catalogs["en"].Strings.GetValueOrDefault(key) ?? key;
        if (arguments.Length == 0)
            return value;
        try
        {
            return string.Format(current.Culture, value, arguments);
        }
        catch (FormatException)
        {
            // A caller with mismatched arguments must not break the entire desktop.
            return FormatEnglishFallback(state.Catalogs["en"].Strings.GetValueOrDefault(key) ?? key, arguments);
        }
    }

    /// <summary>
    /// Formats a counted text in the grammatical number of <paramref name="count"/>. The base key holds the
    /// general plural form; <c>key.one</c> (and, where the language has them, <c>key.few</c>, <c>key.many</c>,
    /// <c>key.two</c>, <c>key.zero</c>) hold the other forms. A missing form falls back to the base key.
    /// Without <paramref name="arguments"/>, <paramref name="count"/> is {0}; texts with several values pass them all.
    /// </summary>
    public static string Count(string key, long count, params object?[] arguments)
    {
        State state = Volatile.Read(ref _state);
        IReadOnlyDictionary<string, string> english = state.Catalogs["en"].Strings;
        IReadOnlyDictionary<string, string> current = state.Catalogs[state.Language].Strings;
        // A language that lacks the base text shows the English text, so it also uses the English number rules.
        bool own = current.ContainsKey(key);
        string language = own ? state.Language : "en";
        IReadOnlyDictionary<string, string> source = own ? current : english;
        string category = PluralRules.Category(language, count);
        string fallback = english.GetValueOrDefault(PluralRules.Variant(key, PluralRules.Category("en", count))) ?? english.GetValueOrDefault(key) ?? key;
        string value = (category != PluralRules.Other ? source.GetValueOrDefault(PluralRules.Variant(key, category)) : null)
            ?? source.GetValueOrDefault(key) ?? fallback;
        return FormatSafely(state.Catalogs[state.Language].Culture, value, fallback, arguments.Length == 0 ? new object?[] { count } : arguments);
    }

    private static string FormatSafely(CultureInfo culture, string value, string fallback, object?[] arguments)
    {
        try
        {
            return string.Format(culture, value, arguments);
        }
        catch (FormatException)
        {
            // A caller with mismatched arguments must not break the entire desktop.
            return FormatEnglishFallback(fallback, arguments);
        }
    }

    private static string FormatEnglishFallback(string fallback, object?[] arguments)
    {
        try { return string.Format(EnglishCulture, fallback, arguments); }
        catch (FormatException) { return fallback; }
    }

    public static bool IsValidLanguageCode(string? code) => code != null && code.Length <= 35 &&
        Regex.IsMatch(code, "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant);

    /// <summary>Checks the complete built-in catalogs, including formatting contracts.</summary>
    public static IReadOnlyList<string> ValidateBuiltInCatalogs()
    {
        State state = CreateBuiltIns();
        IReadOnlyDictionary<string, string> english = state.Catalogs["en"].Strings;
        IReadOnlyDictionary<string, string> german = state.Catalogs["de"].Strings;
        List<string> errors = new List<string>();
        foreach (KeyValuePair<string, string> entry in english)
        {
            if (!german.TryGetValue(entry.Key, out string? translation))
            {
                errors.Add("Missing German translation: " + entry.Key);
                continue;
            }
            try
            {
                if (!Placeholders(entry.Value).SetEquals(Placeholders(translation)))
                    errors.Add("Different placeholders: " + entry.Key);
            }
            catch (FormatException) { errors.Add("Invalid placeholders: " + entry.Key); }
        }
        foreach (string key in german.Keys.Except(english.Keys, StringComparer.Ordinal))
            errors.Add("Missing English translation: " + key);
        return errors.AsReadOnly();
    }

    private static State CreateBuiltIns()
    {
        Dictionary<string, Catalog> catalogs = new Dictionary<string, Catalog>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = new Catalog(new LanguageInfo("en", "English"), BuiltInCatalog.English),
            ["de"] = new Catalog(new LanguageInfo("de", "Deutsch"), BuiltInCatalog.German)
        };
        return new State(new ReadOnlyDictionary<string, Catalog>(catalogs),
            Array.AsReadOnly(catalogs.Values.Select(catalog => catalog.Info).ToArray()), Array.Empty<string>(), "en");
    }

    private static Catalog ReadPack(string path, IReadOnlyDictionary<string, string> english)
    {
        using FileStream stream = File.OpenRead(path);
        return ReadPack(stream, english);
    }

    // Both embedded and user packs use exactly the same schema, size and formatting checks.
    private static Catalog ReadPack(Stream stream, IReadOnlyDictionary<string, string> english)
    {
        if (stream.Length > MaximumPackBytes)
            throw new InvalidDataException("Language pack exceeds 2 MiB.");
        using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Language pack must be an object.");

        Dictionary<string, JsonElement> metadata = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!metadata.TryAdd(property.Name, property.Value))
                throw new InvalidDataException("Duplicate metadata: " + property.Name);
            if (property.Name.ToLowerInvariant() is not ("schemaversion" or "code" or "nativename" or "strings"))
                throw new InvalidDataException("Unknown metadata: " + property.Name);
        }
        if (!metadata.TryGetValue("schemaVersion", out JsonElement version) ||
            !version.TryGetInt32(out int schema) || schema != 1)
            throw new InvalidDataException("Unsupported language-pack schemaVersion.");

        string code = ReadString(metadata, "code");
        string nativeName = ReadString(metadata, "nativeName");
        if (!IsValidLanguageCode(code) || string.IsNullOrWhiteSpace(nativeName) || nativeName.Length > 64 ||
            nativeName.Any(char.IsControl))
            throw new InvalidDataException("Invalid language-pack metadata.");
        code = CultureInfo.GetCultureInfo(code).Name;
        if (!metadata.TryGetValue("strings", out JsonElement strings) || strings.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Missing translation object.");

        HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, string> accepted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in strings.EnumerateObject())
        {
            if (!keys.Add(entry.Name))
                throw new InvalidDataException("Duplicate translation: " + entry.Name);
            if (keys.Count > MaximumPackKeys || entry.Name.Length > 200)
                throw new InvalidDataException("Too many or excessively long translation keys.");
            if (entry.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Translation must be plain text: " + entry.Name);
            string translation = entry.Value.GetString()!;
            if (string.IsNullOrWhiteSpace(translation) || translation.Length > 10_000)
                throw new InvalidDataException("Empty or excessively long translation: " + entry.Name);
            if (!english.TryGetValue(entry.Name, out string? fallback)
                && !(PluralRules.BaseKey(entry.Name) is { } baseKey && english.TryGetValue(baseKey, out fallback)))
                continue; // A newer catalog may contain keys that this version does not use.
            if (!Placeholders(fallback).SetEquals(Placeholders(translation)))
                throw new InvalidDataException("Different placeholders or formats: " + entry.Name);
            accepted.Add(entry.Name, translation);
        }
        if (accepted.Count == 0)
            throw new InvalidDataException("Language pack has no known translations.");
        return new Catalog(new LanguageInfo(code, nativeName.Trim()), new ReadOnlyDictionary<string, string>(accepted));
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Missing text metadata: " + key);
        return value.GetString()!;
    }

    private static HashSet<string> Placeholders(string format)
    {
        _ = CompositeFormat.Parse(format);
        HashSet<string> placeholders = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < format.Length; index++)
        {
            if (format[index] != '{')
                continue;
            if (index + 1 < format.Length && format[index + 1] == '{')
            {
                index++;
                continue;
            }
            int end = format.IndexOf('}', index + 1);
            // CompositeFormat has already validated every unescaped opening brace.
            placeholders.Add(format.Substring(index, end - index + 1));
            index = end;
        }
        return placeholders;
    }

    private static bool IsPackError(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException
        or JsonException or ArgumentException or FormatException or InvalidOperationException;
}
