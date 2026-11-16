using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Semi.Avalonia;
using Ursa.Themes.Semi;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using Serilog;
using System;
using System.Globalization;
using System.Linq;

namespace MeowChan;

public partial class App : Application
{
    public override void Initialize()
    {
        LinuxVideoPreflight.Begin();
        using (MeowChan.Diagnostics.PerformanceDiagnostics.Measure("startup.app.harfbuzz")) LinuxHarfBuzzBootstrap.EnsureLoaded();
        using (MeowChan.Diagnostics.PerformanceDiagnostics.Measure("startup.app.xaml")) AvaloniaXamlLoader.Load(this);
        // TH1: the theme dictionaries come from the runtime palette (no overrides yet, so exactly the XAML colours).
        using (MeowChan.Diagnostics.PerformanceDiagnostics.Measure("startup.app.theme-palette")) MeowChan.Desktop.Theming.ThemeRuntime.Install(this);
        MeowChan.Desktop.Controls.HeadingLevels.Install(Styles);
        ThemeLogo.FollowAllWindows();
        ApplyControlLanguage();
        Texts.Changed += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess()) ApplyControlLanguage();
        else Dispatcher.UIThread.Post(ApplyControlLanguage);
    }

    private void ApplyControlLanguage()
    {
        // Vendor controls require a specific culture they ship themselves; other languages, including the
        // bundled previews, keep the application's English fallback instead of the vendors' Chinese defaults.
        string language = Texts.Culture.TwoLetterISOLanguageName;
        CultureInfo semiCulture = CultureInfo.GetCultureInfo(VendorLocale(language, SemiLocales));
        CultureInfo ursaCulture = CultureInfo.GetCultureInfo(VendorLocale(language, UrsaLocales));
        foreach (Avalonia.Styling.IStyle style in Styles)
        {
            if (style is SemiTheme semi) semi.Locale = semiCulture;
            else if (style is UrsaSemiTheme ursa) ursa.Locale = ursaCulture;
        }
        // The CJK font that answers first follows the UI language (zh-Hans: SC, ja: JP).
        ReaderFonts.Apply(this, Texts.Language);
    }

    // Vendor locales the themes actually ship for the built-in languages and the newer bundled previews
    // (zh-Hans, ja, ko, it, pl, cs); es/fr/ru stay on English vendor strings until their own review.
    // Semi: zh-CN, ja-JP, ko-KR, it-IT, pl-PL (no cs/pt). Ursa: zh-CN, cs-CZ, pl-PL (no ja/ko/it/pt).
    internal static readonly (string Language, string Locale)[] SemiLocales =
        { ("de", "de-DE"), ("zh", "zh-CN"), ("ja", "ja-JP"), ("ko", "ko-KR"), ("it", "it-IT"), ("pl", "pl-PL") };
    internal static readonly (string Language, string Locale)[] UrsaLocales =
        { ("de", "de-DE"), ("zh", "zh-CN"), ("cs", "cs-CZ"), ("pl", "pl-PL") };

    internal static string VendorLocale(string language, (string Language, string Locale)[] locales) =>
        locales.FirstOrDefault(entry => entry.Language == language).Locale ?? "en-US";

    protected virtual MainWindow CreateMainWindow() => Program.StartupSmoke?.IntegrityProcess is ApphostIntegrityProcessSmoke policy
        ? new MainWindow(policy.CreateSession())
        : Program.StartupSmoke?.Workflow is ApphostWorkflowSmoke workflow
        ? new MainWindow(workflow.CreateSession())
        : Program.StartupSmoke?.PluginWorkflow is ApphostPluginWorkflowSmoke plugins
        ? new MainWindow(plugins.CreateSession()) : new MainWindow();

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // D1: every window that opens follows the motion policy (class motion-off); the saved setting arrives with the main window.
            MeowChan.Desktop.Motion.MotionPolicy.Install();
            // Esc closes every light-dismiss popup that has no Esc of its own (keyboard-only use, 16.11.2026).
            MeowChan.Desktop.Controls.PopupEscape.Install();
            WindowChrome.LogStartup();
            desktop.Exit += (_, _) => Texts.Changed -= OnLanguageChanged;
            LinuxVideoPreflightResult? video = null;
            try
            {
                using (MeowChan.Diagnostics.PerformanceDiagnostics.Measure("startup.app.video-preflight")) video = LinuxVideoPreflight.CheckBegun();
                if (!video.Success)
                {
                    // No session or watches exist yet. Bootstrap messages use the OS
                    // language because opening the profile is deliberately deferred.
                    Texts.SetLanguage(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en");
                    Log.Error("Video runtime startup check failed: {Kind}: {Diagnostic}", video.Kind, video.Diagnostic);
                    ShowStartupFailure(desktop, Texts.T(video.MessageKey));
                }
                else
                {
                    MainWindow main = CreateMainWindow();
                    desktop.MainWindow = main;
                    UnhandledErrors.Presenter = main.ShowUnexpectedError;
                    main.Closed += (_, _) => UnhandledErrors.Presenter = null;
                }
            }
            catch (SettingsRecoveryRequiredException recovery)
            {
                // No settings are in force, so the language is the readable backup's or the operating system's.
                Log.Error(recovery, "Desktop startup stopped: the settings of the profile need a recovery decision.");
                Texts.Initialize();
                Texts.SetLanguage(SettingsRecoveryRequiredException.StartupLanguage(recovery.SavedLanguage, CultureInfo.CurrentUICulture));
                ShowStartupFailure(desktop, recovery.LocalizedMessage());
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Desktop startup failed.");
                ShowStartupFailure(desktop, exception.Message);
            }
            if (desktop.MainWindow is Window window)
                Program.StartupSmoke?.Attach(desktop, window, video);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowStartupFailure(IClassicDesktopStyleApplicationLifetime desktop, string message)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        StartupErrorWindow error = new(message);
        error.Closed += (_, _) => desktop.Shutdown(2);
        desktop.MainWindow = error;
    }
}
