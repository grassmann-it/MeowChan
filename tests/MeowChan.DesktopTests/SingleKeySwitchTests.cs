using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

/// <summary>The accessibility switch (WCAG 2.1.4): standard single-character shortcuts off, everything else and saved bindings stay.</summary>
public sealed class SingleKeySwitchTests
{
    private static ShortcutBindingsSection Off() => new ShortcutBindingsSection().WithSingleKeyShortcuts(false);

    [Fact]
    public void TheSwitchIsOnByDefaultAndStoredAsAnAdditiveFieldThatTheRestOfTheSectionKeeps()
    {
        var section = new ShortcutBindingsSection();
        Assert.True(section.SingleKeyShortcuts);
        var off = section.WithSingleKeyShortcuts(false);
        Assert.False(off.SingleKeyShortcuts);
        Assert.True(off.IsEditable);
        Assert.Contains("\"SingleKeyShortcuts\":false", off.Raw.GetRawText());
        // Editing bindings, resetting them or switching back neither loses nor leaves the field.
        Assert.False(off.Set("navigation.archive", [ShortcutGesture.Parse("Primary+9")]).SingleKeyShortcuts);
        Assert.False(off.ResetKnown().SingleKeyShortcuts);
        var on = off.WithSingleKeyShortcuts(true);
        Assert.True(on.SingleKeyShortcuts);
        Assert.DoesNotContain("SingleKeyShortcuts", on.Raw.GetRawText());
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var document = System.Text.Json.JsonDocument.Parse("""{"SchemaVersion":9,"Overrides":{}}""");
            new ShortcutBindingsSection(document.RootElement).WithSingleKeyShortcuts(false);
        });
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void TurnedOffNoStandardSingleCharacterGestureRemainsButEnterSpaceArrowsAndChordsDo(ShortcutPlatform platform)
    {
        var on = EffectiveShortcutBindings.Build(platform: platform);
        var off = EffectiveShortcutBindings.Build(Off(), platform);
        Assert.True(on.SingleKeyShortcuts);
        Assert.False(off.SingleKeyShortcuts);
        Assert.Empty(off.Issues);
        foreach (var command in CommandCatalog.All)
            Assert.DoesNotContain(off.For(command.Id), EffectiveShortcutBindings.IsSingleCharacterKey);
        foreach (string id in new[] { "favorites.toggle", "favorites.add", "favorites.remove", "gallery.context-toggle", "gallery.show-in-thread",
            "gallery.fit", "gallery.actual-size", "gallery.zoom-in", "gallery.zoom-out" })
        {
            Assert.NotEmpty(on.For(id));
            Assert.Empty(off.For(id));
        }
        foreach (string id in new[] { "video.play-pause", "thread.read", "gallery.open", "gallery.previous", "gallery.next", "reader.next-post",
            "watch.check", "navigation.media", "session.pause", "viewer.shortcut-help", "media.select-all", "thread.tags", "window.fullscreen" })
            Assert.NotEmpty(off.For(id));
        // Help keeps F1 (and ⌘? on macOS) when the typed "?" goes.
        Assert.DoesNotContain(off.For("viewer.shortcut-help"), g => g.Symbol == "?" && g.Modifiers.All(m => m == "Shift"));
    }

    [Fact]
    public void ASavedSingleKeyBindingIsTheUsersOwnChoiceAndStaysWhenTheSwitchIsOff()
    {
        var section = Off().Set("favorites.toggle", [ShortcutGesture.Parse("X")]);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Equal("X", bindings.Display("favorites.toggle"));
        Assert.False(bindings.SingleKeyShortcuts);
    }

    [Theory]
    [InlineData("F", true)] [InlineData("Shift+F", true)] [InlineData("5", true)] [InlineData("NumPad0", true)] [InlineData("Add", true)]
    [InlineData("OemMinus", true)] [InlineData("Space", false)] [InlineData("Enter", false)] [InlineData("Left", false)]
    [InlineData("Delete", false)] [InlineData("F5", false)] [InlineData("Primary+F", false)] [InlineData("Alt+T", false)]
    [InlineData("Apps", false)] [InlineData("PageDown", false)]
    public void OnlyBareCharacterKeysCountAsSingleKeys(string gesture, bool single) =>
        Assert.Equal(single, EffectiveShortcutBindings.IsSingleCharacterKey(ShortcutGesture.Parse(gesture)));
}
