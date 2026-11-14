using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowChan;

// Like the controls idle-hide converter (GL5e): repair a presentation preference without losing the rest of the profile.
internal sealed class DesktopPreferenceGalleryFilmstripTileGapConverter : JsonConverter<int>
{
    public override bool HandleNull => true;

    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
            return Math.Clamp(value, Config.GalleryFilmstripTileGapMinimum, Config.GalleryFilmstripTileGapMaximum);
        reader.Skip();
        return Config.GalleryFilmstripTileGapDefault;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
