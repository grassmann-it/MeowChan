using System.Reflection;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class NavigationMediaShortcutTests : IDisposable
{
    private static readonly string[] Pages = MainPageOrder.CommandIds.ToArray();
    // Pages whose digit changed with the footer-order renumbering (Downloads and Archive kept theirs).
    public static IEnumerable<object[]> RenumberedPages() =>
        new[] { "navigation.media", "navigation.rules", "navigation.sources", "navigation.diagnostics", "navigation.plugins" }.Select(id => new object[] { id });
    public static IEnumerable<object[]> RenumberedPagesAndOwners() =>
        from page in RenumberedPages() from owner in new[] { "navigation.archive", "thread.read", "gallery.fit", "input.focus-search" }
        select new object[] { page[0], owner };

    private readonly EffectiveShortcutBindings _original = ShortcutService.Current;
    public NavigationMediaShortcutTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { SetBindings(_original); Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData(ShortcutPlatform.Linux, "Ctrl+3")]
    [InlineData(ShortcutPlatform.Windows, "Ctrl+3")]
    [InlineData(ShortcutPlatform.MacOS, "⌘3")]
    public void MediaJoinsTheOtherMainPagesWithItsOwnDefaultAndNoConflict(ShortcutPlatform platform, string display)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Equal(display, bindings.Display("navigation.media"));
        Assert.Empty(bindings.Issues);
        // Every main page has its own digit; none of the others, and no other command, owns Ctrl+3.
        foreach (string page in Pages) Assert.Single(bindings.For(page));
        Assert.Equal(Pages.Length, Pages.Select(page => bindings.Display(page)).Distinct().Count());
        ShortcutGesture media = Assert.Single(bindings.For("navigation.media"));
        foreach (var command in CommandCatalog.All.Where(command => command.Id != "navigation.media"))
            Assert.DoesNotContain(bindings.For(command.Id), gesture => EffectiveShortcutBindings.Equivalent(media, gesture, platform));
        // The n-th main page from the left is Primary+n.
        for (int index = 0; index < Pages.Length; index++)
            Assert.Equal((index + 1).ToString(), bindings.Display(Pages[index])![^1..]);
        Assert.Equal("5", bindings.Display("navigation.sources")![^1..]);
        Assert.Equal("7", bindings.Display("navigation.plugins")![^1..]);
    }

    [Theory]
    [MemberData(nameof(RenumberedPagesAndOwners))]
    public void ASavedBindingOnTheSameKeyKeepsItAndTheNewDefaultYields(string page, string owner)
    {
        string digit = MainPageOrder.Digit(page)!.Value.ToString();
        var section = new ShortcutBindingsSection().Set(owner, [ShortcutGesture.Parse("Primary+" + digit)]);
        string before = section.Raw.GetRawText();
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Equal("Ctrl+" + digit, bindings.Display(owner));
        Assert.Empty(bindings.For(page));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        Assert.Contains(bindings.Issues, issue => issue.CommandId == page && issue.OtherCommandId == owner);
        Assert.Equal(before, section.Raw.GetRawText());
    }

    [Fact]
    public void AFrozenListThatStillHoldsTheOldDigitKeepsBothGesturesAgainstTheRenumberedDefaults()
    {
        // Add another shortcut stored [old default, extra] as Replace; the old digit is now a different page's default.
        var section = new ShortcutBindingsSection()
            .Set("navigation.rules", [ShortcutGesture.Parse("Primary+3"), ShortcutGesture.Parse("Primary+9")])
            .Set("navigation.diagnostics", [ShortcutGesture.Parse("Primary+5")]);
        string before = section.Raw.GetRawText();
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Equal("Ctrl+3 / Ctrl+9", bindings.Display("navigation.rules"));
        Assert.Equal("Ctrl+5", bindings.Display("navigation.diagnostics"));
        Assert.Empty(bindings.For("navigation.media"));   // Ctrl+3 stays with Rules
        Assert.Empty(bindings.For("navigation.sources")); // Ctrl+5 stays with Diagnostics
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        Assert.Equal(before, section.Raw.GetRawText());
    }

    [Fact]
    public void SwappingTwoPagesWithSavedBindingsNeverBlocksSaving()
    {
        var section = new ShortcutBindingsSection()
            .Set("navigation.media", [ShortcutGesture.Parse("Primary+6")])
            .Set("navigation.diagnostics", [ShortcutGesture.Parse("Primary+3")]);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Equal("Ctrl+6", bindings.Display("navigation.media"));
        Assert.Equal("Ctrl+3", bindings.Display("navigation.diagnostics"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
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
        // Giving Ctrl+3 away from Media to another page is an ordinary explicit binding, not a conflict.
        var swapped = EffectiveShortcutBindings.Build(new ShortcutBindingsSection()
            .Set("navigation.media", [ShortcutGesture.Parse("Primary+Shift+M")])
            .Set("navigation.sources", [ShortcutGesture.Parse("Primary+3")]), ShortcutPlatform.Linux);
        Assert.Equal("Ctrl+3", swapped.Display("navigation.sources"));
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
        Assert.StartsWith(label + " · Ctrl+3", initial);
        Assert.Equal(CommandBindings.Tooltip("navigation.media"), initial);

        Rebind(new ShortcutBindingsSection().Set("navigation.media", [ShortcutGesture.Parse("Primary+Shift+M")]));
        string moved = Assert.IsType<string>(ToolTip.GetTip(footer));
        Assert.StartsWith(label + " · Ctrl+Shift+M", moved);
        Assert.DoesNotContain("Ctrl+3", moved);

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
