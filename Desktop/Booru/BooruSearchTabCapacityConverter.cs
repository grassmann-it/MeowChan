using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowChan;

/// <summary>Profiles and settings transfers store the legacy additional-tab count.</summary>
internal sealed class BooruSearchTabCapacityConverter : JsonConverter<int>
{
    public BooruSearchTabCapacityConverter() { }

    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int additional)
            || additional is < Config.BooruSearchTabsMinimum - 1 or > Config.BooruSearchTabsMaximum - 1)
            throw new JsonException("The saved Booru search tab limit is outside its supported range.");
        return additional + 1;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value - 1);
}
