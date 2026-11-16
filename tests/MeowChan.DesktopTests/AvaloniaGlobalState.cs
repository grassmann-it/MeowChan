using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Skia;

namespace MeowChan.DesktopTests;

/// <summary>
/// The one serialized collection for test classes that mutate process-global Avalonia state (the <see cref="AvaloniaLocator"/>, the primary
/// dispatcher slot, <c>Texts.Changed</c>, the shortcut refreshers) and that do not already sit in one of the other serialized collections.
/// xunit v2 runs every <c>DisableParallelization</c> collection one after the other and only after all parallel collections have finished, so
/// the serialized collections never overlap each other either; a class with default (per-class) collection would overlap the swappers instead.
/// <see cref="SourceScanGuardTests"/> (G7) requires every class that uses the swappers to be in a serialized collection.
/// </summary>
[CollectionDefinition("Avalonia global state", DisableParallelization = true)]
public sealed class AvaloniaGlobalStateCollection;

/// <summary>
/// The design tokens (WorkspaceStyles) as application resources, the way the pages find them in the product: no theme, no window. The single shared
/// copy of what every page test used to declare for itself. Call <see cref="Bind"/> inside an <see cref="AvaloniaLocator.EnterScope"/> of the test, so the
/// application is dropped with the scope and never reaches the root locator.
/// </summary>
internal sealed class TokenApplication : Application
{
    public override void Initialize() =>
        Styles.Add(new StyleInclude(new Uri("avares://MeowChan/")) { Source = new Uri("avares://MeowChan/Desktop/WorkspaceStyles.axaml") });

    /// <summary>Creates this test's own application, binds it as the locator's <see cref="Application"/> and loads the tokens.</summary>
    internal static TokenApplication Bind()
    {
        TokenApplication app = new();
        AvaloniaLocator.CurrentMutable.Bind<Application>().ToConstant(app);
        app.Initialize();
        return app;
    }
}

/// <summary>A Skia platform without windows for tests that decode and copy bitmaps.</summary>
internal static class AvaloniaTestPlatform
{
    private static readonly object Gate = new();
    private static bool _setUp;

    /// <summary>
    /// Sets the Skia platform up once per process: Avalonia allows <see cref="AppBuilder.Setup"/> only once, so a per-test locator scope cannot
    /// redo it. The classes that need it sit in the serialized "Avalonia global state" collection, so the root application it leaves does not
    /// overlap the parallel classes. The returned handle is kept for the callers' <c>using</c>; disposing it does nothing.
    /// </summary>
    internal static IDisposable EnterSkia()
    {
        lock (Gate)
        {
            if (!_setUp && Application.Current == null)
                AppBuilder.Configure<Application>().UseSkia().UseStandardRuntimePlatformSubsystem().UseHarfBuzz()
                    .UseWindowingSubsystem(() => { }).SetupWithoutStarting();
            _setUp = true;
        }
        return NoScope.Instance;
    }

    private sealed class NoScope : IDisposable
    {
        internal static readonly NoScope Instance = new();
        public void Dispose() { }
    }
}
