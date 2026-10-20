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
    private sealed record Catalog(LanguageInfo Info, IReadOnlyDictionary<string, string> Strings);
    private sealed record State(IReadOnlyDictionary<string, Catalog> Catalogs,
        IReadOnlyList<LanguageInfo> Languages, IReadOnlyList<string> Warnings, string Language);

    private const int MaximumPackBytes = 2_000_000;
    private static readonly object Gate = new object();
    private static State _state = CreateBuiltIns();

    public static event EventHandler? Changed;
    public static string Language => Volatile.Read(ref _state).Language;
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
    public static IReadOnlyList<LanguageInfo> Languages => Volatile.Read(ref _state).Languages;
    public static IReadOnlyList<string> Warnings => Volatile.Read(ref _state).Warnings;

    /// <summary>Reloads built-ins and optional JSON packs. A missing directory is normal.</summary>
    public static void Initialize(string? directory = null)
    {
        State builtIns = CreateBuiltIns();
        Dictionary<string, Catalog> catalogs = new Dictionary<string, Catalog>(builtIns.Catalogs, StringComparer.OrdinalIgnoreCase);
        List<string> warnings = new List<string>();
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
                            if (!catalogs.TryAdd(catalog.Info.Code, catalog))
                                throw new InvalidDataException("Duplicate language code: " + catalog.Info.Code);
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
        string fallback = state.Catalogs["en"].Strings.GetValueOrDefault(key) ?? key;
        string value = state.Catalogs[state.Language].Strings.GetValueOrDefault(key) ?? fallback;
        if (arguments.Length == 0)
            return value;

        try
        {
            return string.Format(CultureInfo.GetCultureInfo(state.Language), value, arguments);
        }
        catch (FormatException)
        {
            // A caller with mismatched arguments must not break the entire desktop.
            try { return string.Format(CultureInfo.GetCultureInfo("en"), fallback, arguments); }
            catch (FormatException) { return fallback; }
        }
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
        if (stream.Length > MaximumPackBytes)
            throw new InvalidDataException("Language pack exceeds 2 MB.");
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
            if (keys.Count > 2000 || entry.Name.Length > 200)
                throw new InvalidDataException("Too many or excessively long translation keys.");
            if (entry.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Translation must be plain text: " + entry.Name);
            string translation = entry.Value.GetString()!;
            if (string.IsNullOrWhiteSpace(translation) || translation.Length > 10_000)
                throw new InvalidDataException("Empty or excessively long translation: " + entry.Name);
            if (!english.TryGetValue(entry.Name, out string? fallback))
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
