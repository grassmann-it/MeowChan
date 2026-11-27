using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Semi.Avalonia;
using Ursa.Themes.Semi;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Controls;
using MeowChan.Persistence;
using Serilog;
using System;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Threading.Tasks;

namespace MeowChan;

public partial class App : Application
{
    // Test-only injection point used by the source-built offline session fixture. Keeping the
    // actual Application type as App preserves the compiled App.axaml loader.
    internal static Func<MainWindow>? SmokeMainWindowFactory { get; set; }

    internal static Action<IClassicDesktopStyleApplicationLifetime, Window, LinuxVideoPreflightResult?>? StartupWindowReady { get; set; }

    public override void Initialize()
    {
        LinuxVideoPreflight.Begin();
        using (MeowChan.Diagnostics.PerformanceDiagnostics.Measure("startup.app.harfbuzz")) LinuxHarfBuzzBootstrap.EnsureLoaded();
        using (MeowChan.Diagnostics.PerformanceDiagnostics.Measure("startup.app.xaml")) AvaloniaXamlLoader.Load(this);
        // TH1: the theme dictionaries come from the runtime palette (no overrides yet, so exactly the XAML colours).
        using (MeowChan.Diagnostics.PerformanceDiagnostics.Measure("startup.app.theme-palette")) MeowChan.Desktop.Theming.ThemeRuntime.Install(this);
        MeowChan.Desktop.Controls.HeadingLevels.Install(Styles);
        ThemeLogo.FollowAllWindows();
        TransientPopupPolicy.Install();
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

    protected virtual MainWindow CreateMainWindow()
    {
        if (SmokeMainWindowFactory is { } factory) return factory();
        return new MainWindow();
    }

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
                    Log.Error("Video runtime startup check failed: {Kind}: {Diagnostic}", video.Kind, video.Diagnostic);
                    ShowStartupFailure(desktop, CreateVideoStartupError(video));
                }
                else if (ProfileStartup.FolderSession is { } folderSession)
                {
                    ShowFolderStartup(desktop, folderSession);
                }
                else
                {
                    SetMainWindow(desktop, CreateMainWindow());
                }
            }
            catch (SettingsRecoveryRequiredException recovery)
            {
                // No settings are in force, so the language is the readable backup's or the operating system's.
                Log.Error(recovery, "Desktop startup stopped: the settings of the profile need a recovery decision.");
                Texts.Initialize();
                Texts.SetLanguage(SettingsRecoveryRequiredException.StartupLanguage(recovery.SavedLanguage, CultureInfo.CurrentUICulture));
                ShowStartupFailure(desktop, new(recovery.LocalizedMessage()));
            }
            catch (BoardAdmissionStartupException recovery)
            {
                Log.Error(recovery, "Desktop startup stopped because the board admission rules could not be loaded.");
                Texts.Initialize();
                Texts.SetLanguage(recovery.SavedLanguage);
                ShowStartupFailure(desktop, new(recovery.LocalizedMessage()));
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Desktop startup failed.");
                ShowStartupFailure(desktop, new(exception.Message));
            }
            if (desktop.MainWindow is Window window)
                StartupWindowReady?.Invoke(desktop, window, video);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowFolderStartup(IClassicDesktopStyleApplicationLifetime desktop, StartupFolderSession folderSession)
    {
        Texts.Initialize();
        Texts.SetLanguage(Texts.ResolveCulture(CultureInfo.CurrentUICulture));
        StartupFolderWindow chooser = new(folderSession, async (profile, temporary) =>
        {
            folderSession.SelectedProfile = profile;
            ProfileStartup.MediaFolder = folderSession.Registration;
            DesktopSessionHost? adopted = null;
            try
            {
                if (!temporary)
                    adopted = await Task.Run(() => new DesktopSessionHost(profile, readOnlyStartupSource: folderSession.Source, admitProfileUpgrade: true));
                MainWindow main = adopted == null ? CreateMainWindow() : new MainWindow(adopted);
                main.TemporaryFolderSession = temporary;
                SetMainWindow(desktop, main);
                main.Show();
            }
            catch
            {
                folderSession.SelectedProfile = null;
                ProfileStartup.MediaFolder = null;
                if (adopted != null) await adopted.DisposeAsync();
                throw;
            }
        });
        desktop.MainWindow = chooser;
    }

    private static void SetMainWindow(IClassicDesktopStyleApplicationLifetime desktop, MainWindow main)
    {
        desktop.MainWindow = main;
        UnhandledErrors.Presenter = main.ShowUnexpectedError;
        main.Closed += (_, _) => UnhandledErrors.Presenter = null;
        if (main.SessionHost.PreUpgradeSnapshot is { } snapshot)
            main.Opened += async (_, _) => await OfferProfileReturnAsync(main, snapshot);
    }

    private static async Task OfferProfileReturnAsync(MainWindow main, string directory)
    {
        try
        {
            ProfileSnapshot snapshot = await Task.Run(() => ProfileSnapshot.Read(directory));
            string savedAt = snapshot.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            string title = UpgradeText("profile.upgrade.title", "Profile upgrade", "Profil-Upgrade");
            string message = UpgradeText("profile.upgrade.return",
                "MeowChan 1.2.0 uses a separate profile copy. To return to 1.1.3, restore the snapshot from {0} and open that folder with 1.1.3. Changes made in 1.2.0 stay in its profile.\n\nSnapshot: {1}",
                "MeowChan 1.2.0 verwendet eine getrennte Profilkopie. Für die Rückkehr zu 1.1.3 stelle die Sicherung vom {0} wieder her und öffne diesen Ordner mit 1.1.3. Änderungen aus 1.2.0 bleiben in seinem Profil.\n\nSicherung: {1}");
            if (!await DialogPrompt.ConfirmAsync(main, title,
                string.Format(CultureInfo.CurrentCulture, message, savedAt, directory),
                UpgradeText("profile.upgrade.restore", "Restore a copy…", "Kopie wiederherstellen…"),
                UpgradeText("profile.upgrade.continue", "Continue in 1.2.0", "In 1.2.0 fortfahren"),
                danger: false, automationId: "ProfileUpgradeReturn")) return;

            var selected = await LocalFolderPicker.PickAsync(main.StorageProvider, Texts.T("profile.new.chooseFolder"));
            if (selected.Path is not { } parent) return;
            string destination = ProfileOpenPolicy.FreeFolder(parent, "MeowChan-1.1.3");
            await Task.Run(() => snapshot.Restore(directory, destination));
            await DialogPrompt.ConfirmAsync(main, title, destination, Texts.T("profile.new.cancel"),
                Texts.T("profile.new.cancel"), danger: false, automationId: "ProfileUpgradeRestored");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Log.Warning(error, "The profile snapshot was not restored.");
            await DialogPrompt.ConfirmAsync(main,
                UpgradeText("profile.upgrade.restoreFailed", "Profile restore failed", "Profil konnte nicht wiederhergestellt werden"),
                error.Message, Texts.T("profile.new.cancel"), Texts.T("profile.new.cancel"),
                danger: false, automationId: "ProfileUpgradeRestoreFailed");
        }
    }

    private static string UpgradeText(string key, string english, string german)
    {
        string localized = Texts.T(key);
        if (localized != key) return localized;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? german : english;
    }

    internal static StartupErrorWindow CreateVideoStartupError(LinuxVideoPreflightResult video)
    {
        // Video preflight runs before the profile opens, so only bundled languages and the OS culture are available.
        Texts.Initialize();
        Texts.SetLanguage(SettingsRecoveryRequiredException.StartupLanguage(null, CultureInfo.CurrentUICulture));
        return new(Texts.T(video.MessageKey));
    }

    private static void ShowStartupFailure(IClassicDesktopStyleApplicationLifetime desktop, StartupErrorWindow error)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        error.Closed += (_, _) => desktop.Shutdown(2);
        desktop.MainWindow = error;
    }
}
