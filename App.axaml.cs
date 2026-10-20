using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MeowChan.Desktop;
using Serilog;
using System;

namespace MeowChan;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected virtual MainWindow CreateMainWindow() => new MainWindow();

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                desktop.MainWindow = CreateMainWindow();
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Desktop startup failed.");
                desktop.MainWindow = new StartupErrorWindow(exception.Message);
            }
            Program.StartupSmoke?.Attach(desktop, desktop.MainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
