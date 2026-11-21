using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowChan;

// A damaged filename template reads as "off" (the current naming): it never fails the whole settings file.
internal sealed class DesktopPreferenceExportFilenameTemplateConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } value
            && MeowChan.Interop.ExportFilenameTemplate.Check(value).Ok)
            return value;
        reader.Skip();
        return string.Empty;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
