using System.Reflection;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class NavigationMediaShortcutTests : IDisposable
{
    private static readonly string[] Pages = MainPageOrder.NumberedCommandIds.ToArray();
    // Pages whose default digit can differ from what a profile saved earlier (every main page yields since the footer digits of F1).
    public static IEnumerable<object[]> RenumberedPages() =>
        new[] { "navigation.downloads", "navigation.media", "navigation.tagging", "navigation.booru", "navigation.rules", "navigation.sources", "navigation.status" }
            .Select(id => new object[] { id });
    public static IEnumerable<object[]> RenumberedPagesAndOwners() =>
        from page in RenumberedPages() from owner in new[] { "navigation.archive", "thread.read", "gallery.fit", "input.focus-search" }
        select new object[] { page[0], owner };

    private readonly EffectiveShortcutBindings _original = ShortcutService.Current;
    public NavigationMediaShortcutTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { SetBindings(_original); Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData(ShortcutPlatform.Linux, "Ctrl+3 / Ctrl+Shift+E")]
    [InlineData(ShortcutPlatform.Windows, "Ctrl+3 / Ctrl+Shift+E")]
    [InlineData(ShortcutPlatform.MacOS, "⌘3 / ⇧⌘E")]
    public void MediaJoinsTheOtherMainPagesWithItsOwnDefaultAndNoConflict(ShortcutPlatform platform, string display)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Equal(display, bindings.Display("navigation.media"));
        Assert.Empty(bindings.Issues);
        // Every main page has its own digit and (revision 15) its own fixed letter behind it; none of the others, and no other command, owns Ctrl+3.
        foreach (string page in Pages) Assert.Equal(2, bindings.For(page).Count);
        Assert.Equal(Pages.Length, Pages.Select(page => bindings.Display(page)).Distinct().Count());
        foreach (ShortcutGesture media in bindings.For("navigation.media"))
            foreach (var command in CommandCatalog.All.Where(command => command.Id != "navigation.media"))
                Assert.DoesNotContain(bindings.For(command.Id), gesture => EffectiveShortcutBindings.Equivalent(media, gesture, platform));
        // The n-th main page from the left is Primary+n (the first gesture).
        for (int index = 0; index < Pages.Length; index++)
            Assert.Equal("D" + MainPageOrder.Digit(Pages[index])!.Value, bindings.For(Pages[index])[0].Key);
        // Booru takes 5. Rules, Sources and Status follow.
        Assert.Equal("D7", bindings.For("navigation.sources")[0].Key);
        Assert.Null(bindings.Display("navigation.plugins"));
        Assert.Equal("D8", bindings.For("navigation.status")[0].Key);
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
        // The yielding digit goes; the page keeps its fixed letter (revision 15), which is nobody's saved key here.
        Assert.Equal(new[] { PageKeyScheme.Letter(page) }, bindings.For(page).Select(g => g.Key));
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
        Assert.Equal(new[] { PageKeyScheme.Letter("navigation.media") }, bindings.For("navigation.media").Select(g => g.Key));   // Ctrl+3 stays with Rules; Media keeps its letter
        Assert.Equal("Ctrl+7 / Ctrl+Shift+X", bindings.Display("navigation.sources")); // Sources has its own digit now; Ctrl+5 stays with Diagnostics
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
        // ToolTip.SetTip verifies the process-primary UIThread, which may belong to another xunit worker ("Localization" is non-parallel).
        OwnedUiDispatcher.Run(() =>
        {
            Texts.SetLanguage(language);
            SetBindings(EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux));
            Button footer = new();
            CommandBindings.Apply(footer, "navigation.media");
            string initial = Assert.IsType<string>(ToolTip.GetTip(footer));
            string initialShortcut = CommandCatalog.ShortcutDisplay("navigation.media") ?? throw new InvalidOperationException("Media should have a default shortcut.");
            Assert.StartsWith(label + " · " + initialShortcut, initial);
            Assert.Equal(CommandBindings.Tooltip("navigation.media"), initial);

            Rebind(new ShortcutBindingsSection().Set("navigation.media", [ShortcutGesture.Parse("Primary+Shift+M")]));
            string moved = Assert.IsType<string>(ToolTip.GetTip(footer));
            string movedShortcut = CommandCatalog.ShortcutDisplay("navigation.media") ?? throw new InvalidOperationException("Media should retain the rebound shortcut.");
            Assert.StartsWith(label + " · " + movedShortcut, moved);
            Assert.NotEqual(initialShortcut, movedShortcut);
            Assert.DoesNotContain(initialShortcut, moved);

            Rebind(new ShortcutBindingsSection().Set("navigation.media", []));
            string unbound = Assert.IsType<string>(ToolTip.GetTip(footer));
            Assert.StartsWith(label, unbound);
            Assert.DoesNotContain(initialShortcut, unbound);
            Assert.DoesNotContain(movedShortcut, unbound);
        });
    }

    [Fact]
    public void EveryFooterButtonHasItsOwnLocalizedDescriptionAndEffectiveShortcut()
    {
        OwnedUiDispatcher.Run(() =>
        {
            SetBindings(EffectiveShortcutBindings.Build());
            foreach (string language in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
            {
                Texts.SetLanguage(language);
                HashSet<string> descriptions = new(StringComparer.Ordinal);
                foreach (string page in MainPageOrder.CommandIds.Append("navigation.settings"))
                {
                    Button footer = new();
                    CommandBindings.Apply(footer, page);
                    string tip = Assert.IsType<string>(ToolTip.GetTip(footer));
                    string[] lines = tip.Split('\n');
                    Assert.Equal(2, lines.Length);
                    Assert.Equal(CommandCatalog.Label(page) + " · " + ShortcutService.Current.Display(page), lines[0]);
                    string key = "command." + page + ".tip";
                    Assert.Equal(Texts.T(key), lines[1]);
                    Assert.NotEqual(key, lines[1]);
                    Assert.InRange(lines[1].Length, 1, 60);
                    Assert.True(descriptions.Add(lines[1]), "Duplicate footer description in " + language + ": " + lines[1]);
                    Assert.DoesNotContain(Texts.T("command.scope.navigation.generic"), tip, StringComparison.Ordinal);
                    if (language != "en") Assert.NotEqual(BuiltInCatalog.English[key], lines[1]);
                }
            }
        });
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
