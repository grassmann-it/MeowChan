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

namespace MeowChan;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
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
        // Vendor controls require a supported specific culture. Unknown language
        // packs retain the application's English fallback instead of Chinese defaults.
        CultureInfo culture = CultureInfo.GetCultureInfo(Texts.Culture.TwoLetterISOLanguageName == "de" ? "de-DE" : "en-US");
        foreach (Avalonia.Styling.IStyle style in Styles)
        {
            if (style is SemiTheme semi) semi.Locale = culture;
            else if (style is UrsaSemiTheme ursa) ursa.Locale = culture;
        }
    }

    protected virtual MainWindow CreateMainWindow() => Program.StartupSmoke?.Workflow is ApphostWorkflowSmoke workflow
        ? new MainWindow(workflow.CreateSession())
        : Program.StartupSmoke?.PluginWorkflow is ApphostPluginWorkflowSmoke plugins
        ? new MainWindow(plugins.CreateSession()) : new MainWindow();

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => Texts.Changed -= OnLanguageChanged;
            LinuxVideoPreflightResult? video = null;
            try
            {
                video = LinuxVideoPreflight.Check();
                if (!video.Success)
                {
                    // No session or watches exist yet. Bootstrap messages use the OS
                    // language because opening the profile is deliberately deferred.
                    Texts.SetLanguage(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en");
                    Log.Error("Video runtime startup check failed: {Kind}: {Diagnostic}", video.Kind, video.Diagnostic);
                    ShowStartupFailure(desktop, Texts.T(video.MessageKey));
                }
                else desktop.MainWindow = CreateMainWindow();
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
