using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// PT3: the keys of the tagging mode (Enter done and next, Space skip, Backspace back, Shift+Space play/pause) are catalog commands of their
// own execution context, so they never compete with Space/Enter/Backspace of the plain gallery; the stamp key is wired in the panel; the
// texts exist in twelve languages; and the viewer and window hooks route, hold and persist as the plan says. The strip and the panel need
// a platform (the tagging-mode smoke covers them).
[Collection("Localization")]
public sealed class TaggingModeKeyRouteTests : IDisposable
{
    public TaggingModeKeyRouteTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly string[] ModeIds = { "tags.queue.done-next", "tags.queue.skip", "tags.queue.back", "tags.queue.play-pause" };

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    [Theory]
    [MemberData(nameof(Platforms))]
    public void TheModeKeysAreEnterSpaceBackspaceAndShiftSpaceOnEveryPlatformWithoutIssues(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Empty(bindings.Issues);
        foreach (string id in ModeIds)
        {
            CommandDefinition command = CommandCatalog.Get(id);
            Assert.True(command.IsImplemented, id);
            Assert.Equal(CommandScope.Gallery, command.Scope);
            Assert.Equal(new[] { "gallery-tagging" }, command.ExecutionContexts);
            Assert.NotEmpty(bindings.For(id));
            foreach (ShortcutGesture gesture in bindings.For(id)) Assert.Null(EffectiveShortcutBindings.InvalidReason(gesture, platform));
        }
        var linux = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
        Assert.True(linux.Matches(new KeyEventArgs { Key = Key.Enter, KeyModifiers = KeyModifiers.None }, "tags.queue.done-next"));
        Assert.True(linux.Matches(new KeyEventArgs { Key = Key.Space, KeyModifiers = KeyModifiers.None }, "tags.queue.skip"));
        Assert.True(linux.Matches(new KeyEventArgs { Key = Key.Back, KeyModifiers = KeyModifiers.None }, "tags.queue.back"));
        Assert.True(linux.Matches(new KeyEventArgs { Key = Key.Space, KeyModifiers = KeyModifiers.Shift }, "tags.queue.play-pause"));
        Assert.False(linux.Matches(new KeyEventArgs { Key = Key.Space, KeyModifiers = KeyModifiers.Shift }, "tags.queue.skip"));
    }

    [Fact]
    public void TheOwnContextKeepsThePlainGalleryKeysAsTheyAre()
    {
        // Space still plays a video and Backspace still resets the speed outside the mode; Enter stays "read posts" in the archive.
        var bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
        Assert.Equal("Space", bindings.Display("video.play-pause"));
        Assert.Equal("Backspace", bindings.Display("video.speed-reset"));
        foreach (string id in ModeIds)
            foreach (string other in new[] { "video.play-pause", "video.speed-reset", "thread.read", "gallery.next", "media.open" })
                Assert.False(EffectiveShortcutBindings.ContextsOverlap(id, other), id + " / " + other);
        // The quick tag keys of the panel (Ctrl+Enter, the stamp) stay gallery keys; the mode reads them itself before the panel does.
        Assert.Equal(new[] { "gallery" }, CommandCatalog.Get(QuickTagCommands.CommitNext).ExecutionContexts);
    }

    [Fact]
    public void TheNewDefaultsAreOfRevisionEightAndYieldToASavedBinding()
    {
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 8);
        foreach (string id in ModeIds) Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
    }

    [Fact]
    public void TheStampKeyIsWiredInTheGalleryPanelAndNothingAwaitsAHandler()
    {
        Assert.Empty(QuickTagCommands.AwaitingHandler);
        Assert.Contains(QuickTagCommands.StampApply, GalleryTagPanelCommands.Handled);
        Assert.Equal(new Desktop.Controls.QuickTagKeyRoute(Desktop.Controls.QuickTagKeyAction.ApplyStamp), GalleryTagPanelCommands.RouteOf(QuickTagCommands.StampApply));
        Assert.True(CommandCatalog.Get(QuickTagCommands.StampApply).IsImplemented);
    }

    // ---- texts ----------------------------------------------------------------------------------------------------

    private static IEnumerable<string> Keys() => TaggingModeCatalog.Entries.Select(entry => entry.Key);

    [Fact]
    public void EveryTextIsInEnglishAndGermanWithTheSamePlaceholders()
    {
        foreach (string key in Keys())
        {
            Assert.True(BuiltInCatalog.English.TryGetValue(key, out string? english), key);
            Assert.True(BuiltInCatalog.German.TryGetValue(key, out string? german), key);
            Assert.NotEqual(english, german);
            foreach (string placeholder in new[] { "{0}", "{1}", "{2}" })
                Assert.Equal(english!.Contains(placeholder), german!.Contains(placeholder));
        }
        foreach (string id in ModeIds) Assert.Equal(CommandCatalog.Get(id).LabelEn, BuiltInCatalog.English["command." + id]);
    }

    [Fact]
    public void ThePackKeysAreInEveryBundledLanguageAsRealTranslationsWithTheirPlaceholders()
    {
        string[] codes = { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" };
        string packs = Path.Combine(RepoRoot(), "Desktop", "Localization", "Packs");
        foreach (string code in codes)
        {
            using System.Text.Json.JsonDocument pack = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(packs, code + ".json")));
            System.Text.Json.JsonElement texts = pack.RootElement.GetProperty("strings");
            foreach (string key in Keys())
            {
                Assert.True(texts.TryGetProperty(key, out System.Text.Json.JsonElement value), code + " lacks " + key);
                string text = value.GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(text), code + " " + key);
                foreach (string placeholder in new[] { "{0}", "{1}", "{2}" })
                    Assert.Equal(BuiltInCatalog.English[key].Contains(placeholder), text.Contains(placeholder));
                Assert.NotEqual(BuiltInCatalog.English[key], text);
            }
        }
    }

    // ---- hooks in the viewer and the window ---------------------------------------------------------------------

    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void TheViewerRoutesTheModeKeysFirstHoldsTheControlsAndCouplesThePanel()
    {
        string viewer = Source("Desktop", "GalleryViewer.cs");
        int mode = viewer.IndexOf("if (_tagging != null && RouteTaggingKey(e)) return;", StringComparison.Ordinal);
        int panel = viewer.IndexOf("if (_tagPanelOpen && RouteTagPanelKey(e)) return;", StringComparison.Ordinal);
        int playback = viewer.IndexOf("foreach (var (_, id) in PlaybackActions())", StringComparison.Ordinal);
        Assert.True(mode > 0 && mode < panel && panel < playback);
        Assert.Contains("_chrome.Child = WithTaggingStrip(toolbar);", viewer);
        Assert.Contains("AddTaggingHelp();", viewer);
        Assert.Contains("|| _tagging != null,", Source("Desktop", "GalleryViewer.Overlay.cs")); // never auto-hidden while the mode runs
        string tags = Source("Desktop", "GalleryViewer.TagPanel.cs");
        Assert.Contains("if (_tagging != null) CoupleTaggingPanel(surface, controller);", tags);
        Assert.Contains("UncoupleTaggingPanel(surface);", tags);
        string strip = Source("Desktop", "GalleryViewer.TaggingMode.cs");
        // Inside the tag panel only an empty field's Enter finishes; chips keep Space and Backspace.
        Assert.Contains("if (surface?.IsKeyboardFocusWithin == true)", strip);
        Assert.Contains("string.IsNullOrEmpty(surface.Input.Text)", strip);
        // A quick second key never decides a medium that is not shown yet.
        Assert.Contains("_navigationPending", strip);
        Assert.Contains("surface.StampApplied += OnTaggingStampApplied;", strip);
    }

    [Fact]
    public void TheWindowStartsTheModeFromTheQueueShowsTheQueuePositionAndKeepsTheResumePoint()
    {
        string queue = Source("Desktop", "Shell", "MainWindow.TagQueue.cs");
        Assert.Contains("await OpenTaggingWalkAsync(queue, walk, entries, first.EntryKey);", queue);
        Assert.Contains("TaggingWalk.Pair(walk, entries)", queue);
        Assert.Contains("StartGalleryTagging(tagging, entries);", queue);
        Assert.Contains("_session.SetTagQueueCursorAsync(position)", queue);
        string gallery = Source("Desktop", "Shell", "MainWindow.Gallery.cs");
        Assert.Contains("EndGalleryTagging(persist: true);", gallery);
        Assert.Contains("TaggingPositionText(_galleryEntries[_galleryIndex]) ??", gallery);
        // PT5 host glue: one stamp for every panel of the window, kept in the settings.
        string glue = Source("Desktop", "Shell", "MainWindow.GalleryTags.cs");
        Assert.Contains("TaggingSettings.StampOptions(_config).CreateStamp(_config.TagStamp)", glue); // PT7: the stamp size is a setting
        Assert.Contains("config.TagStamp = (string[])names.Clone()", glue);
        Assert.Contains("public QuickTagStamp? Stamp => window.QuickTagStampSet;", glue);
        Assert.Contains("new QuickTagSession(target, owner.Options.UndoDepth, owner.Stamp)", Source("Desktop", "GalleryTagPanelModel.cs"));
    }
}
