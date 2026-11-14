using System.Runtime.CompilerServices;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// A11y review SOLLTE A10, A11, A18, A19, A20 (14.11.2026): the search scope help and platform-correct keys, banner severity,
/// the selection plural and the focus fallback of a reduced toolbar action. Source checks only where the behaviour needs a window
/// (the native proof is the smoke list in the report).
/// </summary>
[Collection("Localization")]
public sealed class A11ySollteTests : IDisposable
{
    public A11ySollteTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    [Fact]
    public void TheScopeMenuKeysAreShownInThePlatformNotation()
    {
        Assert.Equal("Alt+Down / F4", SearchScopeChip.MenuShortcutDisplay(macOS: false));
        Assert.Equal("⌥↓ / F4 (Fn)", SearchScopeChip.MenuShortcutDisplay(macOS: true));
    }

    [Fact]
    public void NoSearchTextHardCodesTheWindowsSpellingOfTheMenuKey()
    {
        foreach (string relative in new[] { "Desktop/Controls/SearchScopeChip.cs", "Desktop/Views/Archive/ArchivePage.WorkViews.cs" })
            Assert.DoesNotContain("\" · Alt+↓\"", Source(relative));
    }

    [Fact]
    public void TheFieldCarriesTheScopeAsHelpTextAndTheChipRefreshesIt()
    {
        string chip = Source("Desktop/Controls/SearchScopeChip.cs");
        Assert.Contains("AutomationProperties.SetHelpText(owner, Texts.T(\"search.scope.fieldHelp\"", chip);
        Assert.Contains("ApplyOwnerHelp();", chip);
        // The owner gets the text when the chip moves into it and loses it when the chip leaves.
        string box = Source("Desktop/Controls/SearchBox.cs");
        Assert.Contains("value.ApplyOwnerHelp()", box);
        Assert.Contains("ClearValue(Avalonia.Automation.AutomationProperties.HelpTextProperty)", box);
    }

    [Fact]
    public void FieldHelpNamesScopeExplanationAndKeys()
    {
        string text = Texts.T("search.scope.fieldHelp", "File name", Texts.T("search.scope.tip"), "Alt+Down / F4");
        Assert.StartsWith("Search scope: File name.", text);
        Assert.Contains("Alt+Down / F4", text);
    }

    [Fact]
    public void EverySeverityHasItsOwnIconShapeAndSpokenWord()
    {
        NotificationType[] all = { NotificationType.Error, NotificationType.Warning, NotificationType.Information, NotificationType.Success };
        string[] icons = all.Select(WorkspaceBanner.IconFor).ToArray();
        Assert.Equal(4, icons.Distinct().Count());
        Assert.All(icons, key => Assert.Contains(key, WorkspaceIcons.Keys));
        Assert.Equal(4, icons.Select(WorkspaceIcons.DataOf).Distinct().Count());
        string[] words = all.Select(WorkspaceBanner.SeverityLabel).ToArray();
        Assert.Equal(new[] { "Error", "Warning", "Information", "Success" }, words);
        Assert.Equal("Warning: Folder missing", WorkspaceBanner.AnnouncedText(NotificationType.Warning, "Folder missing"));
    }

    [Fact]
    public void TheShellBannerShowsTheSeverityAndAnnouncesItBeforeTheMessage()
    {
        Assert.Contains("x:Name=\"ShellActionErrorBanner\" Type=\"Error\" ShowSeverityIcon=\"True\"", Source("Desktop/Shell/MainWindow.axaml"));
        string notifications = Source("Desktop/Shell/MainWindow.Notifications.cs");
        int name = notifications.IndexOf("WorkspaceBanner.AnnouncedText(type, message)", StringComparison.Ordinal);
        int text = notifications.IndexOf("text.Text = message;", StringComparison.Ordinal);
        Assert.True(name > 0 && text > name, "the name is set before the text, so the live region reads the severity first");
    }

    [Theory, InlineData("en", 1, "Selected thread (1)"), InlineData("en", 3, "Selected threads (3)"),
        InlineData("de", 1, "Ausgewählter Thread (1)"), InlineData("de", 3, "Ausgewählte Threads (3)"),
        InlineData("ru", 1, "Выбранный тред (1)"), InlineData("ru", 2, "Выбранные треды (2)"),
        InlineData("pl", 1, "Zaznaczony wątek (1)"), InlineData("pl", 5, "Zaznaczone wątki (5)"),
        InlineData("cs", 1, "Vybrané vlákno (1)"), InlineData("cs", 4, "Vybraná vlákna (4)"),
        InlineData("ja", 1, "選択したスレッド（1）")]
    public void TheSelectionScopeTakesItsPluralFromTheLanguage(string language, int count, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, Texts.Count("export.scope.selection", count));
    }

    [Fact]
    public void AReducedToolbarActionReturnsFocusToMoreOrAShownItem()
    {
        string source = Source("Desktop/Views/Archive/ArchivePage.ActionToolBar.cs");
        Assert.Contains("GalleryOriginFocusTarget(Control? origin) => ActionOriginFocusTarget(origin)", source);
        // Both reduced states (closed "More" popup, left out by the view) end at "More"; with none on the bar the first shown item.
        Assert.Contains("if (toolbar.OverflowHost?.Children.Contains(origin) == true) return more;", source);
        Assert.Contains("if (more != null) return more;", source);
        Assert.Contains("toolbar.Items.OfType<Control>().FirstOrDefault(item =>", source);
    }

    private static string Source(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../..", relative)));
}
