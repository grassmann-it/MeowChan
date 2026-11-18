using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MeowChan.Persistence;

/// <summary>
/// Finds a property name that an object of a JSON document repeats. The serializer keeps the last of two equal names,
/// so a repeated collection would silently replace the stored one; a store calls this before it deserializes and treats
/// a repeat as damage. Streams with a reader: no document tree is built, and only one set of names per open object is
/// held. Names are compared after unescaping and ordinally, as the case-sensitive serializers of the stores match them.
/// </summary>
internal static class JsonDuplicateProperties
{
    private static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>True when any object repeats a property name. Malformed JSON throws <see cref="JsonException"/>.</summary>
    internal static bool Any(ReadOnlySpan<byte> utf8Json, int maxDepth = 64)
    {
        if (utf8Json.StartsWith(Bom)) utf8Json = utf8Json[Bom.Length..];
        Utf8JsonReader reader = new(utf8Json, new JsonReaderOptions { MaxDepth = maxDepth });
        Stack<HashSet<string>?> scopes = new();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject: scopes.Push(new HashSet<string>(StringComparer.Ordinal)); break;
                    case JsonTokenType.StartArray: scopes.Push(null); break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray: scopes.Pop(); break;
                    case JsonTokenType.PropertyName:
                        if (!scopes.Peek()!.Add(reader.GetString()!)) return true;
                        break;
                }
            }
        }
        // GetString throws InvalidOperationException for a name that cannot be decoded (a lone surrogate escape, invalid UTF-8).
        catch (InvalidOperationException error) { throw new JsonException(error.Message, error); }
        return false;
    }
}
