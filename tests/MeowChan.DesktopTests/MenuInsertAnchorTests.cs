using MeowChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>Review wave 6 N4 and N11: the app menu's tools group keeps "Reading history" directly behind "Continue reading"; texts name the Alt key of the platform.</summary>
public sealed class MenuInsertAnchorTests
{
    [Fact]
    public void PostBookmarksSitBehindTheReadingHistorySubmenuWhichSitsBehindContinueReading()
    {
        object tools = new(), resume = new(), history = new(), import = new();
        List<object> items = [tools, resume, history, import];
        Assert.Equal(3, MainWindow.InsertIndexBehind(items, 0, history, resume));
        items.Insert(MainWindow.InsertIndexBehind(items, 0, history, resume), "bookmarks");
        // The queue entry then goes behind the bookmarks, so the order is: Continue reading, Reading history, bookmarks, queue.
        items.Insert(MainWindow.InsertIndexBehind(items, 0, "bookmarks"), "queue");
        Assert.Equal([tools, resume, history, "bookmarks", "queue", import], items);
    }

    [Fact]
    public void WithoutTheHistorySubmenuTheEntryFollowsContinueReadingAndWithoutBothTheToolsAnchor()
    {
        object tools = new(), resume = new(), import = new();
        List<object> items = [tools, resume, import];
        Assert.Equal(2, MainWindow.InsertIndexBehind(items, 0, null, resume));
        // Absent entries (never inserted, or removed) fall through to the anchor of the tools group.
        Assert.Equal(1, MainWindow.InsertIndexBehind(items, 0, null, new object()));
        Assert.Equal(1, MainWindow.InsertIndexBehind(items, 0));
    }

    [Fact]
    public void TheAltKeyIsOptionOnMacAndAltElsewhere()
    {
        Assert.Equal("⌥", PlatformKeyNames.Alt(ShortcutPlatform.MacOS));
        Assert.Equal("Alt", PlatformKeyNames.Alt(ShortcutPlatform.Windows));
        Assert.Equal("Alt", PlatformKeyNames.Alt(ShortcutPlatform.Linux));
    }

    [Collection("Localization")]
    public sealed class ModifierTexts : IDisposable
    {
        public ModifierTexts() { Texts.Initialize(); Texts.SetLanguage("en"); }
        public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

        [Fact]
        public void TheTagListTextsTakeTheModifierFromTheCallerInAllTwelveLanguages()
        {
            foreach (string code in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
            {
                Texts.SetLanguage(code);
                foreach (string key in new[] { "media.tagList.tip", "media.tags.hint" })
                {
                    string mac = Texts.T(key, PlatformKeyNames.Alt(ShortcutPlatform.MacOS));
                    string other = Texts.T(key, PlatformKeyNames.Alt(ShortcutPlatform.Linux));
                    Assert.Contains("⌥+", mac, StringComparison.Ordinal);
                    Assert.DoesNotContain("Alt", mac, StringComparison.Ordinal);
                    Assert.Contains("Alt+", other, StringComparison.Ordinal);
                    Assert.DoesNotContain("{", other, StringComparison.Ordinal);
                }
            }
        }
    }
}
