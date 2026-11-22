using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using Semi.Avalonia;
using Ursa.Themes.Semi;

namespace MeowChan.DesktopTests;

internal static class BooruReviewTestPlatform
{
    internal static IDisposable Enter()
    {
        IDisposable scope = AvaloniaLocator.EnterScope();
        try
        {
            AvaloniaLocator.CurrentMutable.Bind<IAssetLoader>().ToConstant(new StandardAssetLoader());
            SkiaPlatform.Initialize();
            BindTextShaper();
            typeof(AvaloniaHeadlessPlatform).GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }]);
            TokenApplication app = TokenApplication.Bind();
            app.RegisterServices();
            AvaloniaLocator.CurrentMutable.Bind<IThemeVariantHost>().ToConstant(app);
            AvaloniaLocator.CurrentMutable.Bind<IGlobalStyles>().ToConstant(app);
            app.Styles.Insert(0, new SemiTheme());
            app.Styles.Insert(1, new UrsaSemiTheme());
            Type tooltipService = typeof(ToolTip).Assembly.GetType("Avalonia.Controls.IToolTipService", throwOnError: true)!;
            return new PlatformScope(scope, (IDisposable)AvaloniaLocator.Current.GetRequiredService(tooltipService));
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

    private sealed class PlatformScope(IDisposable locator, IDisposable tooltips) : IDisposable
    {
        public void Dispose()
        {
            // The service subscribes to static ToolTip properties as well as the local input manager.
            try { tooltips.Dispose(); }
            finally { locator.Dispose(); }
        }
    }
}
