using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Rules;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Evidence only: the Rules page (a board rule with a selected template) and every Settings category at 1440 x 920 and
/// 900 x 760, Dark and Light, EN and DE, to compare the action buttons, label/field spacing and left edges with the
/// Plugins page. Nothing is saved; the rule and template stores live in a throw-away profile directory.
/// </summary>
internal static class RulesSettingsCaptureChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        string language = Texts.Language, mode = window.SessionHost.Config.WorkspaceMode;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        double width = window.Width, height = window.Height;
        string profile = Path.Combine(Config.GetConfigDirectory(), "rules-settings-captures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        MediaRuleStore rules = new(profile, lease); await rules.LoadAsync();
        RuleTemplateStore templates = new(profile, lease); await templates.LoadAsync();
        MediaRuleScope board = MediaRuleScope.ForBoard("fixture", "a");
        await rules.SaveAsync(new[] { new MediaRule(board, true, new(MediaTypeSelectionMode.Selected, new[] { "jpg", "png" }),
            new(MediaSizeLimitMode.Limited, 3_000_000)) }, rules.Current.Revision);
        await templates.SaveAsync(new[] {
            new RuleTemplate(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Size only", sizeLimit: new(MediaSizeLimitMode.Limited, 1_000_000)),
            new RuleTemplate(Guid.Parse("00000000-0000-0000-0000-000000000002"), "Video", new(MediaTypeSelectionMode.Selected, new[] { "mp4", "webm" })),
        }, templates.Current.Revision);
        ContentControl host = window.FindControl<ContentControl>("RulesPageHost")!; object? retained = host.Content;
        RulesPage page = new(() => rules.Current, (values, revision) => rules.SaveAsync(values, revision));
        page.AttachTemplateServices(() => templates.LoadAsync(), (values, revision) => templates.SaveAsync(values, revision));
        try
        {
            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Rules);
            host.Content = page;
            page.IsAvailable = true; page.EditScope(board);
            await Program.WaitUntilAsync(() => page.CanExecuteTemplateCommand("rules.template-edit") || page.CanExecuteTemplateCommand("rules.template-create"));
            await Settle();
            ComboBox picker = page.GetVisualDescendants().OfType<ComboBox>().First(box => box.Name == "RuleTemplateList");
            foreach ((double w, double h, string size) in new[] { (1440d, 920d, "1440"), (900d, 760d, "900") })
            foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (string lang in new[] { "en", "de" })
            {
                await Prepare(window, w, h, variant, lang);
                if (picker.SelectedIndex < 0) picker.SelectedIndex = 1;
                await Settle();
                string suffix = size + "-" + lang + "-" + variant.Key!.ToString()!.ToLowerInvariant();
                ScrollViewer scroll = page.FindControl<ScrollViewer>("RulesScroll")!;
                for (int part = 0; part < 2; part++)
                {
                    scroll.Offset = part == 0 ? default : new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
                    await Settle();
                    Program.CaptureWindow(window, "smoke-rules-page-" + suffix + (part == 0 ? "-top" : "-end") + ".png");
                }
            }
            host.Content = retained;
            window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SettingsEditor editor = window.SettingsEditor;
            await Program.WaitUntilAsync(() => editor.IsEffectivelyVisible && editor.Bounds.Width > 0);
            foreach ((double w, double h, string size) in new[] { (1440d, 920d, "1440"), (900d, 760d, "900") })
            foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (string lang in new[] { "en", "de" })
            {
                await Prepare(window, w, h, variant, lang);
                string suffix = size + "-" + lang + "-" + variant.Key!.ToString()!.ToLowerInvariant();
                ScrollViewer scroll = editor.FindControl<ScrollViewer>("SettingsScrollViewer")!;
                foreach (string section in new[] { "settings.interface", "settings.reading-media", "settings.downloads", "settings.worker-budgets", "settings.updates", "settings.notifications", "settings.behavior", "settings.shortcuts" })
                {
                    await Dispatcher.UIThread.InvokeAsync(editor.UpdateLayout, DispatcherPriority.Background);
                    editor.OpenSectionNavigation();
                    if (!editor.CanGoToSection(section)) { Console.WriteLine("SMOKE capture skipped unavailable category " + section); continue; }
                    editor.GoToSection(section);
                    await Task.Delay(150); await Settle();
                    for (int part = 0; part < 3; part++)
                    {
                        scroll.Offset = part == 0 ? default : new Vector(0, Math.Min(Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height), part * scroll.Viewport.Height * 0.9));
                        await Settle();
                        Program.CaptureWindow(window, "smoke-settings-" + section.Replace("settings.", "") + "-" + suffix + "-p" + part + ".png");
                        if (scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1) break;
                    }
                }
            }
            Console.WriteLine("RULES_SETTINGS_CAPTURES PASS: evidence only.");
        }
        finally
        {
            host.Content = retained; page.EndLifetime();
            Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme;
            await PresentationChecks.SwitchModeAsync(window, mode);
            window.Width = width; window.Height = height;
        }
    }

    private static async Task Prepare(MainWindow window, double w, double h, ThemeVariant variant, string lang)
    {
        Texts.SetLanguage(lang); Application.Current!.RequestedThemeVariant = variant;
        window.WindowState = WindowState.Normal; window.Width = w; window.Height = h;
        await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, w, h) && window.ActualThemeVariant == variant);
        await Settle();
    }

    private static async Task Settle()
    {
        for (int i = 0; i < 3; i++) await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(40);
    }
}
