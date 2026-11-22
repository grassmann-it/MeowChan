using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.Styling;
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
            // Use the production shaper in this locator scope without initializing a second application.
            Type shaper = typeof(HarfBuzzApplicationExtensions).Assembly.GetTypes().Single(type => type.Name == "HarfBuzzTextShaper");
            AvaloniaLocator.CurrentMutable.Bind<ITextShaperImpl>().ToConstant((ITextShaperImpl)Activator.CreateInstance(shaper, nonPublic: true)!);
            typeof(AvaloniaHeadlessPlatform).GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }]);
            TokenApplication app = TokenApplication.Bind();
            app.RegisterServices();
            AvaloniaLocator.CurrentMutable.Bind<IThemeVariantHost>().ToConstant(app);
            AvaloniaLocator.CurrentMutable.Bind<IGlobalStyles>().ToConstant(app);
            app.Styles.Insert(0, new SemiTheme());
            app.Styles.Insert(1, new UrsaSemiTheme());
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}
