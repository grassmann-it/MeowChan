namespace MeowChan.PluginSdk;

/// <summary>The SDK version is independent of the product and private worker protocol.</summary>
public static class SdkContract
{
    public const string Version = "1.1.0";

    /// <summary>
    /// 1.1.0 adds the booru post-search contract and <see cref="PluginDescription.DeclarationStatus"/> to
    /// 1.0.0, both additively. The host accepts exactly 1.0.0 and 1.1.0 by ordinal comparison; no other
    /// cross-version compatibility is promised in 1.x.
    /// </summary>
    public static bool IsSupported(string? version)
        => string.Equals(version, "1.0.0", StringComparison.Ordinal) || string.Equals(version, Version, StringComparison.Ordinal);
}

/// <summary>Contract defaults; the host may apply stricter limits to a particular operation.</summary>
public static class SdkLimits
{
    public const int MaximumMetadataBytes = 8 * 1024 * 1024;
    public const int MaximumImageChunkBytes = 64 * 1024;
    public const long MaximumImageBytes = 32L * 1024 * 1024;
    public const long MaximumDecodedPixels = 16L * 1024 * 1024;
    public const int MaximumIdentifierLength = 256;

    /// <summary>Maximum decimal digits in a booru post ID or before-ID cursor.</summary>
    public const int MaximumBooruPostIdDigits = 18;
    public const int MaximumDiagnosticLength = 2048;
    public const int MaximumTextLength = 1024 * 1024;
    public const int MaximumCollectionCount = 100000;
}
