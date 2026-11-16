using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowChan;

// Like the status dwell converter: repair a presentation preference without losing the rest of the profile.
internal sealed class DesktopPreferenceGalleryPostTagChipLimitConverter : JsonConverter<int>
{
    public override bool HandleNull => true;

    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
            return Math.Clamp(value, Config.GalleryPostTagChipLimitMinimum, Config.GalleryPostTagChipLimitMaximum);
        reader.Skip();
        return Config.GalleryPostTagChipLimitDefault;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
