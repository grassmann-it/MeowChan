using System.Reflection;

namespace MeowChan.PluginHost.Http;

/// <summary>Builds the product-owned User-Agent used only for booru metadata requests.</summary>
public static class BooruUserAgent
{
    internal const string InvalidDanbooruUserIdMessage = "The Danbooru user id must be a positive decimal number of at most 12 digits without leading zeros.";
    private const string ProductPrefix = "MeowChan/";
    private const string ProductSuffix = " (+https://github.com/grassmann-it/MeowChan)";

    public static string Build(string productVersion, string? danbooruUserId = null)
    {
        string version = NormalizeProductVersion(productVersion);
        if (danbooruUserId is not null && !IsValidUserId(danbooruUserId))
            throw new ArgumentException(InvalidDanbooruUserIdMessage, nameof(danbooruUserId));

        return ProductPrefix + version + ProductSuffix
            + (danbooruUserId is null ? string.Empty : " (user #" + danbooruUserId + ")");
    }

    /// <summary>Returns the informational product version without build metadata.</summary>
    public static string ProductVersion()
    {
        Assembly assembly = typeof(BooruUserAgent).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            string value = informational.Split('+', 2)[0];
            if (IsSafeProductVersion(value)) return value;
        }

        Version? version = assembly.GetName().Version;
        if (version is null) return "0.0.0";
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{version.Major}.{version.Minor}.{(version.Build < 0 ? 0 : version.Build)}");
    }

    private static string NormalizeProductVersion(string productVersion)
    {
        if (productVersion is null) throw new ArgumentNullException(nameof(productVersion));
        string value = productVersion.Split('+', 2)[0];
        if (!IsSafeProductVersion(value))
            throw new ArgumentException("A non-empty product version without control characters is required.", nameof(productVersion));
        return value;
    }

    private static bool IsSafeProductVersion(string value) => value.Length is > 0 and <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool IsValidUserId(string value) => value.Length is > 0 and <= 12
        && value[0] is >= '1' and <= '9'
        && value[1..].All(char.IsAsciiDigit);
}
