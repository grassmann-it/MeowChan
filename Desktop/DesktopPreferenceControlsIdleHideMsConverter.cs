using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowChan;

// Like the slideshow converters: repair presentation preferences without losing valid profile settings.
internal sealed class DesktopPreferenceControlsIdleHideMsConverter : JsonConverter<int>
{
    public override bool HandleNull => true;

    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
            return Math.Clamp(value, Config.ControlsIdleHideMsMinimum, Config.ControlsIdleHideMsMaximum);
        reader.Skip();
        return Config.ControlsIdleHideMsDefault;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
