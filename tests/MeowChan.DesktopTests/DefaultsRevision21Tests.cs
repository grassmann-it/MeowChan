using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Revision 21 (Tom's decision #132 of 16.11.2026, sweep S3) made M the only default of "Add favorite".
// Revision 32 gave B to "Background" in the gallery. That default yields to a saved binding on B without rewriting the profile.
[Collection("Localization")]
public sealed class DefaultsRevision21Tests : IDisposable
{
    public DefaultsRevision21Tests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    private static readonly string[] FavoriteContexts = { "archive", "gallery", "media", "reader" };

    private static KeyEventArgs Press(Key key, KeyModifiers modifiers = KeyModifiers.None) => new() { Key = key, KeyModifiers = modifiers };

    [Theory, MemberData(nameof(Platforms))]
    public void MIsTheOnlyDefaultOfAddFavoriteAndPlainBChangesTheGalleryBackground(ShortcutPlatform platform)
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
        CommandDefinition commandWithPlainB = Assert.Single(CommandCatalog.All, command => EffectiveShortcutBindings.Defaults(command, platform)
            .Any(gesture => gesture.Key == "B" && gesture.Modifiers.Count == 0));
        Assert.Equal("gallery.background", commandWithPlainB.Id);
        Assert.True(bindings.Matches(Press(Key.B), "gallery.background", "gallery"));
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
            AssertBackgroundYieldsTo(bindings, "favorites.add");
            foreach (string context in FavoriteContexts)
                Assert.True(bindings.Matches(Press(Key.B), "favorites.add", context), context);
            Assert.Equal(saved.Contains("M"), bindings.Matches(Press(Key.M), "favorites.add", "gallery"));
            Assert.Equal(before, section.Raw.GetRawText());
        }
    }

    [Theory, MemberData(nameof(Platforms))]
    public void ASavedBindingOfAnotherGalleryCommandOnBAlsoWinsOverTheBackgroundDefault(ShortcutPlatform platform)
    {
        ShortcutBindingsSection section = new ShortcutBindingsSection().Set("gallery.slideshow-toggle", [ShortcutGesture.Parse("B")]);
        string before = section.Raw.GetRawText();
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, platform);
        AssertBackgroundYieldsTo(bindings, "gallery.slideshow-toggle");
        Assert.True(bindings.Matches(Press(Key.B), "gallery.slideshow-toggle", "gallery"));
        Assert.Empty(bindings.YieldedContexts("favorites.add", ShortcutGesture.Parse("B")));
        Assert.Equal(new[] { "M" }, bindings.For("favorites.add").Select(gesture => gesture.Key).ToArray());
        Assert.Equal(before, section.Raw.GetRawText());
    }

    private static void AssertBackgroundYieldsTo(EffectiveShortcutBindings bindings, string savedCommand)
    {
        ShortcutIssue issue = Assert.Single(bindings.Issues);
        Assert.Equal("defaultYields", issue.Reason?.Code);
        Assert.Equal("gallery.background", issue.CommandId);
        Assert.Equal(savedCommand, issue.OtherCommandId);
        Assert.False(issue.BlocksSave);
        Assert.False(bindings.Matches(Press(Key.B), "gallery.background", "gallery"));
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
