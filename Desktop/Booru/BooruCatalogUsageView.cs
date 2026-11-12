using MeowChan.Archive;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// Size of the local Danbooru catalog against its configured limit (Settings). The watcher area shows a warning
/// from <see cref="BooruCatalogUsage.WarningFraction"/> on: once the limit is reached, watchers and original
/// downloads can no longer store new posts.
/// </summary>
internal sealed record BooruCatalogUsageView(long DocumentBytes, int PostCount, long LimitBytes)
{
    internal double Fraction => LimitBytes <= 0 ? 1 : (double)DocumentBytes / LimitBytes;
    internal bool NearLimit => Fraction >= BooruCatalogUsage.WarningFraction;
    internal int Percent => (int)System.Math.Min(100, System.Math.Floor(Fraction * 100));

    internal static BooruCatalogUsageView From(BooruCatalogUsage usage) => new(usage.DocumentBytes, usage.PostCount, usage.LimitBytes);
}
