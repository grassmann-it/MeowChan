using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using Semi.Avalonia;
using Ursa.Themes.Semi;

namespace MeowChan.DesktopTests;

internal static class BooruReviewTestPlatform
{
    internal static IDisposable Enter(bool overlayPopups = true, bool? macOS = null)
    {
        IDisposable scope = AvaloniaLocator.EnterScope();
        try
        {
            AvaloniaLocator.CurrentMutable.Bind<IAssetLoader>().ToConstant(new StandardAssetLoader());
            SkiaPlatform.Initialize();
            BindTextShaper();
            typeof(AvaloniaHeadlessPlatform).GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false, OverlayPopups = overlayPopups }]);
            TokenApplication app = TokenApplication.Bind();
            app.RegisterServices();
            AvaloniaLocator.CurrentMutable.Bind<IThemeVariantHost>().ToConstant(app);
            AvaloniaLocator.CurrentMutable.Bind<IGlobalStyles>().ToConstant(app);
            app.Styles.Insert(0, new SemiTheme());
            app.Styles.Insert(1, new UrsaSemiTheme());
            Type tooltipService = typeof(ToolTip).Assembly.GetType("Avalonia.Controls.IToolTipService", throwOnError: true)!;
            IDisposable tooltips = (IDisposable)AvaloniaLocator.Current.GetRequiredService(tooltipService);
            bool? previousMacOS = ContextClick.MacOSForTests;
            if (macOS is { } model)
            {
                KeyModifiers primary = model ? KeyModifiers.Meta : KeyModifiers.Control;
                AvaloniaLocator.CurrentMutable.Bind<PlatformHotkeyConfiguration>().ToConstant(
                    new PlatformHotkeyConfiguration(primary) { SelectionModifiers = primary });
                ContextClick.MacOSForTests = model;
            }
            return new PlatformScope(scope, tooltips, previousMacOS);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    // Bind the production shaper without initializing a second application.
    internal static void BindTextShaper()
    {
        Type shaper = typeof(HarfBuzzApplicationExtensions).Assembly.GetTypes().Single(type => type.Name == "HarfBuzzTextShaper");
        AvaloniaLocator.CurrentMutable.Bind<ITextShaperImpl>().ToConstant((ITextShaperImpl)Activator.CreateInstance(shaper, nonPublic: true)!);
    }

    internal static void Resize(Window window, double width)
    {
        window.PlatformImpl!.Resize(window.ClientSize.WithWidth(width), WindowResizeReason.User);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private sealed class PlatformScope(IDisposable locator, IDisposable tooltips, bool? previousMacOS) : IDisposable
    {
        public void Dispose()
        {
            ContextClick.MacOSForTests = previousMacOS;
            // The service subscribes to static ToolTip properties as well as the local input manager.
            try { tooltips.Dispose(); }
            finally { locator.Dispose(); }
        }
    }
}
