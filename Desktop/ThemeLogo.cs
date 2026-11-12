using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace MeowChan.Desktop;

/// <summary>
/// The one place that chooses the cat logo: the orange cat in the light theme, the grey cat in the dark
/// theme. Consumers follow the effective (resolved) variant and switch live; an unresolved variant, such
/// as before any theme exists, shows the orange cat. The executable and package icons cannot follow the
/// app theme and stay orange outside this class.
/// </summary>
internal static class ThemeLogo
{
    internal const string LightAsset = "avares://MeowChan/Assets/meowchan.png";
    internal const string DarkAsset = "avares://MeowChan/Assets/meowchan-dark.png";

    private static readonly ConditionalWeakTable<StyledElement, object> s_followed = new();
    private static Bitmap? s_lightBitmap, s_darkBitmap;
    private static WindowIcon? s_lightIcon, s_darkIcon;
    private static bool s_allWindows;

    internal static string AssetFor(ThemeVariant? variant) => IsDark(variant) ? DarkAsset : LightAsset;

    // A custom variant counts as dark when it inherits from Dark.
    private static bool IsDark(ThemeVariant? variant)
    {
        for (ThemeVariant? current = variant; current != null; current = current.InheritVariant)
            if (current == ThemeVariant.Dark) return true;
        return false;
    }

    // Decoded once per asset and shared; neither Image nor Window disposes a source it was given.
    internal static Bitmap BitmapFor(ThemeVariant? variant) => IsDark(variant)
        ? s_darkBitmap ??= Load(DarkAsset)
        : s_lightBitmap ??= Load(LightAsset);

    internal static WindowIcon IconFor(ThemeVariant? variant) => IsDark(variant)
        ? s_darkIcon ??= new WindowIcon(BitmapFor(variant))
        : s_lightIcon ??= new WindowIcon(BitmapFor(variant));

    private static Bitmap Load(string asset)
    {
        using var stream = AssetLoader.Open(new Uri(asset));
        return new Bitmap(stream);
    }

    internal static void Follow(Window window) => Follow(window, variant => window.Icon = IconFor(variant));

    internal static void Follow(Image image) => Follow(image, variant => image.Source = BitmapFor(variant));

    /// <summary>Applies the element's current variant now and again on every change; once per element.</summary>
    internal static void Follow(StyledElement element, Action<ThemeVariant?> apply)
    {
        if (s_followed.TryGetValue(element, out _)) return;
        s_followed.Add(element, apply);
        apply(element.ActualThemeVariant);
        element.ActualThemeVariantChanged += (_, _) => apply(element.ActualThemeVariant);
    }

    /// <summary>The tray is outside the visual tree, so it follows the application's variant until disposed.</summary>
    internal static IDisposable Follow(TrayIcon trayIcon)
    {
        Application? application = Application.Current;
        // The native tray owns its icon separately from the windows, so it receives its own instance.
        void Apply(object? sender, EventArgs e) => trayIcon.Icon = new WindowIcon(BitmapFor(application?.ActualThemeVariant));
        Apply(null, EventArgs.Empty);
        if (application == null) return new Subscription(null);
        application.ActualThemeVariantChanged += Apply;
        return new Subscription(() => application.ActualThemeVariantChanged -= Apply);
    }

    /// <summary>
    /// Dialogs that never choose an icon would otherwise show the platform's executable or desktop-entry
    /// icon, which is always orange. Windows that already follow, including the ones that do so from their
    /// constructor to show the right cat from the first frame, are skipped.
    /// </summary>
    internal static void FollowAllWindows()
    {
        if (s_allWindows) return;
        s_allWindows = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Follow(window));
    }

    private sealed class Subscription(Action? dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose()
        {
            _dispose?.Invoke();
            _dispose = null;
        }
    }
}
