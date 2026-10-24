using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Updates;

namespace MeowChan.DesktopSmoke;

internal static class UpdateGuiChecks
{
    internal static async Task RunAsync(MainWindow owner, string browserCapture)
    {
        string profile = Path.Combine(owner.SessionHost.ProfileDirectory, "update-gui-fixture");
        Config config = Config.LoadConfig(profile);
        config.DownloadPath = Path.Combine(profile, "downloads");
        config.SaveConfig();
        Handler handler = new();
        string? disabled = null;
        await using DesktopSessionHost host = new(profile, [], updateSessionFactory: (path, settings) =>
            new UpdateSession(path, settings, new HttpClient(handler), disableEnvironment: () => disabled));
        MainWindow window = new(host);
        AboutWindow? about = null;
        window.Show(owner);
        try
        {
            await Program.WaitUntilAsync(() => host.State == DesktopSessionState.Ready && window.IsVisible);
            Require(handler.Requests == 0, "Update startup performed HTTP before its 30-second delay.");
            MenuItem check = window.FindControl<MenuItem>("WorkspaceMenuItem")!.Items.OfType<MenuItem>()
                .Single(item => item.Name == "CheckUpdatesMenuItem");
            check.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Program.WaitUntilAsync(() => window.OwnedWindows.OfType<AboutWindow>().Any() && host.Updates.View.Update != null);
            about = window.OwnedWindows.OfType<AboutWindow>().Single();
            await Program.WaitUntilAsync(() => about.FindControl<TextBlock>("AvailableUpdateText")!.Text!.Contains("9.8.7"));
            Require(handler.Requests == 1, "Help update action did not perform exactly one fixture request.");
            about.FindControl<Button>("UpdateReleaseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => File.ReadAllLines(browserCapture).Length >= 21);
            about.FindControl<Button>("UpdateDownloadButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => File.ReadAllLines(browserCapture).Length >= 28);
            string[] handoffs = File.ReadAllLines(browserCapture);
            Require(handoffs[16] == Handler.ReleaseUrl && handoffs[23] == Handler.DownloadUrl,
                "Update browser handoff differs: " + handoffs[16] + " | " + handoffs[23] + "; runtime=" + System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
            about.Close();
            about = null;

            foreach (string language in new[] { "en", "de" })
            foreach (string theme in new[] { "Dark", "Light" })
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            {
                await host.SaveViewPreferencesAsync(current => { current.Language = language; current.Theme = theme; });
                Texts.SetLanguage(language);
                ThemeVariant expectedTheme = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
                Application.Current!.RequestedThemeVariant = expectedTheme;
                ComboBox modes = window.FindControl<ComboBox>("WorkspaceModeComboBox")!;
                modes.SelectedItem = modes.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == mode);
                await Program.WaitUntilAsync(() => host.Config.WorkspaceMode == mode
                    && Config.LoadConfig(profile).WorkspaceMode == mode
                    && window.FindControl<Button>("AddUrlButton")!.IsEnabled);
                window.Width = mode == "Barebones" ? 640 : 760;
                window.Height = mode == "Barebones" ? 480 : 500;
                await Task.Delay(60);
                window.UpdateLayout();
                Require(Texts.Language == language && window.ActualThemeVariant == expectedTheme,
                    "The requested language/theme matrix state was not applied.");
                Button notice = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "UpdateNoticeButton");
                Require(notice.IsVisible && notice.Bounds.Width > 0 && notice.Bounds.Height > 0,
                    "Update notice is missing in " + language + "/" + theme + "/" + mode);
                Require(AutomationProperties.GetName(notice) == Texts.T("updates.available", "9.8.7"), "Update notice has no localized accessible name.");
                Rect? bounds = notice.TransformToVisual(window) is { } transform ? new Rect(notice.Bounds.Size).TransformToAABB(transform) : null;
                Require(bounds.HasValue && bounds.Value.Right <= window.ClientSize.Width + 1 && bounds.Value.Bottom <= window.ClientSize.Height + 1,
                    "Update status action is clipped at the workspace minimum size.");
                Capture(window, $"smoke-updates-{language}-{theme}-{mode}-notice.png");
                notice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => window.OwnedWindows.OfType<AboutWindow>().Any());
                about = window.OwnedWindows.OfType<AboutWindow>().Single();
                await Task.Delay(40);
                Require(about.FindControl<TextBlock>("AvailableUpdateText")!.Text == Texts.T("updates.available", "9.8.7"), "About update version is not localized.");
                Require(about.ActualThemeVariant == expectedTheme, "About did not inherit the selected theme.");
                Capture(about, $"smoke-updates-{language}-{theme}-{mode}-about.png");
                about.Close();
                about = null;
                if (mode == "Advanced")
                {
                    window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    await RevealUpdateSettingsAsync(window);
                    Capture(window, $"smoke-updates-settings-{language}-{theme}.png");
                    window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }

            // Settings drafts change no live preference until Save, and Discard restores all update/budget fields.
            window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            SettingsEditor editor = window.SettingsEditor;
            Require(editor.FindControl<CheckBox>("AutomaticUpdatesInput")!.IsEffectivelyEnabled,
                "Update settings did not use their actual profile session's enabled state.");
            editor.AutomaticUpdateChecks = false;
            editor.IncludePrereleaseUpdates = true;
            editor.MinimumFreeSpaceGiB = 3;
            editor.ProductionWorkers = 2;
            Require(host.Config.AutomaticUpdateChecks && host.Config.MinimumFreeSpaceGiB == 1, "Settings draft leaked into the running profile.");
            editor.CancelChanges();
            Require(editor.AutomaticUpdateChecks && !editor.IncludePrereleaseUpdates && editor.MinimumFreeSpaceGiB == 1 && editor.ProductionWorkers == 4,
                "Discard failed to restore update and resource preferences.");
            editor.AutomaticUpdateChecks = false;
            editor.MinimumFreeSpaceGiB = 2;
            editor.SelectedLanguage = editor.LanguageChoices.Single(choice => choice.Code == "de");
            editor.SelectedTheme = editor.ThemeChoices.Single(choice => choice.Value == "Light");
            editor.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !editor.IsBusy && !host.Config.AutomaticUpdateChecks);
            Require(!editor.HasError && Config.LoadConfig(profile).MinimumFreeSpaceGiB == 2, "Updated settings were not persisted.");
            await RevealUpdateSettingsAsync(window);
            Capture(window, "smoke-updates-settings-de-Light.png");

            // Disabled automatic checks stay silent; actual manual HTTP errors remain visible across language changes.
            handler.Status = HttpStatusCode.Forbidden;
            await host.Updates.CheckAsync(UpdateCheckKind.Automatic);
            Require(host.Updates.View.ManualResult == null, "A disabled automatic update check leaked into manual status.");
            about = new AboutWindow(host.Updates);
            about.Show(window);
            about.FindControl<Button>("CheckUpdatesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => host.Updates.View.ManualResult?.Error == UpdateCheckError.RateLimited);
            await Program.WaitUntilAsync(() => about.FindControl<TextBlock>("UpdateStatusText")!.Text == Texts.T("updates.error.rateLimited"));
            RequireRetainedUpdateContext(about);
            Capture(about, "smoke-updates-manual-error-de.png");
            int handoffLines = File.ReadAllLines(browserCapture).Length;
            about.FindControl<Button>("UpdateReleaseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => File.ReadAllLines(browserCapture).Length == handoffLines + 7);
            about.FindControl<Button>("UpdateDownloadButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => File.ReadAllLines(browserCapture).Length == handoffLines + 14);
            string[] retainedHandoffs = File.ReadAllLines(browserCapture);
            Require(retainedHandoffs[handoffLines + 2] == Handler.ReleaseUrl
                && retainedHandoffs[handoffLines + 9] == Handler.DownloadUrl,
                "A failed check changed the retained release or download action target.");
            await CheckRetainedUpdateGeometryAsync(about);
            Texts.SetLanguage("en");
            await Program.WaitUntilAsync(() => about.FindControl<TextBlock>("UpdateStatusText")!.Text == Texts.T("updates.error.rateLimited"));
            about.FindControl<Button>("SkipUpdateButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => host.Updates.View.Update == null);
            Require(new UpdateStateStore(profile).Load().SkippedVersion?.ToString() == "9.8.7", "Skip did not persist the selected version.");
            await Program.WaitUntilAsync(() => !about.FindControl<TextBlock>("AvailableUpdateText")!.IsVisible
                && !about.FindControl<WrapPanel>("UpdateActions")!.IsVisible);
            disabled = "1";
            await host.Updates.ConfigureAsync(host.Config);
            await Program.WaitUntilAsync(() => about.FindControl<TextBlock>("UpdateStatusText")!.Text == Texts.T("updates.disabled"));
            Require(!about.FindControl<Button>("CheckUpdatesButton")!.IsEnabled, "Distribution disable did not disable manual checking.");
            Capture(about, "smoke-updates-disabled-en.png");
            about.Close();
            about = null;
            Console.WriteLine("SMOKE Updates: fixture Help/About/status integration, EN/DE × Dark/Light × B/S/A, minimum sizes, exact browser handoff, settings draft/save/discard, skip, manual errors and distribution override passed.");
        }
        finally
        {
            about?.Close();
            window.Close();
            await Program.WaitUntilAsync(() => !window.IsVisible);
            Texts.SetLanguage(owner.SessionHost.Config.Language);
            Application.Current!.RequestedThemeVariant = owner.SessionHost.Config.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        }
    }

    private static void RequireRetainedUpdateContext(AboutWindow about)
    {
        TextBlock available = about.FindControl<TextBlock>("AvailableUpdateText")!;
        TextBlock status = about.FindControl<TextBlock>("UpdateStatusText")!;
        WrapPanel actions = about.FindControl<WrapPanel>("UpdateActions")!;
        Require(available.IsVisible && available.Text == Texts.T("updates.available", "9.8.7"),
            "The manual failure hid the known release version while retaining its actions.");
        Require(status.IsVisible && status.Text == Texts.T("updates.error.rateLimited"),
            "The retained release version replaced the manual check error.");
        Require(actions.IsVisible && new[] { "UpdateReleaseButton", "UpdateDownloadButton", "SkipUpdateButton" }
            .All(name => about.FindControl<Button>(name)!.IsEffectivelyEnabled),
            "The known release actions were lost after a failed manual check.");
    }

    private static async Task CheckRetainedUpdateGeometryAsync(AboutWindow about)
    {
        foreach (string language in new[] { "en", "de" })
        foreach (string theme in new[] { "Dark", "Light" })
        foreach (Size size in new[] { new Size(800, 660), new Size(440, 400) })
        {
            Texts.SetLanguage(language);
            ThemeVariant variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
            Application.Current!.RequestedThemeVariant = variant;
            about.Width = size.Width;
            about.Height = size.Height;
            await Program.WaitUntilAsync(() => Math.Abs(about.ClientSize.Width - size.Width) <= 1
                && Math.Abs(about.ClientSize.Height - size.Height) <= 1
                && about.ActualThemeVariant == variant
                && about.FindControl<TextBlock>("AvailableUpdateText")!.Text == Texts.T("updates.available", "9.8.7"));
            about.UpdateLayout();
            RequireRetainedUpdateContext(about);
            ScrollViewer scroll = about.FindControl<ScrollViewer>("AboutScrollViewer")!;
            TextBlock error = about.FindControl<TextBlock>("UpdateStatusText")!;
            error.BringIntoView();
            await Program.WaitUntilAsync(() => FullyInViewport(error, scroll));
            Capture(about, $"smoke-updates-retained-error-{language}-{theme}-{(int)size.Width}-error.png");
            TextBlock available = about.FindControl<TextBlock>("AvailableUpdateText")!;
            WrapPanel actions = about.FindControl<WrapPanel>("UpdateActions")!;
            actions.BringIntoView();
            await Program.WaitUntilAsync(() => FullyInViewport(available, scroll) && FullyInViewport(actions, scroll));
            Require(new[] { "UpdateReleaseButton", "UpdateDownloadButton", "SkipUpdateButton" }
                .All(name => FullyInViewport(about.FindControl<Button>(name)!, scroll)),
                "A retained release action is clipped at the About minimum size.");
            Capture(about, $"smoke-updates-retained-error-{language}-{theme}-{(int)size.Width}-actions.png");
        }
        about.Width = 800;
        about.Height = 660;
    }

    private static bool FullyInViewport(Control control, ScrollViewer scroll) =>
        control.TranslatePoint(default, scroll) is { } point && point.X >= -1 && point.Y >= -1
        && point.X + control.Bounds.Width <= scroll.Bounds.Width + 1
        && point.Y + control.Bounds.Height <= scroll.Bounds.Height + 1;

    private static async Task RevealUpdateSettingsAsync(MainWindow window)
    {
        await Program.WaitUntilAsync(() => window.SettingsPageVisible);
        window.UpdateLayout();
        TextBlock privacy = window.SettingsEditor.FindControl<TextBlock>("UpdatePrivacyText")!;
        CheckBox automatic = window.SettingsEditor.FindControl<CheckBox>("AutomaticUpdatesInput")!;
        ScrollViewer scroll = window.SettingsEditor.FindControl<ScrollViewer>("SettingsScrollViewer")!;
        privacy.BringIntoView();
        await Program.WaitUntilAsync(() => privacy.TranslatePoint(default, scroll) is { } bottom
            && automatic.TranslatePoint(default, scroll) is { } top
            && top.Y >= -1 && bottom.Y + privacy.Bounds.Height <= scroll.Bounds.Height + 1);
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        double scale = window.RenderScaling;
        using RenderTargetBitmap bitmap = new(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale), (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, name), PngBitmapEncoderOptions.Default);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Handler : HttpMessageHandler
    {
        internal const string ReleaseUrl = "https://github.com/grassmann-it/MeowChan/releases/tag/v9.8.7";
        internal const string DownloadUrl = "https://github.com/grassmann-it/MeowChan/releases/download/v9.8.7/MeowChan-9.8.7-linux-x64-system-vlc.tar.gz";
        internal int Requests;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            string body = JsonSerializer.Serialize(new { tag_name = "v9.8.7", html_url = ReleaseUrl, draft = false, prerelease = false,
                assets = new[] { new { name = "MeowChan-9.8.7-linux-x64-system-vlc.tar.gz", browser_download_url = DownloadUrl } } });
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
