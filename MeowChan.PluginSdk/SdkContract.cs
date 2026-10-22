namespace MeowChan.PluginSdk;

/// <summary>The SDK version is independent of the product and private worker protocol.</summary>
public static class SdkContract
{
    public const string Version = "1.0.0";

    /// <summary>No cross-version binary compatibility is promised in 1.x.</summary>
    public static bool IsSupported(string? version) => string.Equals(version, Version, StringComparison.Ordinal);
}

/// <summary>Contract defaults; the host may apply stricter limits to a particular operation.</summary>
public static class SdkLimits
{
    public const int MaximumMetadataBytes = 8 * 1024 * 1024;
    public const int MaximumImageChunkBytes = 64 * 1024;
    public const long MaximumImageBytes = 32L * 1024 * 1024;
    public const long MaximumDecodedPixels = 16L * 1024 * 1024;
    public const int MaximumIdentifierLength = 256;
    public const int MaximumDiagnosticLength = 2048;
    public const int MaximumTextLength = 1024 * 1024;
    public const int MaximumCollectionCount = 100000;
}
