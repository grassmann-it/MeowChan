using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Pure policy and source coverage of the shared title bar. A Window or the bar's geometry
// needs a platform backend, so its behaviour on real windows is DesktopSmoke's window-chrome
// scenario (SecondaryWindowChromeChecks); Windows/macOS frames need the owner's native run.
[Collection("Localization")]
public sealed class WindowChromeCoverageTests
{
    [Theory]
    [InlineData(null, false, "Custom")]
    [InlineData("", false, "Custom")]
    [InlineData("custom", false, "Custom")]
    [InlineData("1", false, "Custom")]
    [InlineData("system", false, "System")]
    [InlineData(" System ", false, "System")]
    [InlineData("SYSTEM", true, "System")]
    // macOS keeps native traffic lights, full screen and corners unless own buttons are forced.
    [InlineData(null, true, "NativeCaptionButtons")]
    [InlineData("1", true, "NativeCaptionButtons")]
    [InlineData("Custom", true, "Custom")]
    public void PlatformDefaultsAndExplicitOverrides(string? value, bool macOS, string expected) =>
        Assert.Equal(Enum.Parse<WindowChromeMode>(expected), WindowChrome.Resolve(value, macOS));

    [Theory]
    [InlineData("Custom", true, true, 0d)]
    [InlineData("NativeCaptionButtons", true, false, WindowChrome.TrafficLightInset)]
    [InlineData("System", false, false, 0d)]
    public void ModesDecideCaptionButtonsAndTrafficLightInset(string name, bool caption, bool ownButtons, double inset)
    {
        WindowChromeMode mode = Enum.Parse<WindowChromeMode>(name);
        Assert.Equal(caption, WindowChrome.ShowsCaption(mode));
        Assert.Equal(ownButtons, WindowChrome.ShowsOwnCaptionButtons(mode));
        Assert.Equal(inset, WindowChrome.LeadingInset(mode));
    }

    [Fact]
    public void OnlyWindowsIsAskedForSquareCorners()
    {
        Assert.Equal(Avalonia.Controls.Win32Properties.WindowCornerPreference.DoNotRound, WindowChrome.CornerPreference(windows: true));
        Assert.Equal(Avalonia.Controls.Win32Properties.WindowCornerPreference.Default, WindowChrome.CornerPreference(windows: false));
    }

    [Fact]
    public void FallbackSwitchIsTheDocumentedEnvironmentVariable() =>
        Assert.Equal("MEOWCHAN_WINDOW_CHROME", WindowChrome.EnvironmentVariable);

    [Theory]
    [InlineData("Window", true, true, true)]
    [InlineData("Window", false, true, false)]
    [InlineData("Dialog", true, false, true)]
    [InlineData("Dialog", false, false, false)]
    public void VariantsDecideMinimizeAndResizabilityDecidesMaximize(string name, bool canResize,
        bool minimize, bool maximize)
    {
        WindowChromeKind kind = Enum.Parse<WindowChromeKind>(name);
        Assert.Equal(minimize, WindowChromePolicy.ShowsMinimize(kind));
        Assert.Equal(maximize, WindowChromePolicy.ShowsMaximize(canResize));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void CaptionButtonNamesExistInEveryBundledLanguage(string language)
    {
        string previous = Texts.Language;
        try
        {
            Texts.SetLanguage(language);
            foreach (string key in new[] { "window.minimize", "window.maximize", "window.restore", "action.close" })
                Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, language + ": " + key);
            Assert.Equal(4, new[] { "window.minimize", "window.maximize", "window.restore", "action.close" }
                .Select(key => Texts.T(key)).Distinct(StringComparer.Ordinal).Count());
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void CaptionKeepsNamedButtonsNativeRolesAndOneDragRegion()
    {
        string bar = File.ReadAllText(Source("Desktop/WindowChromeBar.cs"));
        // One drag region: the header grid (role User, so buttons and menus keep their input).
        Assert.Single(Regex.Matches(bar, @"WindowDecorationProperties\.SetElementRole\(header, WindowDecorationsElementRole\.User\);"));
        Assert.Contains("header.PointerPressed += OnTitleBarPointerPressed;", bar);
        Assert.Contains("window.BeginMoveDrag(e);", bar);
        Assert.Contains("if (e.ClickCount == 2) ToggleMaximize();", bar);
        Assert.Contains(".Any(visual => visual is Button or Menu or MenuItem)) return;", bar);
        // Commands: close goes through Window.Close (Closing guards), minimize respects CanMinimize.
        Assert.Contains("_close.Click += (_, _) => _window?.Close();", bar);
        Assert.Contains("if (_window is { CanMinimize: true } window) window.WindowState = WindowState.Minimized;", bar);
        Assert.Contains("SetLabel(_minimize, \"window.minimize\");", bar);
        Assert.Contains("SetLabel(_maximize, WindowChromePolicy.MaximizeLabel(window.WindowState));", bar);
        Assert.Contains("SetLabel(_close, \"action.close\");", bar);
        Assert.Contains("_minimize.IsVisible = WindowChromePolicy.ShowsMinimize(_kind);", bar);
        Assert.Contains("_maximize.IsVisible = WindowChromePolicy.ShowsMaximize(window.CanResize);", bar);
        Assert.Contains("IsVisible = WindowChrome.ShowsCaption(WindowChrome.Mode);", bar);
        Assert.Contains("IsVisible = WindowChrome.ShowsOwnCaptionButtons(WindowChrome.Mode) };", bar);
        Assert.Contains("16 + WindowChrome.LeadingInset(WindowChrome.Mode)", bar);
        Assert.Contains("if (TopLevel.GetTopLevel(this) is Window window) Attach(window);", bar);
    }

    [Fact]
    public void FrameAppliesThePolicyAndCollapsesInTheSystemFallback()
    {
        string chrome = File.ReadAllText(Source("Desktop/WindowChrome.cs"));
        Assert.Contains("if (OperatingSystem.IsWindows())\n            Win32Properties.SetWindowCornerPreference(window, CornerPreference(windows: true));", chrome.Replace("\r\n", "\n"));
        Assert.Contains("window.WindowDecorations = WindowDecorations.BorderOnly;", chrome);
        Assert.Equal(2, Regex.Matches(chrome, @"window\.WindowDecorations = WindowDecorations\.Full;").Count);
        Assert.Contains("new RowDefinitions(\"Auto,*\")", chrome);
        Assert.Contains("root[!Panel.BackgroundProperty] = new DynamicResourceExtension(\"Workspace.Background\");", chrome);
        Assert.Equal(48, WindowChrome.TitleBarHeight);
        string main = File.ReadAllText(Source("Desktop/Shell/MainWindow.Chrome.cs"));
        Assert.Contains("WindowChrome.Apply(this);", main);
        Assert.Contains("WindowCaptionButtons.IsVisible = WindowChrome.ShowsOwnCaptionButtons(WindowChrome.Mode);", main);
        Assert.Contains("WorkspaceTitleBarFrame.Padding = new Thickness(WindowChrome.LeadingInset(WindowChrome.Mode), 0, 0, 0);", main);
        Assert.Contains("!WindowChrome.ShowsCaption(WindowChrome.Mode)", main);
    }

    // Every product window goes through WindowChrome; nobody sets decorations on its own.
    [Fact]
    public void EveryProductWindowUsesTheSharedChrome()
    {
        string root = Source("Desktop");
        var missing = new List<string>();
        int windows = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(Source(""), path).Replace('\\', '/');
            string source = File.ReadAllText(path);
            if (relative != "Desktop/WindowChrome.cs")
                Assert.DoesNotMatch(@"\bWindowDecorations\s*=\s*WindowDecorations\.", source);
            // MainWindow keeps its own XAML caption; MainWindow.Chrome.cs applies the policy (test above).
            bool subclass = Regex.IsMatch(source, @"class \w+ : Window\b") && relative != "Desktop/Shell/MainWindow.axaml.cs";
            int inline = Regex.Matches(source, @"\bWindow \w+ = new\(\)").Count;
            if (!subclass && inline == 0) continue;
            windows += (subclass ? 1 : 0) + inline;
            int framed = Regex.Matches(source, @"WindowChrome\.(Frame|WrapContent|Apply)\(").Count;
            if (framed < (subclass ? 1 : 0) + inline) missing.Add(relative);
        }
        Assert.Empty(missing);
        Assert.True(windows >= 30, "window sweep found only " + windows);
    }

    [Theory]
    [InlineData("Desktop/MediaRatingDialog.cs")]
    [InlineData("Desktop/Views/Admission/ExperimentalActivationDialog.axaml.cs")]
    [InlineData("Desktop/Shell/MainWindow.ArchiveDeletion.cs")]
    [InlineData("Desktop/Shell/MainWindow.GalleryActions.cs")]
    [InlineData("Desktop/Views/Archive/ArchivePage.Commands.cs")]
    public void SmallModalPromptsUseTheDialogVariant(string path) =>
        Assert.Contains("WindowChromeKind.Dialog", File.ReadAllText(Source(path)));

    // Modeless or ownerless windows must keep minimize (taskbar return path).
    [Theory]
    [InlineData("Desktop/StartupErrorWindow.cs")]
    [InlineData("Desktop/Views/Booru/BooruWorkbenchWindow.cs")]
    [InlineData("Desktop/Views/Media/MediaOccurrencesWindow.cs")]
    public void ModelessAndOwnerlessWindowsKeepMinimize(string path) =>
        Assert.DoesNotContain("WindowChromeKind.Dialog", File.ReadAllText(Source(path)));

    [Fact]
    public void ModelessBookmarkListKeepsMinimizeWhileItsConfirmationIsADialog()
    {
        string source = File.ReadAllText(Source("Desktop/Views/Collections/PostBookmarksDialog.axaml.cs"));
        Assert.Contains("        WindowChrome.WrapContent(this);", source);
        Assert.Contains("WindowChrome.Frame(confirm, ", source);
        Assert.Contains("WindowChromeKind.Dialog);", source);
    }

    [Theory]
    [InlineData("Desktop/Views/About/AboutWindow.axaml")]
    [InlineData("Desktop/Views/Settings/SettingsWindow.axaml")]
    [InlineData("Desktop/Views/Settings/FirstRunSetupWindow.axaml")]
    public void XamlCaptionWindowsApplyThePolicyInCode(string path)
    {
        XElement root = XDocument.Load(Source(path)).Root!.Elements().Single(e => e.Name.LocalName == "Grid");
        Assert.Equal("Auto,*", (string?)root.Attribute("RowDefinitions"));
        Assert.Contains("WindowChrome.Apply(this);", File.ReadAllText(Source(path + ".cs")));
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
