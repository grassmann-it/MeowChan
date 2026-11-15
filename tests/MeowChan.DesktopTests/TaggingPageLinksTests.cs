using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// PLAN-TAGGING-GALERIE-UX-1.1 §6, QW6 and T1: the way back from the gallery names the Tagging page and lands on the medium shown last, and
// the page links into the gallery (a "last shown" line, double-click and Enter). Window-bound parts are covered by the tagging-page and
// gallery-close smokes; the rules here are pure functions and source contracts.
[Collection("Localization")]
public sealed class TaggingPageLinksTests : IDisposable
{
    private static readonly ResourceId Thread = new("4chan", "g", "123");
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    public TaggingPageLinksTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    // ---- QW6: the way back to the Tagging page ------------------------------------------------------------------------------

    [Fact]
    public void TheTaggingTargetIsAppendedSoTheOldValuesStayAndItsLabelNamesThePage()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, new[] { GalleryReturnTarget.None, GalleryReturnTarget.Media, GalleryReturnTarget.Thread,
            GalleryReturnTarget.Archive, GalleryReturnTarget.Downloads }.Select(target => (int)target));
        Assert.Equal(5, (int)GalleryReturnTarget.Tagging);
        Assert.Equal("Back to Tagging page", Texts.T("command.gallery.close.tagging"));
        Texts.SetLanguage("de");
        Assert.Equal("Zurück zur Seite Taggen", Texts.T("command.gallery.close.tagging"));
        string viewer = Source("Desktop/GalleryViewer.cs");
        Assert.Contains("GalleryReturnTarget.Tagging => Texts.T(\"command.gallery.close.tagging\")", viewer);
    }

    [Fact]
    public void ARunStartedOnTheTaggingPageReturnsThereWithTheLastShownRowMarked()
    {
        string gallery = Source("Desktop/Shell/MainWindow.Gallery.cs");
        Assert.Contains("WorkspacePage.Tagging => GalleryReturnTarget.Tagging", gallery);
        Assert.Contains("TagQueueKey? lastTagged = EndGalleryTagging(persist: true);", gallery);
        Assert.Contains("if (restoreOrigin && closingTarget == GalleryReturnTarget.Tagging) ReturnToTaggingPage(lastTagged);", gallery);
        string queue = Source("Desktop/Shell/MainWindow.TagQueue.cs");
        Assert.Contains("private TagQueueKey? EndGalleryTagging(bool persist)", queue);
        Assert.Contains("_taggingPage.SelectKey(key);", queue);
        // Started elsewhere (the Media page, the ⋯ menu of another page), the way back is what it was: no case of its own.
        Assert.Contains("WorkspacePage.Media => GalleryReturnTarget.Media,", gallery);
        string page = Source("Desktop/Views/Tagging/TaggingPage.Commands.cs");
        Assert.Contains("internal bool SelectKey(TagQueueKey key)", page);
        Assert.Contains("Filter = TaggingQueueFilter.All; // re-renders the rows", page); // a filter that hides the row never hides the way back
        Assert.Contains("ApplyPendingSelect();", Source("Desktop/Views/Tagging/TaggingPage.cs")); // a page still loading selects when it has the queue
    }

    // ---- T1: the page links into the gallery --------------------------------------------------------------------------------

    private static TagQueueItem Item(int n, TagQueueState state = TagQueueState.Open, string? name = null) =>
        new(Thread, "/media/123", "asset-" + n, (n % 16).ToString("x") + new string('a', 63), 100 + n, "media-" + n, "original",
            "p" + n, null, null, null, false, null, name ?? "file-" + n + ".png", SavedMediaKind.Image, state, T0.AddSeconds(n));

    private static TagQueueSnapshot Queue(int cursor, params TagQueueItem[] items) =>
        new(1, cursor, Array.Empty<string>(), items, TagQueueLoadState.Ready);

    [Fact]
    public void TheLastShownLineNamesTheFileAndPositionOnlyOnceTheWalkHasBegun()
    {
        // Fresh queue (cursor at the first entry, nothing decided): "Start tagging" says it all.
        Assert.Null(TaggingQueueView.LastShownText(Queue(0, Item(1), Item(2), Item(3))));
        Assert.Null(TaggingQueueView.LastShownText(TagQueueSnapshot.Empty));
        // The cursor stands on the third of four entries.
        TagQueueSnapshot walking = Queue(2, Item(1, TagQueueState.Done), Item(2, TagQueueState.Skipped), Item(3), Item(4));
        Assert.Equal(2, TaggingQueueView.LastShownIndex(walking));
        Assert.Equal("Last shown: file-3.png · position 3 of 4", TaggingQueueView.LastShownText(walking));
        // It is the entry "continue" starts at: a cursor on a decided entry moves on to the next open one.
        Assert.Equal("Last shown: file-3.png · position 3 of 4",
            TaggingQueueView.LastShownText(Queue(1, Item(1, TagQueueState.Done), Item(2, TagQueueState.Skipped), Item(3), Item(4))));
        // Something decided at the very start: the cursor is 0 but the walk has begun.
        Assert.Equal("Last shown: file-2.png · position 2 of 2", TaggingQueueView.LastShownText(Queue(0, Item(1, TagQueueState.Done), Item(2))));
        // Nothing open any more: no link to continue at.
        Assert.Null(TaggingQueueView.LastShownText(Queue(1, Item(1, TagQueueState.Done), Item(2, TagQueueState.Done))));
        // An entry without a name shows its asset id, like the list does.
        Assert.Equal("Last shown: asset-5 · position 2 of 2", TaggingQueueView.LastShownText(Queue(1, Item(4, TagQueueState.Done), Item(5, name: ""))));
        Texts.SetLanguage("de");
        Assert.Equal("Zuletzt gezeigt: file-3.png · Position 3 von 4", TaggingQueueView.LastShownText(walking));
    }

    [Fact]
    public void TheLinkContinuesLikeTheStartButtonAndIsLeftOutOfBarebonesWhoseStartButtonSaysTheSame()
    {
        WorkViewEntry row = WorkViewPolicy.Get("tagging.last-shown");
        Assert.Equal(Presence.Hidden, row.Barebones);
        Assert.Equal(Presence.Direct, row.Simple);
        Assert.Equal(Presence.Direct, row.Advanced);
        Assert.Equal("tagging.start", row.Alternative);
        Assert.Equal(Presence.Required, WorkViewPolicy.Resolve("tagging.start", WorkView.Barebones));
        string page = Source("Desktop/Views/Tagging/TaggingPage.cs");
        Assert.Contains("_lastShown.Click += (_, _) => OpenActions.Start(() => StartAsync(null), \"tagging.start\");", page);
        string view = Source("Desktop/Views/Tagging/TaggingPage.WorkView.cs");
        Assert.Contains("WorkViewElement.SetId(_lastShown, \"tagging.last-shown\");", view);
        Assert.Contains("WorkViewGate.Apply(_lastShown, _workView,", view);
    }

    [Fact]
    public void ADoubleClickAndEnterOnARowContinueThereAndSayWhyWhenTheRowIsNotOpen()
    {
        string commands = Source("Desktop/Views/Tagging/TaggingPage.Commands.cs");
        Assert.Contains("_list.DoubleTapped += (_, e) =>", commands);
        Assert.Contains("OpenActions.Start(() => ExecuteRowCommandAsync(\"tagging.continue-here\"), \"tagging.list.doubleClick\");", commands);
        Assert.Contains("TaggingCommands.ContinueHere => StartAsync(lead.Index),", commands);
        Assert.Contains("ShowStatus(Texts.T(\"tagging.page.continue.notOpen\"));", commands);
        Assert.Equal("Enter", MeowChan.Desktop.Commands.CommandCatalog.Get("tagging.continue-here").Shortcut);
    }

    // ---- texts: twelve languages --------------------------------------------------------------------------------------------

    private static readonly (string Key, string[] Placeholders)[] NewKeys =
    {
        ("command.gallery.close.tagging", Array.Empty<string>()),
        ("viewer.help.escape.tagging", Array.Empty<string>()),
        ("tagging.page.lastShown", new[] { "{0}", "{1}", "{2}" }),
        ("tagging.page.lastShown.help", Array.Empty<string>()),
        ("tagging.page.continue.notOpen", Array.Empty<string>()),
    };

    [Fact]
    public void EveryNewTextExistsInTwelveLanguagesWithItsPlaceholders()
    {
        foreach ((string key, string[] placeholders) in NewKeys)
        {
            Assert.True(BuiltInCatalog.English.TryGetValue(key, out string? english), key);
            Assert.True(BuiltInCatalog.German.TryGetValue(key, out string? german), key);
            foreach (string text in new[] { english!, german! })
            {
                Assert.False(string.IsNullOrWhiteSpace(text), key);
                foreach (string placeholder in placeholders) Assert.Contains(placeholder, text);
            }
        }
        string[] files = Directory.GetFiles(Root("Desktop/Localization/Packs"), "*.json");
        Assert.Equal(10, files.Length);
        foreach (string file in files)
        {
            using JsonDocument pack = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement strings = pack.RootElement.GetProperty("strings");
            foreach ((string key, string[] placeholders) in NewKeys)
            {
                Assert.True(strings.TryGetProperty(key, out JsonElement value), Path.GetFileName(file) + ": " + key);
                string text = value.GetString() ?? string.Empty;
                Assert.False(string.IsNullOrWhiteSpace(text), Path.GetFileName(file) + ": " + key);
                foreach (string placeholder in placeholders) Assert.Contains(placeholder, text);
            }
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------

    private static int CountOf(string text, string needle) => (text.Length - text.Replace(needle, "", StringComparison.Ordinal).Length) / needle.Length;

    private static string Source(string relative) => File.ReadAllText(Root(relative));

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}
