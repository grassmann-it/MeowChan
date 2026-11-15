using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// Review 15.11. (finding 12): a saved binding of a command that was removed on purpose (the media-panel previous/next keys) is dead data, not a
// typo. It gives no "Unknown action retained" issue and is gone after the next save; a truly unknown id is still reported and kept.
public sealed class RetiredShortcutCommandTests
{
    private static ShortcutGesture[] Gesture(string text) => [ShortcutGesture.Parse(text)];

    [Fact]
    public void SavedBindingsOfRetiredCommandsGiveNoIssueAndAnUnknownIdStillWarns()
    {
        ShortcutBindingsSection section = new ShortcutBindingsSection()
            .Set("media.panel.previous", Gesture("Alt+Left"))
            .Set("media.panel.next", Gesture("Alt+Right"))
            .Set("media.panel.never-existed", Gesture("Alt+Up"));
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Windows);
        Assert.DoesNotContain(bindings.Issues, issue => issue.CommandId is "media.panel.previous" or "media.panel.next");
        Assert.Contains(bindings.Issues, issue => issue.CommandId == "media.panel.never-existed");
    }

    [Fact]
    public void TheNextSaveDropsRetiredBindingsAndKeepsEverythingElse()
    {
        ShortcutBindingsSection section = new ShortcutBindingsSection()
            .Set("media.panel.previous", Gesture("Alt+Left"))
            .Set("media.panel.never-existed", Gesture("Alt+Up"))
            .Set("navigation.plugins", Gesture("Alt+P"));
        Assert.True(section.HasRetiredOverrides);
        using TempDirectory directory = new();
        Config config = Config.LoadConfig(directory.Path);
        config.ShortcutBindings = section;
        config.SaveConfig();
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.False(loaded.ShortcutBindings.HasRetiredOverrides);
        Assert.False(loaded.ShortcutBindings.HasOverride("media.panel.previous"));
        Assert.True(loaded.ShortcutBindings.HasOverride("media.panel.never-existed"));
        Assert.True(loaded.ShortcutBindings.HasOverride("navigation.plugins"));
    }
}
