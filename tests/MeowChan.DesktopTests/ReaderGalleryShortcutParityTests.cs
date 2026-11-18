using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class ReaderGalleryShortcutParityTests
{
    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(p => new object[] { p });

    [Theory, MemberData(nameof(Platforms))]
    public void ReaderPostFocus_QReachesTaggingQueue(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(new(), platform);
        Assert.Contains("reader", EffectiveShortcutBindings.ExecutionContexts("media.queue.add"));
        using (EffectiveShortcutBindings.EnterKeyContext("reader"))
            Assert.True(bindings.Matches(new() { Key = Key.Q, Source = new ListBoxItem() }, "media.queue.add"));
    }

    [Fact]
    public void SidebarMediumFocus_HRoutesTheCatalogPostHideCommand()
    {
        // H is post.hide in the existing catalog, not a new help/history alias.
        Assert.Equal("H", CommandCatalog.Get("post.hide").Shortcut);
        Assert.Contains("OnPostFilterKeyDown(sidebar, e)", Source("ArchivePage.MediaSidebar.cs"));
        Assert.Contains("sender as Control ?? ArchiveReaderPane", Source("ArchivePage.PostFilter.cs"));
    }

    [Fact]
    public void InlineVideoFocus_EmbeddedReaderHasAGuardedRouteBeforeTypeahead()
    {
        string inline = Source("ArchivePage.InlineVideo.cs");
        string route = inline[inline.IndexOf("private bool TryRouteReaderVideoKey", StringComparison.Ordinal)..];
        route = route[..route.IndexOf("// The sidebar's own focus", StringComparison.Ordinal)];
        Assert.DoesNotContain("!_largeReader", route);
        Assert.Contains("TryRouteReaderVideoKey(e)", Source("ArchivePage.Reader.cs")[..Source("ArchivePage.Reader.cs").IndexOf("private async void OnReaderKeyDown", StringComparison.Ordinal)]);
        Assert.Contains("bool needsGallery = true, bool largeReaderOnly = true", Source("ArchivePage.Reader.cs"));
        string reader = Source("ArchivePage.Reader.cs");
        string observer = reader[reader.IndexOf("private void ObserveReaderMediaKeys", StringComparison.Ordinal)..];
        observer = observer[..observer.IndexOf("private void StopObservingReaderMediaKeys", StringComparison.Ordinal)];
        Assert.DoesNotContain("!_largeReader", observer);
        Assert.Contains("owner.AddHandler(KeyUpEvent, OnReaderMediaKeyUp", observer);
    }

    [Fact]
    public void MediumActivationCarriesAdmissionPolicyThroughEveryGuard()
    {
        string inline = Source("ArchivePage.InlineVideo.cs");
        Assert.Contains("const bool largeReaderOnly = false;", inline);
        Assert.Contains("ReaderMediaContextIsCurrent(source, post, needsGallery: false, largeReaderOnly: largeReaderOnly)", inline);
        Assert.Contains("TryActivatePostMedium(e, post, source, largeReaderOnly)", inline);
        Assert.Contains("cancellation, row, largeReaderOnly)", Source("ArchivePage.MediaActivation.cs"));
        string reader = Source("ArchivePage.Reader.cs");
        Assert.Contains("ArchiveAttachmentRow? only = null, bool largeReaderOnly = true)", reader);
        Assert.Contains("ReaderMediaContextIsCurrent(origin, post, largeReaderOnly: largeReaderOnly)", reader);
    }

    [Fact]
    public void FocusedMediumGalleryRoutePrecedesGenericThreadGalleryFallback()
    {
        string source = Source("ArchivePage.Reader.cs");
        Assert.Contains("TryOpenFocusedReaderMedium(e)", source);
        Assert.Contains("StartReaderMediumGalleryOpen(source, post, row, largeReaderOnly)", Source("ArchivePage.MediaActivation.cs"));
    }

    [Fact]
    public void EmbeddedReaderContextEntersBeforePageTunnelHandlers()
    {
        string source = Source("ArchivePage.axaml.cs");
        int constructor = source.IndexOf("public ArchivePage()", StringComparison.Ordinal);
        int handlers = source.IndexOf("AttachMouseKeyboardQuickWins();", constructor, StringComparison.Ordinal);
        int context = source.IndexOf("KeyContextHost.Install(this", constructor, StringComparison.Ordinal);
        Assert.True(handlers > constructor && context > handlers,
            "The focus-decided context host must be installed last so its tunnel runs first.");
    }

    [Fact]
    public void ReaderMediaListsUseTheReaderGalleryCommandForBothPresentationAndRouting()
    {
        string reader = Source("ArchivePage.Reader.cs");
        string gallery = Source("ArchivePage.Gallery.cs");
        Assert.Contains("CommandBindings.BindExisting(media, \"gallery.open\")", reader);
        Assert.DoesNotContain("CommandBindings.BindExisting(media, \"media.open\")", reader);
        Assert.Contains("CommandBindings.Matches(e, \"gallery.open\")", gallery);
        Assert.DoesNotContain("CommandBindings.Matches(e, \"media.open\")", gallery);
    }

    [Theory, MemberData(nameof(Platforms))]
    public void GalleryFocus_QueueAndVideoBindingsStayAvailable(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(new(), platform);
        using (EffectiveShortcutBindings.EnterKeyContext("gallery"))
        {
            Assert.True(bindings.Matches(new() { Key = Key.Q }, "media.queue.add"));
            Assert.True(bindings.Matches(new() { Key = Key.Space }, "video.play-pause"));
            Assert.True(bindings.Matches(new() { Key = Key.M, KeyModifiers = KeyModifiers.Shift }, "video.mute"));
            Assert.True(bindings.Matches(new() { Key = Key.L }, "video.loop"));
        }
    }

    [Theory, MemberData(nameof(Platforms))]
    public void TagInputFocus_RetainsLettersAndNativeActivation(ShortcutPlatform platform)
    {
        FieldInfo current = typeof(ShortcutService).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!;
        var original = ShortcutService.Current;
        try
        {
            current.SetValue(null, EffectiveShortcutBindings.Build(new(), platform));
            using (EffectiveShortcutBindings.EnterKeyContext("reader"))
                foreach (var (key, mods, id) in new[] { (Key.Q, KeyModifiers.None, "media.queue.add"),
                    (Key.H, KeyModifiers.None, "post.hide"), (Key.L, KeyModifiers.None, "video.loop"),
                    (Key.M, KeyModifiers.Shift, "video.mute"), (Key.Enter, KeyModifiers.Shift, "gallery.open"),
                    (Key.Enter, KeyModifiers.None, "gallery.open") })
                    Assert.False(CommandBindings.Matches(new() { Key = key, KeyModifiers = mods, Source = new TextBox() }, id));
            Assert.True(CommandRouter.IsNativeInput(new Button(), Key.Enter));
        }
        finally { current.SetValue(null, original); }
    }

    [Theory, MemberData(nameof(Platforms))]
    public void PrimaryUsesCommandOnMacButExplicitCtrlNeverDoes(ShortcutPlatform platform)
    {
        KeyModifiers primary = platform == ShortcutPlatform.MacOS ? KeyModifiers.Meta : KeyModifiers.Control;
        Assert.Equal(primary | KeyModifiers.Shift, ShortcutGesture.Parse("Primary+Shift+T").Resolve(platform).KeyModifiers);
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, ShortcutGesture.Parse("Ctrl+Shift+T").Resolve(platform).KeyModifiers);
        var bindings = EffectiveShortcutBindings.Build(new(), platform);
        using (EffectiveShortcutBindings.EnterKeyContext("reader"))
        {
            Assert.True(bindings.Matches(new() { Key = Key.T, KeyModifiers = primary | KeyModifiers.Shift }, "media.tags.edit"));
            Assert.False(bindings.Matches(new() { Key = Key.T, KeyModifiers = (platform == ShortcutPlatform.MacOS ? KeyModifiers.Control : KeyModifiers.Meta) | KeyModifiers.Shift }, "media.tags.edit"));
        }
    }

    [Theory, MemberData(nameof(Platforms))]
    public void OneGalleryCommandHasEnterAndLegacyAliasOnEveryPlatform(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Empty(bindings.Issues);
        Assert.Equal("gallery.open", CommandCatalog.Get("media.open").Id);
        Assert.DoesNotContain(CommandCatalog.All, command => command.Id == "media.open");
        Assert.Equal("Enter", CommandCatalog.Get("gallery.open").Shortcut);
        foreach (KeyModifiers modifiers in new[] { KeyModifiers.None, KeyModifiers.Shift })
            Assert.True(bindings.Matches(new() { Key = Key.Enter, KeyModifiers = modifiers }, "gallery.open"));
    }

    [Theory]
    [InlineData("media.open")]
    [InlineData("gallery.open")]
    public void RebindingEitherNameRemovesBothOldKeys(string id)
    {
        var bindings = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set(id, [ShortcutGesture.Parse("Alt+O")]));
        Assert.True(bindings.Matches(new() { Key = Key.O, KeyModifiers = KeyModifiers.Alt }, "gallery.open"));
        Assert.False(bindings.Matches(new() { Key = Key.Enter }, "gallery.open"));
        Assert.False(bindings.Matches(new() { Key = Key.Enter, KeyModifiers = KeyModifiers.Shift }, "gallery.open"));
        Assert.Equal(bindings.For("gallery.open"), bindings.For("media.open"));
    }

    [Fact]
    public void ExplicitGalleryOverrideWinsOverLegacyMediaOverrideIncludingUnbound()
    {
        var bindings = EffectiveShortcutBindings.Build(new ShortcutBindingsSection()
            .Set("media.open", [ShortcutGesture.Parse("Alt+O")]).Set("gallery.open", []));
        Assert.Empty(bindings.For("gallery.open"));
        Assert.Empty(bindings.For("media.open"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.CommandId == "media.open");
    }

    [Fact]
    public void LegacySavedBindingMigratesIntoTheSingleEditableRowAndResetClearsIt()
    {
        using var json = System.Text.Json.JsonDocument.Parse(
            """{"SchemaVersion":1,"Overrides":{"media.open":{"Mode":"Replace","Gestures":[{"Key":"O","Modifiers":["Alt"]}]}},"FutureData":"retained"}""");
        var old = new ShortcutBindingsSection(json.RootElement);
        var draft = new ShortcutDraftSession(old);
        Assert.True(draft.Draft.HasOverride("gallery.open"));
        Assert.False(draft.Draft.HasOverride("media.open"));
        Assert.Equal("retained", draft.Draft.Raw.GetProperty("FutureData").GetString());
        Assert.Equal("Alt+O", draft.Snapshot(ShortcutPlatform.Linux).Display("gallery.open"));
        Assert.False(draft.Draft.ResetKnown().HasOverride("gallery.open"));
        Assert.True(old.HasOverride("media.open")); // Input object was not mutated.
    }

    private static string Source(string name, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../Desktop/Views/Archive", name)));
}
