using Avalonia.Input;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class GalleryFavoriteShortcutTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;

    public void Dispose() => Texts.SetLanguage(_previousLanguage);

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void FavoriteDefaultsAreImplementedGalleryOnlyAndConflictFree(ShortcutPlatform platform)
    {
        var add = CommandCatalog.Get("favorites.add");
        var remove = CommandCatalog.Get("favorites.remove");
        Assert.True(add.IsImplemented);
        Assert.True(remove.IsImplemented);
        Assert.Equal(CommandScope.Gallery, add.Scope);
        Assert.Equal(CommandScope.Gallery, remove.Scope);
        Assert.Equal(new[] { "gallery" }, add.ExecutionContexts);
        Assert.Equal(new[] { "gallery" }, remove.ExecutionContexts);

        var bindings = EffectiveShortcutBindings.Build(new(), platform);
        Assert.Empty(bindings.Issues);
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.M }, "favorites.add"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.U }, "favorites.remove"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.F }, "favorites.toggle"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.M }, "favorites.toggle"));
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void NewFavoriteDefaultsYieldToSavedExplicitGalleryBindings(ShortcutPlatform platform)
    {
        var section = new ShortcutBindingsSection()
            .Set("gallery.context-toggle", [ShortcutGesture.Parse("M")])
            .Set("gallery.close", [ShortcutGesture.Parse("U")]);
        string before = section.Raw.GetRawText();
        var bindings = EffectiveShortcutBindings.Build(section, platform);

        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.M }, "gallery.context-toggle"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.U }, "gallery.close"));
        Assert.Empty(bindings.For("favorites.add"));
        Assert.Empty(bindings.For("favorites.remove"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        Assert.Contains(bindings.Issues, issue => issue.CommandId == "favorites.add"
            && issue.OtherCommandId == "gallery.context-toggle" && !issue.BlocksSave);
        Assert.Contains(bindings.Issues, issue => issue.CommandId == "favorites.remove"
            && issue.OtherCommandId == "gallery.close" && !issue.BlocksSave);
        Assert.Equal(before, section.Raw.GetRawText());
    }

    [Fact]
    public void NewAddDefaultAlsoYieldsToASavedFavoriteToggleOverride()
    {
        var section = new ShortcutBindingsSection()
            .Set("favorites.toggle", [ShortcutGesture.Parse("M")]);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);

        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.M }, "favorites.toggle"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.F }, "favorites.toggle"));
        Assert.Empty(bindings.For("favorites.add"));
        Assert.Contains(bindings.Issues, issue => issue.CommandId == "favorites.add"
            && issue.OtherCommandId == "favorites.toggle" && !issue.BlocksSave);
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void ExplicitFavoriteConflictsStillRejectBothActionsSymmetrically(ShortcutPlatform platform)
    {
        var addSection = new ShortcutBindingsSection()
            .Set("favorites.add", [ShortcutGesture.Parse("M")])
            .Set("gallery.context-toggle", [ShortcutGesture.Parse("M")]);
        var addBindings = EffectiveShortcutBindings.Build(addSection, platform);
        Assert.Empty(addBindings.For("favorites.add"));
        Assert.Empty(addBindings.For("gallery.context-toggle"));
        Assert.True(IsSymmetricConflict(Assert.Single(addBindings.Issues),
            "favorites.add", "gallery.context-toggle"));

        var removeSection = new ShortcutBindingsSection()
            .Set("favorites.remove", [ShortcutGesture.Parse("U")])
            .Set("gallery.close", [ShortcutGesture.Parse("U")]);
        var removeBindings = EffectiveShortcutBindings.Build(removeSection, platform);
        Assert.Empty(removeBindings.For("favorites.remove"));
        Assert.Empty(removeBindings.For("gallery.close"));
        Assert.True(IsSymmetricConflict(Assert.Single(removeBindings.Issues),
            "favorites.remove", "gallery.close"));
    }

    [Fact]
    public void FavoriteCommandsSupportRemapUnbindAndSavedToggleOverrides()
    {
        var section = new ShortcutBindingsSection()
            .Set("favorites.add", [ShortcutGesture.Parse("Control+Shift+F9")])
            .Set("favorites.remove", [])
            .Set("favorites.toggle", [ShortcutGesture.Parse("Primary+Shift+F")]);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);

        Assert.Empty(bindings.Issues);
        Assert.True(bindings.Matches(new KeyEventArgs
        {
            Key = Key.F9,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift
        }, "favorites.add"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.M }, "favorites.add"));
        Assert.Empty(bindings.For("favorites.remove"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.U }, "favorites.remove"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.F }, "favorites.toggle"));
        Assert.True(bindings.Matches(new KeyEventArgs
        {
            Key = Key.F,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift
        }, "favorites.toggle"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void FavoriteCommandsStatusAndStaticHelpAreLocalized(string language)
    {
        Texts.SetLanguage(language);
        bool german = language == "de";

        Assert.Equal(german ? "Favorit hinzufügen" : "Add favorite", CommandCatalog.Label("favorites.add"));
        Assert.Equal(german ? "Favorit entfernen" : "Remove favorite", CommandCatalog.Label("favorites.remove"));
        Assert.Equal(german ? "Favorisiert" : "Favorite", Texts.T("viewer.favorite.saved"));
        Assert.Equal(german ? "Kein Favorit" : "Not a favorite", Texts.T("viewer.favorite.unsaved"));
        Assert.Contains(german ? "M  Favorit hinzufügen" : "M  Add favorite", Texts.T("viewer.help.favorite"));
        Assert.Contains(german ? "U  Favorit entfernen" : "U  Remove favorite", Texts.T("viewer.help.favorite"));
        Assert.Contains(german ? "F  Hinzufügen oder entfernen" : "F  Add or remove", Texts.T("viewer.help.favorite"));
    }

    private static bool IsSymmetricConflict(ShortcutIssue issue, string first, string second) =>
        issue.BlocksSave && issue.OtherCommandId is not null
        && (issue.CommandId == first && issue.OtherCommandId == second
            || issue.CommandId == second && issue.OtherCommandId == first);
}
