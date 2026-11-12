using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Desktop;
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
