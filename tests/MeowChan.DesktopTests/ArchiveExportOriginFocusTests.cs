using System.Runtime.CompilerServices;

namespace MeowChan.DesktopTests;

// Source regressions only: the native focus return is proven by the download-session smoke.
public sealed class ArchiveExportOriginFocusTests
{
    [Fact]
    public void ClosingExportResolvesTheOriginThroughItsToolbarPresentation()
    {
        string source = Source("Desktop/Views/Archive/ArchivePage.Export.cs");
        int close = source.IndexOf("private void OnExportClose(", StringComparison.Ordinal);
        int next = source.IndexOf("private async void OnExportOpen(", StringComparison.Ordinal);
        Assert.True(close >= 0 && next > close);
        string body = source[close..next];
        // An action in a closed More popup cannot take focus; Focus() on it silently left no focus at all.
        Assert.Contains("ActionOriginFocusTarget(_exportOrigin)?.Focus()", body);
        Assert.DoesNotContain("_exportOrigin?.Focus()", body);
    }

    [Fact]
    public void OriginResolverCoversEveryActionToolbarAndKeepsTheGalleryEntryPoint()
    {
        string source = Source("Desktop/Views/Archive/ArchivePage.ActionToolBar.cs");
        Assert.Contains("GalleryOriginFocusTarget(Control? origin) => ActionOriginFocusTarget(origin)", source);
        Assert.Contains("GetLogicalAncestors().OfType<ActionToolBar>()", source);
    }

    [Fact]
    public void SmokeChecksFocusReturnForBothDirectAndOverflowedOrigin()
    {
        string source = Source("tests/MeowChan.DesktopSmoke/ArchiveExportChecks.cs");
        Assert.Contains("await CheckExportOriginFocusAsync(fixture, saved.Summary.Id);", source);
        Assert.Contains("foreach (bool overflow in new[] { false, true })", source);
        Assert.Contains("Control? expected = direct ? origin : overflowed ? toolbar.More : null;", source);
        Assert.Contains("ReferenceEquals(focused, expected)", source);
    }

    private static string Source(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../..", relative)));
}
