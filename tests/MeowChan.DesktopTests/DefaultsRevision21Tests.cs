using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Revision 21 (Tom's decision #132 of 16.11.2026, sweep S3): B is no longer a second default key of "Add favorite". M is the only default; U removes and F toggles.
// Taking a default away cannot silence a saved binding, so there is nothing new to yield: a profile that saved B for "Add favorite" (an "add another shortcut" freezes
// the whole list) keeps it, a profile that saved another command on B now has the key to itself, and no saved profile is rewritten. Plain objects; no window.
[Collection("Localization")]
public sealed class DefaultsRevision21Tests : IDisposable
{
    public DefaultsRevision21Tests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    private static readonly string[] FavoriteContexts = { "archive", "gallery", "media", "reader" };

    private static KeyEventArgs Press(Key key, KeyModifiers modifiers = KeyModifiers.None) => new() { Key = key, KeyModifiers = modifiers };

    [Theory, MemberData(nameof(Platforms))]
    public void MIsTheOnlyDefaultOfAddFavoriteAndNoCommandHasPlainBAnyMore(ShortcutPlatform platform)
    {
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 21);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        Assert.Empty(bindings.Issues);
        Assert.Equal(new[] { "M" }, bindings.For("favorites.add").Select(gesture => gesture.Key).ToArray());
        Assert.Equal(new[] { "U" }, bindings.For("favorites.remove").Select(gesture => gesture.Key).ToArray());
        Assert.Equal(new[] { "F" }, bindings.For("favorites.toggle").Select(gesture => gesture.Key).ToArray());
        Assert.Equal("M", bindings.Display("favorites.add"));
        foreach (string context in FavoriteContexts)
        {
            Assert.True(bindings.Matches(Press(Key.M), "favorites.add", context), context);
            Assert.False(bindings.Matches(Press(Key.B), "favorites.add", context), context);
        }
        // B is free everywhere: not a default of any command, with or without Shift.
        foreach (CommandDefinition command in CommandCatalog.All)
            Assert.DoesNotContain(EffectiveShortcutBindings.Defaults(command, platform), gesture => gesture.Key == "B" && gesture.Modifiers.Count == 0);
        // The add key is still a yielding default (M).
        Assert.True(EffectiveShortcutBindings.YieldsOn("favorites.add", platform));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void AProfileThatSavedBForAddFavoriteKeepsItInEveryContext(ShortcutPlatform platform)
    {
        // The frozen list of an "add another shortcut" (M and B), and B alone: both stay exactly as saved.
        foreach (string[] saved in new[] { new[] { "M", "B" }, new[] { "B" } })
        {
            ShortcutBindingsSection section = new ShortcutBindingsSection().Set("favorites.add", saved.Select(ShortcutGesture.Parse).ToArray());
            string before = section.Raw.GetRawText();
            EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, platform);
            Assert.Equal(saved, bindings.For("favorites.add").Select(gesture => gesture.Key).ToArray());
            Assert.Empty(bindings.Issues);
            foreach (string context in FavoriteContexts)
                Assert.True(bindings.Matches(Press(Key.B), "favorites.add", context), context);
            Assert.Equal(saved.Contains("M"), bindings.Matches(Press(Key.M), "favorites.add", "gallery"));
            Assert.Equal(before, section.Raw.GetRawText());
        }
    }

    [Theory, MemberData(nameof(Platforms))]
    public void ASavedBindingOfAnotherCommandOnBHasTheKeyToItselfWithoutAnyIssue(ShortcutPlatform platform)
    {
        // Revision 13 to 20 made "Add favorite" yield here (an informational "new default yields" issue and a hidden B); now there is no default left on B.
        ShortcutBindingsSection section = new ShortcutBindingsSection().Set("gallery.slideshow-toggle", [ShortcutGesture.Parse("B")]);
        string before = section.Raw.GetRawText();
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, platform);
        Assert.Empty(bindings.Issues);
        Assert.True(bindings.Matches(Press(Key.B), "gallery.slideshow-toggle", "gallery"));
        Assert.Empty(bindings.YieldedContexts("favorites.add", ShortcutGesture.Parse("B")));
        Assert.Equal(new[] { "M" }, bindings.For("favorites.add").Select(gesture => gesture.Key).ToArray());
        Assert.Equal(before, section.Raw.GetRawText());
    }

    [Theory, MemberData(nameof(Platforms))]
    public void ABindingSavedForAddFavoriteOrItsUnbindingIsKeptAsItIs(ShortcutPlatform platform)
    {
        EffectiveShortcutBindings own = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("favorites.add", [ShortcutGesture.Parse("F9")]), platform);
        Assert.Equal(new[] { "F9" }, own.For("favorites.add").Select(gesture => gesture.Key).ToArray());
        Assert.Empty(own.Issues);
        EffectiveShortcutBindings unbound = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("favorites.add", []), platform);
        Assert.Empty(unbound.For("favorites.add"));
        Assert.Empty(unbound.Issues);
    }

    [Fact]
    public void TheStarTooltipsNameTheToggleAndEveryActiveAlias()
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux);
        Assert.Equal(Texts.T("viewer.favorite.add") + " · F / M", FavoriteKeys.Tooltip(false, "gallery", bindings));
        Assert.Equal(Texts.T("viewer.favorite.remove") + " · F / U", FavoriteKeys.Tooltip(true, "gallery", bindings));
        // A profile with the old frozen list (M and B) keeps both saved gestures visible: current UX names every active alias.
        EffectiveShortcutBindings frozen = EffectiveShortcutBindings.Build(
            new ShortcutBindingsSection().Set("favorites.add", [ShortcutGesture.Parse("M"), ShortcutGesture.Parse("B")]), ShortcutPlatform.Linux);
        Assert.Equal(Texts.T("viewer.favorite.add") + " · F / M / B", FavoriteKeys.Tooltip(false, "media", frozen));
    }
}
