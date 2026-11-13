using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Window icons, dialog icons and the tray follow the effective theme (orange in Light, grey in Dark), live
// and for new windows. The header logo and the About window show both cats side by side in either theme.
// Product theme toggle and brand-logo About entry on the private Xvfb; managed input only. The tray is
// a standalone TrayIcon: the private run has no StatusNotifier host, so no native tray is observed.
internal static class LogoThemeChecks
{
    // Alpha-weighted mean red minus blue of the visible cat: about 63 for the orange cat, 29 for the grey one.
    private const double OrangeThreshold = 46;

    internal static async Task RunAsync(MainWindow window)
    {
        Application application = Application.Current ?? throw new InvalidOperationException("The desktop application is unavailable.");
        Image workspaceLogo = window.FindControl<Image>("WorkspaceLogo")!, workspaceLogoDark = window.FindControl<Image>("WorkspaceLogoDark")!;
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
            RequireIcon(window, ThemeVariant.Light, "main window");
            RequirePair(window, workspaceLogo, workspaceLogoDark, ThemeVariant.Light, "main window header");
            RequireHeaderFits(window, workspaceLogo, workspaceLogoDark, "main window header (Light)");
            RequireTray(tray, ThemeVariant.Light);
            Program.CaptureWindow(window, "smoke-logo-main-light.png");
            CaptureControl(window.FindControl<Border>("WorkspaceTitleBarFrame")!, "smoke-logo-header-light.png");

            Button brand = window.FindControl<Button>("BrandAboutButton")!;
            brand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => window.OwnedWindows.OfType<AboutWindow>().Any(owned => owned.IsVisible));
            about = window.OwnedWindows.OfType<AboutWindow>().Single();
            Image catLogo = about.FindControl<Image>("CatLogo")!, catLogoDark = about.FindControl<Image>("CatLogoDark")!;
            await Program.WaitUntilAsync(() => about.Bounds.Width > 0 && catLogo.Bounds.Width > 0 && catLogoDark.Bounds.Width > 0);
            RequireIcon(about, ThemeVariant.Light, "About");
            RequirePair(about, catLogo, catLogoDark, ThemeVariant.Light, "About");
            RequireAboutNaming(about);
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
            RequireIcon(window, ThemeVariant.Dark, "main window");
            RequirePair(window, workspaceLogo, workspaceLogoDark, ThemeVariant.Dark, "main window header");
            RequireHeaderFits(window, workspaceLogo, workspaceLogoDark, "main window header (Dark)");
            RequireIcon(about, ThemeVariant.Dark, "About");
            RequirePair(about, catLogo, catLogoDark, ThemeVariant.Dark, "About");
            Require(ReferenceEquals(plain.Icon, ThemeLogo.IconFor(ThemeVariant.Dark)), "An open dialog kept the orange cat in Dark.");
            Require(!ReferenceEquals(tray.Icon, lightTrayIcon), "The tray icon was not replaced on the theme switch.");
            RequireTray(tray, ThemeVariant.Dark);
            Program.CaptureWindow(window, "smoke-logo-main-dark.png");
            Program.CaptureWindow(about, "smoke-logo-about-dark.png");
            CaptureControl(window.FindControl<Border>("WorkspaceTitleBarFrame")!, "smoke-logo-header-dark.png");
            await RequireNarrowAboutAsync(about, catLogo, catLogoDark);
            await RequireCreditsAsync(about, ThemeVariant.Dark);

            // Windows created after the switch carry the grey cat before they are shown.
            AboutWindow later = new();
            RequireConstructed(later, ThemeVariant.Dark, "a new About window");
            Require(ReferenceEquals(later.FindControl<Image>("CatLogo")!.Source, ThemeLogo.BitmapFor(ThemeVariant.Light))
                && ReferenceEquals(later.FindControl<Image>("CatLogoDark")!.Source, ThemeLogo.BitmapFor(ThemeVariant.Dark)),
                "A new About window did not carry both cats before opening.");
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
            RequireIcon(window, system, "main window (system " + system + ")");
            RequirePair(window, workspaceLogo, workspaceLogoDark, system, "main window header (system " + system + ")");
            RequireTray(tray, system);

            // Back to Light: the switch is live in both directions. The toggle flips the resolved variant.
            if (system == ThemeVariant.Light) await ToggleAsync(window, toggle, ThemeVariant.Dark);
            await ToggleAsync(window, toggle, ThemeVariant.Light);
            RequireIcon(window, ThemeVariant.Light, "main window (back to Light)");
            RequirePair(window, workspaceLogo, workspaceLogoDark, ThemeVariant.Light, "main window header (back to Light)");
            RequireIcon(about, ThemeVariant.Light, "About (back to Light)");
            RequirePair(about, catLogo, catLogoDark, ThemeVariant.Light, "About (back to Light)");
            Require(ReferenceEquals(plain.Icon, ThemeLogo.IconFor(ThemeVariant.Light)), "An open dialog did not return to the orange cat.");
            RequireTray(tray, ThemeVariant.Light);
            await RequireNarrowAboutAsync(about, catLogo, catLogoDark);
            await RequireCreditsAsync(about, ThemeVariant.Light);
            about.Close();
            about = null;
            await RequireNarrowHeaderAsync(window, toggle, workspaceLogo, workspaceLogoDark);
            if (initial != ThemeVariant.Light) await ToggleAsync(window, toggle, initial);
            Console.WriteLine("SMOKE logo theme: main window icon, About icon, plain dialog and standalone tray icon switched live "
                + "Light->Dark->System(" + system + ")->Light (orange in Light, grey in Dark); header logo and About showed the orange and the grey cat "
                + "side by side, same size, unclipped, in both themes, with one automation name for the About pair; new About/Settings/startup-error windows "
                + "carried the grey icon before opening; header fit at 640x480 Barebones and 760x500 and About at its 440 minimum.");
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

    // Window icons follow the theme; they are not the header or About image.
    private static void RequireIcon(Window owner, ThemeVariant theme, string name)
    {
        Require(ReferenceEquals(owner.Icon, ThemeLogo.IconFor(theme)), name + " icon is not the " + Cat(theme) + " cat.");
        Require(!ReferenceEquals(ThemeLogo.BitmapFor(ThemeVariant.Light), ThemeLogo.BitmapFor(ThemeVariant.Dark)),
            "Light and Dark share one logo bitmap.");
    }

    // Both cats, orange first, whatever the theme: sources, geometry and rendered colour.
    private static void RequirePair(Window owner, Image orange, Image grey, ThemeVariant theme, string name)
    {
        Require(ReferenceEquals(orange.Source, ThemeLogo.BitmapFor(ThemeVariant.Light)), name + " left logo is not the orange cat in " + theme + ".");
        Require(ReferenceEquals(grey.Source, ThemeLogo.BitmapFor(ThemeVariant.Dark)), name + " right logo is not the grey cat in " + theme + ".");
        owner.UpdateLayout();
        Require(orange.IsEffectivelyVisible && grey.IsEffectivelyVisible && orange.Bounds.Width > 0 && grey.Bounds.Width > 0,
            name + " does not show both cats in " + theme + ".");
        Require(Math.Abs(orange.Bounds.Width - grey.Bounds.Width) < 0.5 && Math.Abs(orange.Bounds.Height - grey.Bounds.Height) < 0.5,
            name + " cats differ in size: " + orange.Bounds.Size + " / " + grey.Bounds.Size);
        Rect first = BoundsIn(orange, owner), second = BoundsIn(grey, owner);
        Require(first.Right <= second.Left + 0.5 && Math.Abs(first.Center.Y - second.Center.Y) < 1,
            name + " cats are not side by side: " + first + " / " + second);
        RequireRendered(owner, orange, orange: true, name + " orange cat", theme);
        RequireRendered(owner, grey, orange: false, name + " grey cat", theme);
    }

    private static void RequireRendered(Window owner, Image image, bool orange, string name, ThemeVariant theme)
    {
        double scale = owner.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(image.Bounds.Width * scale),
            (int)Math.Ceiling(image.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(image);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        double redOverBlue = RedOverBlue(stream.ToArray());
        Console.WriteLine($"SMOKE logo theme pixels: {name}; theme={theme}; rendered={bitmap.PixelSize}; meanRedMinusBlue={redOverBlue:F1}");
        Require(orange ? redOverBlue > OrangeThreshold : redOverBlue < OrangeThreshold,
            name + " rendered logo colour does not match: " + redOverBlue.ToString("F1"));
    }

    private static Rect BoundsIn(Control control, Visual root) =>
        new(control.TranslatePoint(default, root) ?? throw new InvalidOperationException(control.Name + " is not in the window."), control.Bounds.Size);

    // The pair must not push the brand text, the version or the right-hand controls out of the header.
    private static void RequireHeaderFits(MainWindow window, Image orange, Image grey, string name)
    {
        window.UpdateLayout();
        Rect first = BoundsIn(orange, window), second = BoundsIn(grey, window);
        Control version = window.FindControl<TextBlock>("WorkspaceVersion")!;
        Rect versionBounds = BoundsIn(version, window);
        Rect brand = BoundsIn(window.FindControl<Grid>("WorkspaceHeader")!.GetLogicalDescendants().OfType<TextBlock>()
            .First(text => text.Classes.Contains("brand")), window);
        Require(first.Left >= 0 && second.Right <= window.Bounds.Width + 0.5, name + ": the logo pair is clipped by the window.");
        Require(second.Right <= brand.Left + 0.5 && brand.Right <= versionBounds.Left + 0.5,
            $"{name}: the logo pair overlaps the brand text ({second} / {brand} / {versionBounds}).");
        Require(version.IsEffectivelyVisible && versionBounds.Right <= window.Bounds.Width + 0.5, name + ": the version is clipped.");
        double rightEdge = new[] { "TransferSpeed", "ContinueReadingButton", "PauseButton", "FullscreenButton", "ThemeToggleButton" }
            .Select(id => window.FindControl<Control>(id)!).Where(control => control.IsEffectivelyVisible)
            .Min(control => BoundsIn(control, window).Left);
        Require(versionBounds.Right <= rightEdge + 0.5,
            $"{name}: the left cluster (right edge {versionBounds.Right:F1}) overlaps the header controls (left edge {rightEdge:F1}).");
        Require(window.FindControl<MenuItem>("WorkspaceMenuItem")!.IsEffectivelyVisible
            && BoundsIn(window.FindControl<MenuItem>("WorkspaceMenuItem")!, window).Right <= window.Bounds.Width + 0.5,
            name + ": the workspace menu is clipped.");
        Console.WriteLine($"SMOKE logo theme header: {name}; pair={first.Left:F1}..{second.Right:F1} brand={brand.Left:F1}..{brand.Right:F1} " +
            $"version right={versionBounds.Right:F1}; controls left={rightEdge:F1}; window={window.Bounds.Width:F0}");
    }

    // One automation name for the About pair; the images are hidden from the accessibility tree.
    private static void RequireAboutNaming(AboutWindow about)
    {
        StackPanel pair = about.FindControl<StackPanel>("CatLogoPair")!;
        Require(AutomationProperties.GetName(pair) == Texts.T("about.logoDescription") && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(pair)),
            "The About logo pair has no accessible name.");
        foreach (string image in new[] { "CatLogo", "CatLogoDark" })
            Require(AutomationProperties.GetAccessibilityView(about.FindControl<Image>(image)!) == AccessibilityView.Raw,
                image + " is announced separately from the pair.");
        Require(AutomationProperties.GetAccessibilityView(about.FindControl<Image>("CatLogo")!.Parent!) == AccessibilityView.Content,
            "The About pair is not exposed as one content element.");
        Require(ToolTip.GetTip(about.FindControl<Image>("CatLogo")!) as string == Texts.T("about.logoSimba")
            && ToolTip.GetTip(about.FindControl<Image>("CatLogoDark")!) as string == Texts.T("about.logoMila"),
            "The About cats are not named Simba and Mila by tooltip.");
        Require(Texts.T("about.logoDescription").Contains("Simba") && Texts.T("about.logoDescription").Contains("Mila"),
            "The About pair name does not name both cats.");
        Button brand = ((MainWindow)about.Owner!).FindControl<Button>("BrandAboutButton")!;
        Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(brand)), "The header brand button has no accessible name.");
        foreach (string image in new[] { "WorkspaceLogo", "WorkspaceLogoDark" })
            Require(AutomationProperties.GetAccessibilityView(((MainWindow)about.Owner!).FindControl<Image>(image)!) == AccessibilityView.Raw,
                image + " is announced separately from the About button.");
    }

    // The visible photo credit in the Credits tab: text naming both cats and the photographer, two links, one safe-open path.
    private static async Task RequireCreditsAsync(AboutWindow about, ThemeVariant theme)
    {
        TabControl tabs = about.FindControl<TabControl>("AboutTabs")!;
        int previous = tabs.SelectedIndex;
        tabs.SelectedIndex = 1;
        TextBlock credit = about.FindControl<TextBlock>("PhotoCreditText")!;
        Button site = about.FindControl<Button>("PhotoCreditLink")!, flickr = about.FindControl<Button>("PhotoCreditFlickrLink")!;
        await Program.WaitUntilAsync(() => credit.IsEffectivelyVisible && site.IsEffectivelyVisible && flickr.IsEffectivelyVisible && credit.Bounds.Width > 0);
        Require(credit.Text is { } text && text.Contains("Alexander Huxel · Webentwicklung Huxel") && text.Contains("Simba") && text.Contains("Mila"),
            "The photo credit does not name the photographer and both cats: " + credit.Text);
        Require(site.Content as string == "Alexander Huxel · Webentwicklung Huxel" && site.Tag as string == "https://webentwicklung-huxel.de/"
            && flickr.Content as string == "Flickr" && flickr.Tag as string == "https://flickr.com/people/204852984@N05/",
            "The photo credit links are wrong.");
        Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(flickr)), "The Flickr credit link has no accessible name.");
        Console.WriteLine("SMOKE logo theme credit: " + credit.Text);
        Program.CaptureWindow(about, "smoke-logo-about-credits-" + Name(theme) + "-theme.png");
        tabs.SelectedIndex = previous;
    }

    // About at its minimum width: the pair must leave the title and version readable.
    private static async Task RequireNarrowAboutAsync(AboutWindow about, Image orange, Image grey)
    {
        double width = about.Width, height = about.Height;
        ThemeVariant theme = about.ActualThemeVariant;
        try
        {
            about.Width = about.MinWidth; about.Height = Math.Max(about.MinHeight, 420);
            await Program.WaitUntilAsync(() => Math.Abs(about.Bounds.Width - about.MinWidth) <= 1);
            about.UpdateLayout();
            RequirePair(about, orange, grey, theme, "About (minimum width)");
            Rect pair = BoundsIn(about.FindControl<StackPanel>("CatLogoPair")!, about);
            Rect version = BoundsIn(about.FindControl<SelectableTextBlock>("VersionText")!, about);
            Rect copy = BoundsIn(about.FindControl<Button>("CopyVersionButton")!, about);
            Require(pair.Right <= version.Left + 0.5 && version.Right <= about.Bounds.Width + 0.5 && copy.Right <= about.Bounds.Width + 0.5
                && pair.Left >= 0, $"About at its minimum width clips the header: pair {pair}, version {version}, copy {copy}, window {about.Bounds.Width}.");
            Program.CaptureWindow(about, "smoke-logo-about-min-" + Name(theme) + "-theme.png");
        }
        finally { about.Width = width; about.Height = height; }
        await Program.WaitUntilAsync(() => Math.Abs(about.Bounds.Width - width) <= 1);
    }

    // The header at the supported small sizes, both themes: 640x480 Barebones and 760x500.
    private static async Task RequireNarrowHeaderAsync(MainWindow window, Button toggle, Image orange, Image grey)
    {
        string mode = window.SessionHost.Config.WorkspaceMode;
        Size original = window.Bounds.Size;
        Exception? failureBeforeCleanup = null;
        try
        {
            foreach (var (workspace, width, height) in new[] { ("Barebones", 640, 480), ("Simple", 760, 500) })
            {
                await PresentationChecks.SwitchModeAsync(window, workspace);
                window.Width = width; window.Height = height;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
                foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    if (window.ActualThemeVariant != theme) await ToggleAsync(window, toggle, theme);
                    await Task.Delay(80);
                    RequirePair(window, orange, grey, theme, $"main window header at {width}x{height}");
                    RequireHeaderFits(window, orange, grey, $"{width}x{height} {workspace} ({theme})");
                    CaptureControl(window.FindControl<Border>("WorkspaceTitleBarFrame")!, $"smoke-logo-header-{width}-{Name(theme)}-theme.png");
                }
            }
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                window.Width = original.Width; window.Height = original.Height;
                await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - original.Width) <= 1);
                if (window.ActualThemeVariant != ThemeVariant.Light) await ToggleAsync(window, toggle, ThemeVariant.Light);
            }, ex => Console.Error.WriteLine("SMOKE logo narrow-header cleanup failed: " + ex));
        }
    }

    private static void CaptureControl(Control control, string fileName)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!;
        top.UpdateLayout();
        double scale = top.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(control.Bounds.Width * scale),
            (int)Math.Ceiling(control.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(control);
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine("SMOKE window render: " + path);
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

    private static string Name(ThemeVariant theme) => IsDark(theme) ? "dark" : "light";

    private static string Cat(ThemeVariant theme) => IsDark(theme) ? "grey" : "orange";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
