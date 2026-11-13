using System.Reflection;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class NavigationMediaShortcutTests : IDisposable
{
    private static readonly string[] Pages =
    {
        "navigation.downloads", "navigation.archive", "navigation.media", "navigation.rules",
        "navigation.sources", "navigation.diagnostics"
    };

    private readonly EffectiveShortcutBindings _original = ShortcutService.Current;
    public NavigationMediaShortcutTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { SetBindings(_original); Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData(ShortcutPlatform.Linux, "Ctrl+6")]
    [InlineData(ShortcutPlatform.Windows, "Ctrl+6")]
    [InlineData(ShortcutPlatform.MacOS, "⌘+6")]
    public void MediaJoinsTheOtherMainPagesWithItsOwnDefaultAndNoConflict(ShortcutPlatform platform, string display)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Equal(display, bindings.Display("navigation.media"));
        Assert.Empty(bindings.Issues);
        // Every main page has its own digit; none of the others, and no other command, owns Ctrl+6.
        foreach (string page in Pages) Assert.Single(bindings.For(page));
        Assert.Equal(Pages.Length, Pages.Select(page => bindings.Display(page)).Distinct().Count());
        ShortcutGesture media = Assert.Single(bindings.For("navigation.media"));
        foreach (var command in CommandCatalog.All.Where(command => command.Id != "navigation.media"))
            Assert.DoesNotContain(bindings.For(command.Id), gesture => EffectiveShortcutBindings.Equivalent(media, gesture, platform));
        // The existing digits keep their meaning; nothing was renumbered.
        Assert.Equal("1", bindings.Display("navigation.downloads")![^1..]);
        Assert.Equal("2", bindings.Display("navigation.archive")![^1..]);
        Assert.Equal("3", bindings.Display("navigation.rules")![^1..]);
    }

    [Theory]
    [InlineData("navigation.archive")]
    [InlineData("thread.read")]
    [InlineData("gallery.fit")]
    [InlineData("input.focus-search")]
    public void ASavedBindingOnTheSameKeyKeepsItAndTheNewDefaultYields(string owner)
    {
        var section = new ShortcutBindingsSection().Set(owner, [ShortcutGesture.Parse("Primary+6")]);
        string before = section.Raw.GetRawText();
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Equal("Ctrl+6", bindings.Display(owner));
        Assert.Empty(bindings.For("navigation.media"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        Assert.Contains(bindings.Issues, issue => issue.CommandId == "navigation.media" && issue.OtherCommandId == owner);
        Assert.Equal(before, section.Raw.GetRawText());
    }

    [Fact]
    public void ARebindingOrUnbindingOfMediaIsHonouredAndNotAddedTo()
    {
        var moved = EffectiveShortcutBindings.Build(
            new ShortcutBindingsSection().Set("navigation.media", [ShortcutGesture.Parse("Primary+Shift+M")]), ShortcutPlatform.Linux);
        Assert.Equal("Ctrl+Shift+M", moved.Display("navigation.media"));
        var unbound = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("navigation.media", []), ShortcutPlatform.Linux);
        Assert.Empty(unbound.For("navigation.media"));
        Assert.DoesNotContain(unbound.Issues, issue => issue.BlocksSave);
        // Giving Ctrl+6 away from Media to another page is an ordinary explicit binding, not a conflict.
        var swapped = EffectiveShortcutBindings.Build(new ShortcutBindingsSection()
            .Set("navigation.media", [ShortcutGesture.Parse("Primary+Shift+M")])
            .Set("navigation.sources", [ShortcutGesture.Parse("Primary+6")]), ShortcutPlatform.Linux);
        Assert.Equal("Ctrl+6", swapped.Display("navigation.sources"));
        Assert.DoesNotContain(swapped.Issues, issue => issue.BlocksSave);
    }

    [Theory]
    [InlineData("en", "Media")]
    [InlineData("de", "Medien")]
    public void TheFooterButtonTooltipNamesTheShortcutAndFollowsRebinding(string language, string label)
    {
        Texts.SetLanguage(language);
        SetBindings(EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux));
        Button footer = new();
        CommandBindings.Apply(footer, "navigation.media");
        string initial = Assert.IsType<string>(ToolTip.GetTip(footer));
        Assert.StartsWith(label + " · Ctrl+6", initial);
        Assert.Equal(CommandBindings.Tooltip("navigation.media"), initial);

        Rebind(new ShortcutBindingsSection().Set("navigation.media", [ShortcutGesture.Parse("Primary+Shift+M")]));
        string moved = Assert.IsType<string>(ToolTip.GetTip(footer));
        Assert.StartsWith(label + " · Ctrl+Shift+M", moved);
        Assert.DoesNotContain("Ctrl+6", moved);

        Rebind(new ShortcutBindingsSection().Set("navigation.media", []));
        Assert.DoesNotContain("Ctrl", Assert.IsType<string>(ToolTip.GetTip(footer)));
    }

    [Fact]
    public void EveryFooterPageButtonTooltipShowsItsShortcut()
    {
        SetBindings(EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux));
        foreach (string page in Pages)
        {
            Button footer = new();
            CommandBindings.Apply(footer, page);
            Assert.Contains(CommandCatalog.ShortcutDisplay(page, false)!, Assert.IsType<string>(ToolTip.GetTip(footer)));
        }
    }

    // Publish posts to the UI dispatcher, which does not run here: swap the snapshot and run the observers directly.
    private static void SetBindings(EffectiveShortcutBindings value) =>
        typeof(ShortcutService).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);

    private static void Rebind(ShortcutBindingsSection section)
    {
        SetBindings(EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux));
        var refreshers = (List<WeakReference<Action>>)typeof(ShortcutService)
            .GetField("Refreshers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        WeakReference<Action>[] callbacks = refreshers.ToArray();
        refreshers.Clear();
        foreach (var callback in callbacks)
            if (callback.TryGetTarget(out Action? refresh)) refresh();
    }
}
