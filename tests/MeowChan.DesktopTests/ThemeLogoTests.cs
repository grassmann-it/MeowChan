using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using SkiaSharp;

namespace MeowChan.DesktopTests;

// No window, platform or profile: the theme-to-logo mapping, the embedded assets and the live follow.
[CollectionDefinition("Theme logo", DisableParallelization = true)]
public sealed class ThemeLogoCollection;

[Collection("Theme logo")]
public sealed class ThemeLogoTests
{
    private const string Orange = "avares://MeowChan/Assets/meowchan.png";
    private const string Grey = "avares://MeowChan/Assets/meowchan-dark.png";

    [Fact]
    public void LightShowsTheOrangeCatAndDarkTheGreyCat()
    {
        Assert.Equal(Orange, ThemeLogo.LightAsset);
        Assert.Equal(Grey, ThemeLogo.DarkAsset);
        Assert.Equal(Orange, ThemeLogo.AssetFor(ThemeVariant.Light));
        Assert.Equal(Grey, ThemeLogo.AssetFor(ThemeVariant.Dark));
    }

    [Fact]
    public void UnresolvedVariantsFallBackToTheOrangeCat()
    {
        Assert.Equal(Orange, ThemeLogo.AssetFor(null));
        Assert.Equal(Orange, ThemeLogo.AssetFor(ThemeVariant.Default));
    }

    [Fact]
    public void CustomVariantsFollowTheVariantTheyInherit()
    {
        Assert.Equal(Grey, ThemeLogo.AssetFor(new ThemeVariant("Night", ThemeVariant.Dark)));
        Assert.Equal(Orange, ThemeLogo.AssetFor(new ThemeVariant("Paper", ThemeVariant.Light)));
        Assert.Equal(Orange, ThemeLogo.AssetFor(new ThemeVariant("Unrelated", null)));
    }

    [Fact]
    public void BothCatsAreEmbeddedAsTheRepositoryFilesAndAreDifferentImages()
    {
        byte[] orange = Resource(Orange), grey = Resource(Grey);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Repository(), "Assets/meowchan.png")), orange);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Repository(), "Assets/meowchan-dark.png")), grey);
        Assert.NotEqual(SHA256.HashData(orange), SHA256.HashData(grey));
        // Pixels, not only bytes: the orange cat is far warmer than the grey tabby (about 63 against 29).
        Assert.True(RedOverBlue(orange) > 46, "The light-theme logo is not the orange cat.");
        Assert.True(RedOverBlue(grey) < 46, "The dark-theme logo is not the grey cat.");
    }

    [Fact]
    public void AnAlreadyCreatedConsumerSwitchesLiveAndFollowsOnlyOnce() => OnOwnedDispatcher(() =>
    {
        var scope = new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Light };
        var consumer = new Border();
        scope.Child = consumer;
        List<string> shown = [];
        ThemeLogo.Follow(consumer, variant => shown.Add(ThemeLogo.AssetFor(variant)));
        ThemeLogo.Follow(consumer, variant => shown.Add("second subscription"));
        Assert.Equal([Orange], shown);
        scope.RequestedThemeVariant = ThemeVariant.Dark;
        Assert.Equal([Orange, Grey], shown);
        scope.RequestedThemeVariant = ThemeVariant.Light;
        Assert.Equal([Orange, Grey, Orange], shown);
    });

    // Windows, images and the tray read the logo only through ThemeLogo; no consumer picks a cat itself.
    [Fact]
    public void OnlyThemeLogoNamesTheLogoAssets()
    {
        string repository = Repository();
        IEnumerable<string> sources = Directory.EnumerateFiles(Path.Combine(repository, "Desktop"), "*.*", SearchOption.AllDirectories)
            .Append(Path.Combine(repository, "App.axaml")).Append(Path.Combine(repository, "App.axaml.cs"))
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".axaml", StringComparison.Ordinal));
        string[] naming = sources.Where(file => File.ReadAllText(file).Contains("Assets/meowchan", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(repository, file).Replace('\\', '/')).ToArray();
        Assert.Equal(["Desktop/ThemeLogo.cs"], naming);
    }

    // Window, tray and dialog icons follow the theme; the header logo and About show both cats side by side.
    [Fact]
    public void HeaderAndAboutShowBothCatsWhileWindowIconsStayThemeBased()
    {
        string repository = Repository();
        string Read(string path) => File.ReadAllText(Path.Combine(repository, path));
        (string Axaml, string Code, string Orange, string Grey, string Pair)[] pairs =
        [
            ("Desktop/Shell/MainWindow.axaml", "Desktop/Shell/MainWindow.axaml.cs", "WorkspaceLogo", "WorkspaceLogoDark", "WorkspaceLogoPair"),
            ("Desktop/Views/About/AboutWindow.axaml", "Desktop/Views/About/AboutWindow.axaml.cs", "CatLogo", "CatLogoDark", "CatLogoPair"),
        ];
        foreach (var (axamlPath, codePath, orange, grey, pair) in pairs)
        {
            string axaml = Read(axamlPath), code = Read(codePath);
            // Two images in one container, neither with a fixed source, both the same size.
            Assert.Matches($@"<Image x:Name=""{orange}"" Width=""(\d+)"" Height=""\1""", axaml);
            Assert.Matches($@"<Image x:Name=""{grey}"" Width=""(\d+)"" Height=""\1""", axaml);
            Assert.DoesNotContain("Source=", axaml[axaml.IndexOf(orange, StringComparison.Ordinal)..axaml.IndexOf(grey, StringComparison.Ordinal)]);
            Assert.Contains($"x:Name=\"{pair}\"", axaml);
            Assert.Contains($"ThemeLogo.ShowPair({orange}, {grey});", code);
            // The pair does not follow the theme, the window icon still does.
            Assert.DoesNotContain($"ThemeLogo.Follow({orange})", code);
            Assert.DoesNotContain($"ThemeLogo.Follow({grey})", code);
            Assert.Contains("ThemeLogo.Follow(this);", code);
        }
        // About names the pair once; the images are not announced separately.
        string about = Read("Desktop/Views/About/AboutWindow.axaml"), aboutCode = Read("Desktop/Views/About/AboutWindow.axaml.cs");
        Assert.Contains("AutomationProperties.SetName(CatLogoPair, Texts.T(\"about.logoDescription\"));", aboutCode);
        Assert.Equal(2, Regex.Matches(about, @"<Image x:Name=""CatLogo(Dark)?""[^>]*AccessibilityView=""Raw""").Count);
        // The tray and the other windows keep their theme-based single icon.
        Assert.Contains("_trayLogo = ThemeLogo.Follow(trayIcon);", Read("Desktop/Shell/MainWindow.axaml.cs"));
        Assert.Contains("ThemeLogo.Follow(this);", Read("Desktop/Views/Settings/SettingsWindow.axaml.cs"));
        Assert.Contains("ThemeLogo.Follow(this);", Read("Desktop/Views/Settings/FirstRunSetupWindow.axaml.cs"));
        Assert.Contains("ThemeLogo.Follow(this);", Read("Desktop/StartupErrorWindow.cs"));
    }

    // The photographer credit is the same in the notices, the README and the About window; the grey cat is not credited to it.
    [Fact]
    public void PhotoCreditCarriesBothLinksInNoticesReadmeAndAbout()
    {
        string repository = Repository();
        string notices = File.ReadAllText(Path.Combine(repository, "THIRD-PARTY-NOTICES.md"));
        string readme = File.ReadAllText(Path.Combine(repository, "README.md"));
        string about = File.ReadAllText(Path.Combine(repository, "Desktop/Views/About/AboutWindow.axaml"));
        const string name = "Alexander Huxel · Webentwicklung Huxel";
        string[] links = ["https://webentwicklung-huxel.de/", "https://flickr.com/people/204852984@N05/"];
        Assert.Equal(name, AboutWindow.PhotoCreditName);
        foreach (string text in new[] { notices, readme })
        {
            Assert.Contains(name, text);
            foreach (string link in links) Assert.Contains(link, text);
        }
        foreach (string link in links) Assert.Matches($@"Tag=""{Regex.Escape(link)}""\s+Click=""OnLinkClick""", about);
        Assert.Contains("6c9024b7220b6e015eeeb36aec9763fa5d804224b2b9e260caa7c4947c8436b1", notices);
        Assert.Contains("Used with the permission of Alexander Huxel for the projects of Tom Graßmann, on condition of attribution by name, as stated by the project owner on 12 November 2026.",
            Regex.Replace(notices, @"\s+", " "));
        Assert.DoesNotContain("Creative Commons", notices.Split("Cat photographs for both sets")[1].Split("## Resolved")[0]);
        Assert.DoesNotContain("provenance as not assessed", notices);
        // Both cats are credited and named; the grey tabby keeps its recorded ImageGen provenance.
        string grey = notices[notices.IndexOf("Grey tabby", StringComparison.Ordinal)..notices.IndexOf("Orange (Simba)", StringComparison.Ordinal)];
        Assert.Contains("OpenAI ImageGen", grey);
        Assert.Contains("Mila", grey);
        Assert.Contains("Simba (orange) and Mila (grey)", notices);
        Assert.Contains("12 November 2026", notices);
        Assert.Contains("Mila, grey", readme);
        Assert.Contains("Simba, orange", readme);
        Assert.DoesNotContain("unknown", grey, StringComparison.OrdinalIgnoreCase);
        // Localized label text exists in the catalog and the machine-translated packs, with the name as a placeholder.
        foreach (string key in new[] { "about.photoCredit", "about.photoCreditFlickr", "about.logoSimba", "about.logoMila" })
        {
            Assert.Contains(R08dAboutCatalog.Entries, entry => entry.Key == key && !entry.English.Contains("Huxel") && (key.StartsWith("about.logo", StringComparison.Ordinal) || (entry.English.Contains("{0}") && entry.German.Contains("{0}"))));
            foreach (string code in new[] { "es", "fr", "ru" })
                Assert.Contains($"\"{key}\": \"", File.ReadAllText(Path.Combine(repository, $"Desktop/Localization/Packs/{code}.json")));
        }
    }

    // Without a platform there is no registered loader; the standard one reads the same compiled resources.
    private static byte[] Resource(string uri)
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<IAssetLoader>().ToConstant(new StandardAssetLoader());
        Assert.True(AssetLoader.Exists(new Uri(uri)), uri + " is not an Avalonia resource.");
        using Stream stream = AssetLoader.Open(new Uri(uri));
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static double RedOverBlue(byte[] png)
    {
        using SKBitmap bitmap = SKBitmap.Decode(png);
        double weight = 0, sum = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha < 128) continue;
                weight += pixel.Alpha;
                sum += (pixel.Red - pixel.Blue) * (double)pixel.Alpha;
            }
        return sum / weight;
    }

    // Same pinned Avalonia 12.1.4 seam as ThreadPriorityContextMenuTests: this nonparallel collection
    // owns the UI dispatcher for the body and restores the process-primary slot afterwards.
    private static void OnOwnedDispatcher(Action test)
    {
        FieldInfo primary = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Avalonia primary dispatcher slot is missing.");
        object? previous = primary.GetValue(null);
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        try
        {
            primary.SetValue(null, owner);
            owner.Invoke(test, DispatcherPriority.Send);
        }
        finally { primary.SetValue(null, previous); }
    }

    private static string Repository([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
