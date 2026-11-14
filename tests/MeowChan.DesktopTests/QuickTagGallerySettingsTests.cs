using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

public sealed class QuickTagGallerySettingsTests
{
    [Fact]
    public void UpdatingTheRecentLimitKeepsNewestTagsAndZeroForgetsThem()
    {
        Config config = new() { QuickTagRecentCount = 3 };
        TagRecentList recent = QuickTagSettings.NewRecentList(config);
        TagId first = new(Guid.NewGuid()), second = new(Guid.NewGuid()), third = new(Guid.NewGuid());
        recent.Touch(new[] { first, second, third });
        config.QuickTagRecentCount = 2;
        QuickTagSettings.ApplyRecentLimit(recent, config);
        Assert.Equal(new[] { third, second }, recent.Snapshot());
        config.QuickTagRecentCount = 0;
        QuickTagSettings.ApplyRecentLimit(recent, config);
        Assert.Empty(recent.Snapshot());
        recent.Touch(first);
        Assert.Empty(recent.Snapshot());
        config.QuickTagRecentCount = 3;
        QuickTagSettings.ApplyRecentLimit(recent, config);
        Assert.Empty(recent.Snapshot()); // Re-enabling never resurrects discarded history.
        recent.Touch(second);
        Assert.Equal(new[] { second }, recent.Snapshot());
    }

    [Fact]
    public void RecentLimitAlsoClampsAnUnvalidatedConfig()
    {
        TagRecentList recent = new();
        Config config = new() { QuickTagRecentCount = int.MaxValue };
        QuickTagSettings.ApplyRecentLimit(recent, config);
        Assert.Equal(TagContextOptions.MaximumRecentCount, recent.Capacity);
        config.QuickTagRecentCount = int.MinValue;
        QuickTagSettings.ApplyRecentLimit(recent, config);
        Assert.Equal(0, recent.Capacity);
    }

    [Fact]
    public void TheGalleryOwnerUsesProfileOptionsRatherThanUnconditionalDefaults()
    {
        string host = File.ReadAllText(Path.Combine(Root(), "Desktop/Shell/MainWindow.GalleryTags.cs"));
        Assert.Contains("QuickTagSettings.NewRecentList(_config)", host, StringComparison.Ordinal);
        Assert.Contains("QuickTagSettings.SurfaceOptions(window._config)", host, StringComparison.Ordinal);
        Assert.Contains("QuickTagSettings.ContextOptions(window._config)", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Options => QuickTagOptions.Default", host, StringComparison.Ordinal);
        Assert.DoesNotContain("ContextOptions => TagContextOptions.Default", host, StringComparison.Ordinal);
        string apply = File.ReadAllText(Path.Combine(Root(), "Desktop/Shell/MainWindow.GalleryPreferences.cs"));
        Assert.Contains("QuickTagSettings.ApplyRecentLimit(recent, _config)", apply, StringComparison.Ordinal);
    }

    private static string Root([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../.."));
}
