using System.Text.Json;
using Avalonia.Input;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

public sealed class ShortcutBindingsTests
{
    private static ShortcutBindingsSection Read(string json)
    { using var doc = JsonDocument.Parse(json); return new(doc.RootElement); }
    private static KeyEventArgs Key(Key key, KeyModifiers modifiers = KeyModifiers.None) => new() { Key = key, KeyModifiers = modifiers };

    [Theory]
    [InlineData(ShortcutPlatform.Linux, KeyModifiers.Control)]
    [InlineData(ShortcutPlatform.Windows, KeyModifiers.Control)]
    [InlineData(ShortcutPlatform.MacOS, KeyModifiers.Meta)]
    public void PrimaryIsLogicalAndExplicitControlStaysDistinct(ShortcutPlatform platform, KeyModifiers expected)
    {
        Assert.Equal(expected, ShortcutGesture.Parse("Primary+K").Resolve(platform).KeyModifiers);
        Assert.Equal(KeyModifiers.Control, ShortcutGesture.Parse("Control+K").Resolve(platform).KeyModifiers);
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void CurrentDefaultsAreConflictFree(ShortcutPlatform platform)
    { Assert.Empty(EffectiveShortcutBindings.Build(platform: platform).Issues); }

    [Fact]
    public void ReplaceRemovesEveryOldAlternativeAndUnboundSurvivesResetOfOthers()
    {
        var section = new ShortcutBindingsSection().Set("gallery.fit", new[] { ShortcutGesture.Parse("Primary+K") })
            .Set("gallery.actual-size", Array.Empty<ShortcutGesture>());
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.False(bindings.Matches(Key(Avalonia.Input.Key.D0), "gallery.fit"));
        Assert.False(bindings.Matches(Key(Avalonia.Input.Key.NumPad0), "gallery.fit"));
        Assert.True(bindings.Matches(Key(Avalonia.Input.Key.K, KeyModifiers.Control), "gallery.fit"));
        Assert.Empty(bindings.For("gallery.actual-size"));
        bindings = EffectiveShortcutBindings.Build(section.Set("gallery.fit", null));
        Assert.Equal(2, bindings.For("gallery.fit").Count);
        Assert.Empty(bindings.For("gallery.actual-size"));
    }

    [Fact]
    public void UnknownAndMalformedKnownBindingsArePreservedButNotActivated()
    {
        var section = Read("""{"SchemaVersion":1,"Overrides":{"future.command":{"future":[1,2]},"navigation.archive":{"Mode":"Replace","Gestures":[{"Key":"Unicorn","Modifiers":[]}]}}} """);
        var snapshot = EffectiveShortcutBindings.Build(section);
        Assert.Empty(snapshot.For("navigation.archive"));
        Assert.Contains(snapshot.Issues, i => i.CommandId == "navigation.archive" && i.BlocksSave);
        Assert.Contains(snapshot.Issues, i => i.CommandId == "future.command" && !i.BlocksSave);
        var reset = section.ResetKnown();
        Assert.True(reset.HasOverride("future.command"));
        Assert.False(reset.HasOverride("navigation.archive"));
        Assert.Equal(section.Raw.GetRawText(), section.Clone().Raw.GetRawText());
    }

    [Fact]
    public void FutureSchemaIsReadOnlyAndUsesDefaultsWithoutRewritingRawData()
    {
        var section = Read("""{"SchemaVersion":7,"Overrides":[1,2],"future":{"nested":true}}""");
        Assert.False(section.IsEditable);
        Assert.Throws<InvalidOperationException>(() => section.Set("navigation.archive", null));
        var snapshot = EffectiveShortcutBindings.Build(section);
        Assert.NotEmpty(snapshot.For("navigation.archive"));
        Assert.All(snapshot.Issues, i => Assert.False(i.BlocksSave));
    }

    [Theory]
    [InlineData("Escape", ShortcutPlatform.Linux)]
    [InlineData("Shift+Tab", ShortcutPlatform.Windows)]
    [InlineData("Meta+K", ShortcutPlatform.Windows)]
    [InlineData("Meta+Space", ShortcutPlatform.MacOS)]
    [InlineData("Control+Alt+E", ShortcutPlatform.Linux)]
    [InlineData("Alt+F4", ShortcutPlatform.Windows)]
    [InlineData("LeftCtrl", ShortcutPlatform.Linux)]
    public void UnsafeCaptureIsRejected(string gesture, ShortcutPlatform platform)
    { Assert.NotNull(EffectiveShortcutBindings.InvalidReason(ShortcutGesture.Parse(gesture), platform)); }

    [Fact]
    public void NativeTextCommandsRemainAvailableInTheirListContext()
    { Assert.Null(EffectiveShortcutBindings.InvalidReason(ShortcutGesture.Parse("Primary+C"), ShortcutPlatform.Linux)); }

    [Fact]
    public void CollisionDisablesOnlyCollidingGesturesForBothActions()
    {
        var section = new ShortcutBindingsSection().Set("navigation.archive", new[] { ShortcutGesture.Parse("F5"), ShortcutGesture.Parse("Primary+K") });
        var snapshot = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.False(snapshot.Matches(Key(Avalonia.Input.Key.F5), "navigation.archive"));
        Assert.Empty(snapshot.For("watch.check"));
        Assert.True(snapshot.Matches(Key(Avalonia.Input.Key.K, KeyModifiers.Control), "navigation.archive"));
        Assert.Equal(2, snapshot.RequestedGestures["navigation.archive"].Count);
        Assert.Contains(snapshot.Issues, i => i.OtherCommandId != null);
    }

    [Fact]
    public void ExclusiveListsAndViewersCanShareGestures()
    {
        Assert.False(EffectiveShortcutBindings.ContextsOverlap("archive.select-page", "selection.select-all"));
        Assert.False(EffectiveShortcutBindings.ContextsOverlap("reader.previous-thread", "gallery.previous"));
        Assert.True(EffectiveShortcutBindings.ContextsOverlap("navigation.archive", "gallery.previous"));
    }

    [Theory]
    [InlineData("sections.go-to", "plugins.save")]
    [InlineData("rules.template-apply", "rules.save")]
    [InlineData("archive.saved-searches.save-current", "archive.saved-searches.delete")]
    [InlineData("media.rating.set-1", "media.rating.clear")]
    [InlineData("media.rating.set-1", "reader.next-post")]
    public void IntegratedSurfacesRejectTwoCommandsWithTheSameGesture(string first, string second)
    {
        var gesture = new[] { ShortcutGesture.Parse("Primary+Shift+F9") };
        var section = new ShortcutBindingsSection().Set(first, gesture).Set(second, gesture);
        var result = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Contains(result.Issues, issue => issue.OtherCommandId != null);
        Assert.Empty(result.For(first));
        Assert.Empty(result.For(second));
    }

    [Theory]
    [InlineData("reader-groups.create", "navigation.archive")]
    [InlineData("media.rating.cleanup-retry", "media.rating.remove-selected")]
    [InlineData("sections.go-to", "archive.saved-searches.apply")]
    public void SeparateModalSurfacesMayReuseAGesture(string first, string second)
    {
        var gesture = new[] { ShortcutGesture.Parse("Primary+Shift+F9") };
        var result = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set(first, gesture).Set(second, gesture), ShortcutPlatform.Linux);
        Assert.Empty(result.Issues);
        Assert.Single(result.For(first));
        Assert.Single(result.For(second));
    }

    [Theory]
    [InlineData("video.mute")]
    [InlineData("video.loop")]
    public void VideoAudioAndRepeatCommandsHaveNoInventedDefaultsButAcceptGalleryOverrides(string id)
    {
        Assert.Equal(CommandAvailability.Contextual, CommandCatalog.Get(id).Availability);
        Assert.Empty(EffectiveShortcutBindings.Build().For(id));
        var bindings = new ShortcutBindingsSection().Set(id, new[] { ShortcutGesture.Parse("Primary+Shift+F9") });
        Assert.Single(EffectiveShortcutBindings.Build(bindings).For(id));
        Assert.Equal(new[] { "gallery" }, CommandCatalog.Get(id).ExecutionContexts);
    }

    [Theory]
    [InlineData("post.copy-link")]
    [InlineData("post.open-source")]
    [InlineData("viewer.focus-mode")]
    [InlineData("table.reset-layout")]
    [InlineData("admission.monitor-all")]
    public void CatalogPlaceholdersRetainOverridesWithoutPretendingToExecute(string id)
    {
        var section = new ShortcutBindingsSection().Set(id, new[] { ShortcutGesture.Parse("Primary+Shift+F9") });
        var result = EffectiveShortcutBindings.Build(section);
        Assert.Equal(CommandAvailability.Unavailable, CommandCatalog.Get(id).Availability);
        Assert.Empty(result.For(id));
        Assert.Single(result.RequestedGestures[id]);
        Assert.True(section.HasOverride(id));
        Assert.Contains(result.Issues, issue => issue.CommandId == id && !issue.BlocksSave);
    }

    [Fact]
    public void PlatformSwitchDetectsPrimaryExplicitControlCollision()
    {
        var section = new ShortcutBindingsSection().Set("navigation.archive", new[] { ShortcutGesture.Parse("Primary+K") })
            .Set("navigation.rules", new[] { ShortcutGesture.Parse("Control+K") });
        Assert.Empty(EffectiveShortcutBindings.Build(section, ShortcutPlatform.MacOS).Issues);
        Assert.NotEmpty(EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux).Issues);
    }

    [Fact]
    public void FixedEscapeIsNotAFreeBinding()
    {
        var bindings = EffectiveShortcutBindings.Build();
        Assert.Empty(bindings.For("reader.close"));
        Assert.Empty(bindings.For("gallery.close"));
        Assert.False(bindings.Matches(Key(Avalonia.Input.Key.Escape), "reader.close"));
    }

    [Fact]
    public void SymbolAndNumpadAlternativesAreInTheSnapshot()
    {
        var bindings = EffectiveShortcutBindings.Build();
        Assert.True(bindings.Matches(Key(Avalonia.Input.Key.NumPad1), "gallery.actual-size"));
        Assert.True(bindings.Matches(Key(Avalonia.Input.Key.Add), "gallery.zoom-in"));
        Assert.True(bindings.Matches(Key(Avalonia.Input.Key.Subtract), "gallery.zoom-out"));
        Assert.False(bindings.Matches(Key(Avalonia.Input.Key.F1, KeyModifiers.Alt), "viewer.shortcut-help"));
    }

    [Fact]
    public void OverrideLimitsQuarantineKnownEntry()
    {
        var section = new ShortcutBindingsSection().Set("gallery.fit", Enumerable.Repeat(ShortcutGesture.Parse("K"), 9).ToArray());
        Assert.Empty(EffectiveShortcutBindings.Build(section).For("gallery.fit"));
        Assert.NotEmpty(EffectiveShortcutBindings.Build(section).Issues);
    }

    [Fact]
    public void ConfigCloneAndSettingsMergeRetainRawSectionAndRecentViewChanges()
    {
        var config = new Config { ShortcutBindings = Read("""{"SchemaVersion":7,"future":{"x":[1,2,3]}}""") };
        var draft = config.Clone();
        config.GalleryContextOpen = true;
        config.CopySettingsFieldsFrom(draft);
        Assert.True(config.GalleryContextOpen);
        Assert.NotSame(config.ShortcutBindings, draft.ShortcutBindings);
        Assert.Equal(draft.ShortcutBindings.Raw.GetRawText(), config.ShortcutBindings.Raw.GetRawText());
        string serialized = JsonSerializer.Serialize(config, ConfigJsonContext.Default.Config);
        var loaded = JsonSerializer.Deserialize(serialized, ConfigJsonContext.Default.Config)!;
        Assert.Equal(config.ShortcutBindings.Raw.GetRawText(), loaded.ShortcutBindings.Raw.GetRawText());
    }

    [Fact]
    public void PersistedOverrideUsesExistingSettingsWriterAndReloads()
    {
        using TempDirectory directory = new();
        var config = new Config { ShortcutBindings = new ShortcutBindingsSection().Set("gallery.fit", Array.Empty<ShortcutGesture>()) };
        config.SaveConfig(directory.Path);
        var loaded = Config.LoadConfig(directory.Path);
        Assert.Empty(EffectiveShortcutBindings.Build(loaded.ShortcutBindings).For("gallery.fit"));
        Assert.Equal(1, loaded.SchemaVersion);
    }
}
