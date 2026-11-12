using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using MeowChan.Desktop;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// The cat logo follows the effective theme: orange in Light, grey in Dark, live and for new windows.
// Product theme toggle and brand-logo About entry on the private Xvfb; managed input only. The tray is
// a standalone TrayIcon: the private run has no StatusNotifier host, so no native tray is observed.
internal static class LogoThemeChecks
{
    // Alpha-weighted mean red minus blue of the visible cat: about 63 for the orange cat, 29 for the grey one.
    private const double OrangeThreshold = 46;

    internal static async Task RunAsync(MainWindow window)
    {
        Application application = Application.Current ?? throw new InvalidOperationException("The desktop application is unavailable.");
        Image workspaceLogo = window.FindControl<Image>("WorkspaceLogo")!;
        Button toggle = window.FindControl<Button>("ThemeToggleButton")!;
        var tray = new TrayIcon();
        using IDisposable trayLogo = ThemeLogo.Follow(tray);
        AboutWindow? about = null;
        Window? plain = null;
        try
        {
            ThemeVariant initial = window.ActualThemeVariant;
            if (initial != ThemeVariant.Light) await ToggleAsync(window, toggle, ThemeVariant.Light);
            Require(ThemeLogo.AssetFor(window.ActualThemeVariant) == ThemeLogo.LightAsset, "Light did not resolve to the orange asset.");
            RequireLogo(window, workspaceLogo, ThemeVariant.Light, "main window");
            RequireTray(tray, ThemeVariant.Light);
            Program.CaptureWindow(window, "smoke-logo-main-light.png");

            Button brand = window.FindControl<Button>("BrandAboutButton")!;
            brand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => window.OwnedWindows.OfType<AboutWindow>().Any(owned => owned.IsVisible));
            about = window.OwnedWindows.OfType<AboutWindow>().Single();
            Image catLogo = about.FindControl<Image>("CatLogo")!;
            await Program.WaitUntilAsync(() => about.Bounds.Width > 0 && catLogo.Bounds.Width > 0);
            RequireLogo(about, catLogo, ThemeVariant.Light, "About");
            Program.CaptureWindow(about, "smoke-logo-about-light.png");

            plain = new Window { Width = 240, Height = 120, Title = "logo fixture" };
            bool opened = false;
            plain.Opened += (_, _) => opened = true;
            plain.Show(window);
            await Program.WaitUntilAsync(() => opened);
            Require(ReferenceEquals(plain.Icon, ThemeLogo.IconFor(ThemeVariant.Light)), "A dialog without its own icon did not get the orange cat.");

            WindowIcon? lightTrayIcon = tray.Icon;
            // Live switch with every consumer above already open.
            await ToggleAsync(window, toggle, ThemeVariant.Dark);
            RequireLogo(window, workspaceLogo, ThemeVariant.Dark, "main window");
            RequireLogo(about, catLogo, ThemeVariant.Dark, "About");
            Require(ReferenceEquals(plain.Icon, ThemeLogo.IconFor(ThemeVariant.Dark)), "An open dialog kept the orange cat in Dark.");
            Require(!ReferenceEquals(tray.Icon, lightTrayIcon), "The tray icon was not replaced on the theme switch.");
            RequireTray(tray, ThemeVariant.Dark);
            Program.CaptureWindow(window, "smoke-logo-main-dark.png");
            Program.CaptureWindow(about, "smoke-logo-about-dark.png");

            // Windows created after the switch carry the grey cat before they are shown.
            AboutWindow later = new();
            RequireConstructed(later, ThemeVariant.Dark, "a new About window");
            Require(ReferenceEquals(later.FindControl<Image>("CatLogo")!.Source, ThemeLogo.BitmapFor(ThemeVariant.Dark)),
                "A new About window did not show the grey cat before opening.");
            later.Close();
            var settings = new SettingsWindow(new Config());
            RequireConstructed(settings, ThemeVariant.Dark, "a new Settings window");
            settings.Close();
            var startupError = new StartupErrorWindow("logo fixture");
            RequireConstructed(startupError, ThemeVariant.Dark, "a startup error window");
            startupError.Close();

            // "Follow system" resolves to the platform's variant, which is never left unresolved.
            application.RequestedThemeVariant = ThemeVariant.Default;
            await Program.WaitUntilAsync(() => window.ActualThemeVariant == application.ActualThemeVariant);
            ThemeVariant system = application.ActualThemeVariant;
            Require(system == ThemeVariant.Light || system == ThemeVariant.Dark, "System theme did not resolve: " + system);
            RequireLogo(window, workspaceLogo, system, "main window (system " + system + ")");
            RequireTray(tray, system);

            // Back to Light: the switch is live in both directions. The toggle flips the resolved variant.
            if (system == ThemeVariant.Light) await ToggleAsync(window, toggle, ThemeVariant.Dark);
            await ToggleAsync(window, toggle, ThemeVariant.Light);
            RequireLogo(window, workspaceLogo, ThemeVariant.Light, "main window (back to Light)");
            RequireLogo(about, catLogo, ThemeVariant.Light, "About (back to Light)");
            Require(ReferenceEquals(plain.Icon, ThemeLogo.IconFor(ThemeVariant.Light)), "An open dialog did not return to the orange cat.");
            RequireTray(tray, ThemeVariant.Light);
            if (initial != ThemeVariant.Light) await ToggleAsync(window, toggle, initial);
            Console.WriteLine("SMOKE logo theme: main icon/header logo, About icon/logo, plain dialog and standalone tray icon switched live "
                + "Light->Dark->System(" + system + ")->Light; new About/Settings/startup-error windows carried the grey cat before opening; "
                + "rendered logo colour orange in Light, grey in Dark.");
        }
        finally
        {
            plain?.Close();
            about?.Close();
            tray.Dispose();
        }
    }

    // The product theme toggle saves the preference and applies it through Application.RequestedThemeVariant.
    private static async Task ToggleAsync(MainWindow window, Button toggle, ThemeVariant target)
    {
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => window.ActualThemeVariant == target,
            () => "theme=" + window.ActualThemeVariant + " target=" + target);
    }

    private static void RequireLogo(Window owner, Image image, ThemeVariant theme, string name)
    {
        Require(ReferenceEquals(owner.Icon, ThemeLogo.IconFor(theme)), name + " icon is not the " + Cat(theme) + " cat.");
        Require(ReferenceEquals(image.Source, ThemeLogo.BitmapFor(theme)), name + " logo source is not the " + Cat(theme) + " cat.");
        Require(!ReferenceEquals(ThemeLogo.BitmapFor(ThemeVariant.Light), ThemeLogo.BitmapFor(ThemeVariant.Dark)),
            "Light and Dark share one logo bitmap.");
        owner.UpdateLayout();
        double scale = owner.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(image.Bounds.Width * scale),
            (int)Math.Ceiling(image.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(image);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        double redOverBlue = RedOverBlue(stream.ToArray());
        Console.WriteLine($"SMOKE logo theme pixels: {name}; theme={theme}; rendered={bitmap.PixelSize}; meanRedMinusBlue={redOverBlue:F1}");
        Require(IsDark(theme) ? redOverBlue < OrangeThreshold : redOverBlue > OrangeThreshold,
            name + " rendered logo colour does not match the " + Cat(theme) + " cat: " + redOverBlue.ToString("F1"));
    }

    // The tray owns its own icon instance; its saved pixels identify the cat.
    private static void RequireTray(TrayIcon tray, ThemeVariant theme)
    {
        WindowIcon icon = tray.Icon ?? throw new InvalidOperationException("The tray icon has no image.");
        using var stream = new MemoryStream();
        icon.Save(stream);
        double redOverBlue = RedOverBlue(stream.ToArray());
        Console.WriteLine($"SMOKE logo theme pixels: tray; theme={theme}; meanRedMinusBlue={redOverBlue:F1}");
        Require(IsDark(theme) ? redOverBlue < OrangeThreshold : redOverBlue > OrangeThreshold,
            "Tray icon colour does not match the " + Cat(theme) + " cat: " + redOverBlue.ToString("F1"));
    }

    private static void RequireConstructed(Window window, ThemeVariant theme, string name) =>
        Require(ReferenceEquals(window.Icon, ThemeLogo.IconFor(theme)), name + " was not constructed with the " + Cat(theme) + " cat.");

    private static double RedOverBlue(byte[] png)
    {
        using SKBitmap bitmap = SKBitmap.Decode(png) ?? throw new InvalidOperationException("Logo pixels could not be decoded.");
        double weight = 0, sum = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha < 128) continue;
                weight += pixel.Alpha;
                sum += (pixel.Red - pixel.Blue) * (double)pixel.Alpha;
            }
        return weight == 0 ? throw new InvalidOperationException("The logo rendered no visible pixels.") : sum / weight;
    }

    private static bool IsDark(ThemeVariant theme) => theme == ThemeVariant.Dark;

    private static string Cat(ThemeVariant theme) => IsDark(theme) ? "grey" : "orange";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
