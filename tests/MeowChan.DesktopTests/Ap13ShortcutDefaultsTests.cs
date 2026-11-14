using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

/// <summary>UX flows AP13: Show new posts, Follow live, Import, Continue reading and the Diagnostics retry are registered
/// commands with a default gesture (so the shortcut editor lists them); the defaults yield to saved bindings.</summary>
public sealed class Ap13ShortcutDefaultsTests
{
    public static IEnumerable<object[]> Cases() =>
        from platform in Enum.GetValues<ShortcutPlatform>()
        from id in new[] { "reader.show-new-posts", "watch.follow-live", "import.open", "navigation.continue-reading", "diagnostics.retry-selection" }
        select new object[] { platform, id };

    [Theory, MemberData(nameof(Cases))]
    public void EachCommandHasADefaultThatIsNotABareKey(ShortcutPlatform platform, string id)
    {
        CommandDefinition command = CommandCatalog.Get(id);
        var defaults = EffectiveShortcutBindings.Defaults(command, platform);
        Assert.NotEmpty(defaults);
        Assert.All(defaults, gesture => Assert.False(EffectiveShortcutBindings.IsSingleCharacterKey(gesture)));
        Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
        Assert.NotEmpty(EffectiveShortcutBindings.Build(null, platform).For(id));
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void NoTwoOfTheNewDefaultsShareAGesture(ShortcutPlatform platform)
    {
        string[] ids = { "reader.show-new-posts", "watch.follow-live", "import.open", "navigation.continue-reading", "diagnostics.retry-selection" };
        var shown = ids.SelectMany(id => EffectiveShortcutBindings.Defaults(CommandCatalog.Get(id), platform)).Select(gesture => gesture.Display(platform)).ToArray();
        Assert.Equal(shown.Length, shown.Distinct().Count());
    }

    [Fact]
    public void RulesCanBeOpenedFromTheReaderAndTheMediaPage()
    {
        CommandScope scope = CommandCatalog.Get("rules.edit-scope").Scope;
        Assert.True(scope.HasFlag(CommandScope.Reader) && scope.HasFlag(CommandScope.Media));
    }
}
