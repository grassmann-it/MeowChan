using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class SettingsSectionChecks
{
    internal static async Task RunAsync(MainWindow owner, string profile)
    {
        Config config = Config.LoadConfig(profile);
        int commits = 0;
        var window = new SettingsWindow(config, _ => { commits++; return Task.CompletedTask; });
        Task dialog = window.ShowDialog(owner);
        try
        {
            await Wait(() => window.IsVisible);
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                var timer = window.Editor.FindControl<NumericUpDown>("RecheckTimerInput")!;
                var path = window.Editor.FindControl<TextBox>("DownloadPathInput")!;
                window.Editor.OpenSectionNavigation();
                Require(window.Editor.CanGoToSection("settings.downloads"), "Downloads section unavailable in active settings.");
                window.Editor.GoToSection("settings.downloads");
                await Wait(() => window.Editor.FindControl<TextBlock>("SettingsDownloadsHeading")!.IsFocused);
                timer.ApplyTemplate();
                TextBox numericText = timer.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");
                numericText.Focus(); numericText.Text = "invalid section draft";
                path.Text = "relative unfinished folder ü";
                string? raw = timer.Text; decimal? value = timer.Value;
                foreach (string id in new[] { "settings.behavior", "settings.updates", "settings.worker-budgets", "settings.interface", "settings.downloads" })
                {
                    window.Editor.OpenSectionNavigation();
                    Require(window.Editor.CanGoToSection(id), "Expected section was not offered: " + id);
                    window.Editor.GoToSection(id);
                    await Task.Delay(50);
                    Require(timer.Text == raw && timer.Value == value && path.Text == "relative unfinished folder ü" && commits == 0,
                        "A section jump changed draft text/model or saved settings.");
                    Require(!window.Editor.FindControl<SectionNavigationControl>("SettingsSections")!.IsExpanded,
                        "Intentional navigation left contents open.");
                }
                numericText.Focus();
                var scroll = window.Editor.FindControl<ScrollViewer>("SettingsScrollViewer")!;
                var before = scroll.Offset;
                Texts.SetLanguage(language == "en" ? "de" : "en");
                await Task.Delay(50);
                Require(numericText.IsFocused && timer.Text == raw && scroll.Offset == before,
                    "Passive language refresh stole focus, scrolled or replaced raw input.");
            }
            Console.WriteLine("SMOKE settings sections: explicit targets, draft/raw preservation and passive language focus passed.");
        }
        finally
        {
            Texts.SetLanguage("en");
            if (window.IsVisible) window.Close();
            await dialog;
        }
    }
    // Use the public navigation contract: hidden category editors cannot be
    // focused or revealed by BringIntoView alone.
    internal static Task OpenFieldAsync(SettingsEditor editor, string field) => OpenAsync(editor, field switch
    {
        "WorkspaceModeInput" or "LanguageInput" or "ThemeInput" or "CompactDensityInput" => "settings.interface",
        "VideoVolumeInput" or "ReadingFontSizeInput" or "RememberReadingPositionInput" or "ForgetReadingPositionButton" => "settings.reading-media",
        "ProductionWorkersInput" or "WorkerIdleMinutesInput" or "SourceHeapMiBInput" or "FilterHeapMiBInput" => "settings.worker-budgets",
        "AboutSettingsButton" => "settings.behavior",
        _ => "settings.downloads"
    });

    internal static async Task OpenAsync(SettingsEditor editor, string section)
    {
        editor.OpenSectionNavigation();
        Require(editor.CanGoToSection(section), "Settings category unavailable: " + section);
        editor.GoToSection(section);
        string heading = section switch
        {
            "settings.interface" => "SettingsInterfaceHeading",
            "settings.reading-media" => "SettingsReadingMediaHeading",
            "settings.worker-budgets" => "SettingsWorkerBudgetsPageTitle",
            "settings.behavior" => "SettingsBehaviorHeading",
            _ => "SettingsDownloadsHeading"
        };
        await Program.WaitUntilAsync(() => editor.FindControl<Control>(heading) is { IsEffectivelyVisible: true, IsFocused: true });
    }

    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Wait(Func<bool> ready)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(5);
        while (!ready()) { if (DateTime.UtcNow > until) throw new TimeoutException("Section navigation did not settle."); await Task.Delay(20); }
    }
}
