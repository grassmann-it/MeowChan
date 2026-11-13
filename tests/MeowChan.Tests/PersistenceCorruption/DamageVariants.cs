using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeowChan.Tests.PersistenceCorruption;

// Shared with the desktop tests (linked file): the named ways in which a persistence file can be damaged.

public static class Damage
{
    internal static readonly string[] PrimaryVariants =
    {
        "empty", "whitespace-only", "bom-only",
        "truncated-1", "truncated-quarter", "truncated-half", "truncated-last-byte",
        "invalid-utf8-in-string", "overlong-utf8-in-string", "invalid-utf8-lead-byte", "nul-bytes",
        "root-array", "root-string", "root-number", "root-null", "root-true",
        "version-as-string", "version-missing",
        "duplicate-first-property", "duplicate-array-property-different-value", "unknown-extra-field",
        "future-version", "trailing-garbage"
    };

    internal static readonly string[] BackupVariants =
    {
        "empty", "truncated-half", "invalid-utf8-in-string", "root-array",
        "duplicate-array-property-different-value", "future-version", "trailing-garbage"
    };

    public static IEnumerable<object[]> Primary() => PrimaryVariants.Select(variant => new object[] { variant });
    public static IEnumerable<object[]> Backup() => BackupVariants.Select(variant => new object[] { variant });

    internal static byte[] Make(string variant, byte[] good, string versionProperty)
    {
        switch (variant)
        {
            case "empty": return Array.Empty<byte>();
            case "whitespace-only": return " \r\n\t ".Select(c => (byte)c).ToArray();
            case "bom-only": return new byte[] { 0xEF, 0xBB, 0xBF };
            case "truncated-1": return good[..1];
            case "truncated-quarter": return good[..(good.Length / 4)];
            case "truncated-half": return good[..(good.Length / 2)];
            case "truncated-last-byte": return TruncatedAfterLastBrace(good);
            case "invalid-utf8-in-string": return InsertAfterFirstStringStart(good, 0xC3, 0x28);
            case "overlong-utf8-in-string": return InsertAfterFirstStringStart(good, 0xC0, 0xAF);
            case "invalid-utf8-lead-byte": return new byte[] { 0xFF, 0xFE }.Concat(good).ToArray();
            case "nul-bytes": return good.Take(good.Length / 2).Concat(new byte[16]).Concat(good.Skip(good.Length / 2)).ToArray();
            case "root-array": return Encoding.UTF8.GetBytes("[]");
            case "root-string": return Encoding.UTF8.GetBytes("\"text\"");
            case "root-number": return Encoding.UTF8.GetBytes("42");
            case "root-null": return Encoding.UTF8.GetBytes("null");
            case "root-true": return Encoding.UTF8.GetBytes("true");
            case "version-as-string": return WithRoot(good, root => root[versionProperty] = "1");
            case "version-missing": return WithRoot(good, root => root.Remove(versionProperty));
            case "unknown-extra-field": return WithRoot(good, root => root["UnexpectedFutureField"] = 1);
            case "future-version": return WithRoot(good, root => root[versionProperty] = 99);
            case "duplicate-first-property": return DuplicateFirstProperty(good);
            case "duplicate-array-property-different-value": return DuplicateArrayProperty(good);
            case "trailing-garbage": return good.Concat(Encoding.UTF8.GetBytes("\n{\"garbage\":")).ToArray();
            default: throw new ArgumentOutOfRangeException(nameof(variant), variant, null);
        }
    }

    private static byte[] TruncatedAfterLastBrace(byte[] good)
    {
        // The complete document minus its final closing brace: the classic "write cut short at the very end".
        int end = good.Length;
        while (end > 0 && (good[end - 1] is (byte)'\n' or (byte)'\r' or (byte)' ' or (byte)'\t')) end--;
        Assert.True(end > 1 && good[end - 1] == (byte)'}');
        return good[..(end - 1)];
    }

    private static byte[] WithRoot(byte[] good, Action<JsonObject> change)
    {
        JsonObject root = JsonNode.Parse(good)!.AsObject();
        change(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static byte[] InsertAfterFirstStringStart(byte[] good, params byte[] inserted)
    {
        // Latin-1 keeps one char per byte, so the regex index is the byte index.
        string text = Encoding.Latin1.GetString(good);
        Match match = Regex.Match(text, "[:,\\[]\\s*\"");
        Assert.True(match.Success, "The seeded document has no string value to damage.");
        int at = match.Index + match.Length;
        return good[..at].Concat(inserted).Concat(good[at..]).ToArray();
    }

    private static byte[] DuplicateFirstProperty(byte[] good)
    {
        using JsonDocument document = JsonDocument.Parse(good);
        JsonProperty first = document.RootElement.EnumerateObject().First();
        string text = Encoding.UTF8.GetString(good);
        int brace = text.IndexOf('{');
        string duplicate = JsonSerializer.Serialize(first.Name) + ":" + first.Value.GetRawText() + ",";
        return Encoding.UTF8.GetBytes(text.Insert(brace + 1, duplicate));
    }

    private static byte[] DuplicateArrayProperty(byte[] good)
    {
        using JsonDocument document = JsonDocument.Parse(good);
        // The first collection of the document (an array, else an object), repeated with an empty value:
        // "the last one wins" would silently drop every entry.
        JsonProperty collection = document.RootElement.EnumerateObject().FirstOrDefault(property => property.Value.ValueKind == JsonValueKind.Array);
        string empty = "[]";
        if (collection.Value.ValueKind != JsonValueKind.Array)
        {
            collection = document.RootElement.EnumerateObject().First(property => property.Value.ValueKind == JsonValueKind.Object);
            empty = "{}";
        }
        string text = Encoding.UTF8.GetString(good);
        int end = text.LastIndexOf('}');
        return Encoding.UTF8.GetBytes(text.Insert(end, "," + JsonSerializer.Serialize(collection.Name) + ":" + empty));
    }

    /// <summary>A file of <paramref name="length"/> bytes that takes no disk space (sparse); reading it whole would allocate that much.</summary>
    internal static void WriteSparse(string path, long length)
    {
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }
}

