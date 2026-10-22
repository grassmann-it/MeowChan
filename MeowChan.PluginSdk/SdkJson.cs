using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowChan.PluginSdk;

/// <summary>DTO JSON only; framing, correlation and protocol-major negotiation remain private.</summary>
public static class SdkJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            MaxDepth = 64,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, CreateOptions());

    /// <summary>Deserialize and then validate against the expected request/description before use.</summary>
    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > SdkLimits.MaximumMetadataBytes * 2)
            throw new JsonException("The SDK JSON document exceeded its byte limit.");
        return JsonSerializer.Deserialize<T>(utf8Json, CreateOptions())
            ?? throw new JsonException("An SDK document must not be null.");
    }
}
