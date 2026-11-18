using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using SkiaSharp;

namespace MeowChan.DesktopTests;

[Collection("Booru exact navigation")]
public sealed class NativeMenuPresentationTests
{
    private static void WithApplication(Action<Application> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        Application app = new();
        AvaloniaLocator.CurrentMutable.Bind<Application>().ToConstant(app);
        app.RequestedThemeVariant = ThemeVariant.Light;
        ShortcutBindingsSection previous = ShortcutService.CurrentSection;
        try { test(app); }
        finally { ShortcutService.Publish(previous); }
    });

    [Fact]
    public void ReapplyAndShortcutPublishReuseIconButRefreshLabelsAndShortcuts() => WithApplication(app =>
    {
        string label = "First", shortcut = "F8";
        NativeMenuItem item = CommandBindings.CreateNativeMenuItem("media.export-original", () => { },
            label: () => label, shortcut: () => shortcut);
        object icon = item.Icon!;
        int iconChanges = 0;
        item.PropertyChanged += (_, e) => { if (e.Property == NativeMenuItem.IconProperty) iconChanges++; };
        int observers = CommandBindings.ObserverCount;
        for (int i = 0; i < 1000; i++)
            CommandBindings.Apply(item, "media.export-original", () => label, () => shortcut);
        Assert.Same(icon, item.Icon);
        Assert.Equal(0, iconChanges);
        Assert.Equal(observers, CommandBindings.ObserverCount);
        label = "Second";
        shortcut = "F9";
        ShortcutService.Publish(new ShortcutBindingsSection());
        Assert.Equal("Second  F9", item.Header);
        Assert.Same(icon, item.Icon);
        Assert.Equal(0, iconChanges);
        Assert.Null(item.Gesture);

        // The normal catalog presentation also updates on rebinding and language re-apply.
        CommandBindings.Apply(item, "media.export-original");
        ShortcutService.Publish(new ShortcutBindingsSection().Set("media.export-original", [ShortcutGesture.Parse("F9")]));
        Assert.Equal(CommandCatalog.Label("media.export-original") + "  " + CommandCatalog.ShortcutDisplay("media.export-original"), item.Header);
        Texts.SetLanguage("de");
        CommandBindings.Apply(item, "media.export-original");
        Assert.StartsWith("Exportieren", item.Header);
        Assert.Same(icon, item.Icon);
        Assert.Equal(0, iconChanges);
    });

    [Fact]
    public void ThemeAndMaskChangesReplaceIconAndReturningReusesOriginal() => WithApplication(app =>
    {
        NativeMenuItem item = new();
        CommandBindings.Apply(item, "media.export-original");
        Bitmap light = Assert.IsType<Bitmap>(item.Icon);
        AssertToneAndAlpha(light, "media.export-original", 30);
        int changes = 0;
        item.PropertyChanged += (_, e) => { if (e.Property == NativeMenuItem.IconProperty) changes++; };
        app.RequestedThemeVariant = ThemeVariant.Dark;
        Assert.Equal(ThemeVariant.Dark, app.ActualThemeVariant);
        CommandBindings.Apply(item, "media.export-original");
        Bitmap dark = Assert.IsType<Bitmap>(item.Icon);
        Assert.NotSame(light, dark);
        AssertToneAndAlpha(dark, "media.export-original", 240);
        app.RequestedThemeVariant = ThemeVariant.Light;
        CommandBindings.Apply(item, "media.export-original");
        Assert.Same(light, item.Icon);
        CommandBindings.Apply(item, "media.copy-path");
        Assert.NotSame(light, item.Icon);
        CommandBindings.Apply(item, "media.export-original");
        Assert.Same(light, item.Icon);
        Assert.Equal(4, changes);
    });

    [Fact]
    public void CacheIsBoundedByDistinctCatalogMasksAndTwoTonesNotItemOrCommandCount() => WithApplication(app =>
    {
        var observed = new Dictionary<(string, bool), object>();
        foreach (bool dark in new[] { false, true })
        {
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            foreach (CommandDefinition command in CommandCatalog.All)
            {
                NativeMenuItem item = new();
                CommandBindings.Apply(item, command.Id);
                var key = (command.Icon, dark);
                if (observed.TryGetValue(key, out object? prior)) Assert.Same(prior, item.Icon);
                else observed.Add(key, item.Icon!);
            }
        }
        var cache = (IDictionary)typeof(CommandBindings).GetField("NativeIcons", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        int bound = 2 * CommandCatalog.All.Select(c => c.Icon).Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(bound, observed.Count);
        Assert.Equal(bound, cache.Count);
        MethodInfo resolve = typeof(CommandBindings).GetMethod("NativeIcon", BindingFlags.NonPublic | BindingFlags.Static)!;
        for (int i = 0; i < 10; i++)
        {
            TargetInvocationException error = Assert.Throws<TargetInvocationException>(() => resolve.Invoke(null, ["not-a-catalog-command-" + i]));
            Assert.IsType<KeyNotFoundException>(error.InnerException);
        }
        Assert.Equal(bound, cache.Count);
    });

    [Fact]
    public void RemovingOneMenuConsumerDoesNotInvalidateSharedBitmap() => WithApplication(app =>
    {
        NativeMenu menu = new();
        NativeMenuItem first = CommandBindings.CreateNativeMenuItem("media.export-original", () => { });
        NativeMenuItem second = CommandBindings.CreateNativeMenuItem("media.export-original", () => { });
        menu.Items.Add(first);
        Bitmap shared = Assert.IsType<Bitmap>(first.Icon);
        Assert.Same(shared, second.Icon);
        menu.Items.Clear();
        first.Icon = null; // Detach the borrowed image; the application cache owns its lifetime.
        AssertToneAndAlpha(Assert.IsType<Bitmap>(second.Icon), "media.export-original", 30);
        CommandBindings.Apply(new NativeMenuItem(), "media.export-original");
        AssertToneAndAlpha(shared, "media.export-original", 30);
    });

    [Fact]
    public void CallerOwnedTrayIconIsNotReplacedByApplyOrShortcutPublication() => WithApplication(app =>
    {
        using Stream source = AssetLoader.Open(new Uri(CommandBindings.IconUri("download")));
        using Bitmap owned = new(source);
        NativeMenuItem item = new() { Icon = owned };
        int iconChanges = 0;
        item.PropertyChanged += (_, e) => { if (e.Property == NativeMenuItem.IconProperty) iconChanges++; };
        string label = "First";
        CommandBindings.Apply(item, "media.export-original", () => label, applyIcon: false);
        Assert.Same(owned, item.Icon);
        label = "Changed";
        app.RequestedThemeVariant = ThemeVariant.Dark;
        ShortcutService.Publish(new ShortcutBindingsSection().Set("media.export-original", [ShortcutGesture.Parse("F9")]));
        Assert.Same(owned, item.Icon);
        Assert.Equal(0, iconChanges);
        Assert.Equal("Changed  " + CommandCatalog.ShortcutDisplay("media.export-original"), item.Header);
        Assert.Null(item.Gesture);
        var cache = (IDictionary)typeof(CommandBindings).GetField("NativeIcons", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.DoesNotContain(cache.Values.Cast<object>(), cached => ReferenceEquals(cached, owned));
        using MemoryStream pixels = new();
        owned.Save(pixels); // Apply/Publish did not dispose the caller-owned image either.
        Assert.True(pixels.Length > 0);
        item.Icon = null; // Only this caller owns and disposes its source; no shared icon was handed to it.
    });

    private static void AssertToneAndAlpha(Bitmap bitmap, string id, byte tone)
    {
        using Stream source = AssetLoader.Open(new Uri(CommandBindings.IconUri(CommandCatalog.Get(id).Icon)));
        using SKBitmap mask = SKBitmap.Decode(source);
        using MemoryStream encoded = new();
        bitmap.Save(encoded);
        using SKBitmap actual = SKBitmap.Decode(encoded.ToArray());
        Assert.Equal(mask.Width, actual.Width);
        Assert.Equal(mask.Height, actual.Height);
        int opaque = 0;
        for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
            {
                SKColor pixel = actual.GetPixel(x, y);
                Assert.Equal(mask.GetPixel(x, y).Alpha, pixel.Alpha);
                if (pixel.Alpha != 255) continue; // Avoid RGB rounding from premultiplication on antialiased edges.
                opaque++;
                Assert.Equal(tone, pixel.Red);
                Assert.Equal(tone, pixel.Green);
                Assert.Equal(tone, pixel.Blue);
            }
        Assert.True(opaque > 0);
    }
}
