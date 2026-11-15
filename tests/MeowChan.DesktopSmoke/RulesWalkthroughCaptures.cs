using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Rules;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Evidence only (review walkthrough 15.11.2026): every state of the Rules page in one throw-away profile. The overview empty and
/// filled, the wizard's steps 1 to 3 for each scope kind, the Rules and drafts expander, an invalid scope, the settings step with
/// its own values and an invalid extension, save, edit, delete and a retained draft, in EN and DE, Dark and Light, at 1440 and
/// 900 dip. Every capture logs the label and control rows of the visible step (left edge, vertical centre) so alignment is
/// measured, not only looked at: smoke-rules-walk-*.png.
/// </summary>
internal static class RulesWalkthroughCaptures
{
    internal static async Task RunAsync(MainWindow window)
    {
        string language = Texts.Language, mode = window.SessionHost.Config.WorkspaceMode;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        double width = window.Width, height = window.Height;
        string profile = Path.Combine(Config.GetConfigDirectory(), "rules-walkthrough-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        MediaRuleStore rules = new(profile, lease); await rules.LoadAsync();
        RuleTemplateStore templates = new(profile, lease); await templates.LoadAsync();
        await templates.SaveAsync(new[] {
            new RuleTemplate(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Size only", sizeLimit: new(MediaSizeLimitMode.Limited, 1_000_000)),
            new RuleTemplate(Guid.Parse("00000000-0000-0000-0000-000000000002"), "Video", new(MediaTypeSelectionMode.Selected, new[] { "mp4", "webm" })),
        }, templates.Current.Revision);
        ContentControl host = window.FindControl<ContentControl>("RulesPageHost")!; object? retained = host.Content;
        RulesPage page = new(() => rules.Current, (values, revision) => rules.SaveAsync(values, revision));
        page.AttachTemplateServices(() => templates.LoadAsync(), (values, revision) => templates.SaveAsync(values, revision));
        int failures = 0;
        try
        {
            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Rules);
            host.Content = page; page.IsAvailable = true;
            await Prepare(window, 1440, 920, ThemeVariant.Dark, "en");
            await Capture(window, page, "01-overview-empty");

            // Step 1 for each scope kind, then the expander and an invalid scope.
            Click(page.OverviewCreateButton);
            await Until(() => page.IsWizardOpen && page.WizardStep == RuleWizardStep.Scope);
            string[] kinds = { "global", "source", "board", "thread" };
            for (int kind = 0; kind < kinds.Length; kind++)
            {
                page.NewScopeKind = kind;
                await Settle();
                await Capture(window, page, "02-scope-" + kinds[kind]);
            }
            Expander saved = page.FindControl<Expander>("RuleSavedScopesExpander")!;
            saved.IsExpanded = true; await Settle();
            await Capture(window, page, "03-scope-thread-expander");
            saved.IsExpanded = false;
            Click(page.FindControl<Button>("OpenNewRuleScopeButton")!); // the thread scope with empty ids
            await Settle();
            await Capture(window, page, "04-scope-invalid");

            // Global: step 2 with its defaults, then own values, step 3, an invalid value and save.
            page.NewScopeKind = 0; await Settle();
            Click(page.FindControl<Button>("OpenNewRuleScopeButton")!);
            await Until(() => page.WizardStep == RuleWizardStep.Settings);
            await Capture(window, page, "05-settings-global");
            Expander filter = page.FindControl<Expander>("RulePluginExpander")!, templateBlock = page.FindControl<Expander>("RuleTemplatesExpander")!;
            filter.IsExpanded = true; templateBlock.IsExpanded = true; await Settle(); await Task.Delay(150); await Settle();
            await Capture(window, page, "05b-settings-global-expanded", scrollEnd: true);
            filter.IsExpanded = false; templateBlock.IsExpanded = false; await Settle();
            page.TypesMode = 2; page.SizeMode = 2; page.MegabytesText = "12"; await Settle();
            page.ExtensionsText = "jpg, png"; await Settle();
            await Capture(window, page, "06-settings-global-own", scrollEnd: true);
            Click(page.FindControl<Button>("RuleWizardNextButton")!);
            await Until(() => page.WizardStep == RuleWizardStep.Review);
            await Capture(window, page, "07-review-global");
            page.ShowWizardStep(RuleWizardStep.Settings);
            page.MegabytesText = "abc"; await Settle();
            await Capture(window, page, "08-settings-invalid-size");
            page.ShowWizardStep(RuleWizardStep.Review); await Settle();
            await Capture(window, page, "09-review-invalid");
            page.ShowWizardStep(RuleWizardStep.Settings);
            page.MegabytesText = "12"; await Settle();
            Click(page.FindControl<Button>("SaveRuleButton")!);
            await Until(() => page.IsOverviewVisible && rules.Current.Rules.Count == 1);
            await Capture(window, page, "10-overview-saved");

            // A board rule left as a draft, a thread rule saved under it.
            Click(page.OverviewCreateButton);
            await Until(() => page.IsWizardOpen && page.WizardStep == RuleWizardStep.Scope);
            page.NewScopeKind = 2; page.NewSourceId = "fixture"; page.NewBoardId = "a"; await Settle();
            await Capture(window, page, "11-scope-board-filled");
            Click(page.FindControl<Button>("OpenNewRuleScopeButton")!);
            await Until(() => page.WizardStep == RuleWizardStep.Settings);
            await Capture(window, page, "12-settings-board");
            page.ShowWizardStep(RuleWizardStep.Review); await Settle();
            await Capture(window, page, "13-review-board");
            page.CloseWizard();
            await Until(() => page.IsOverviewVisible);
            await Capture(window, page, "14-overview-draft");

            Click(page.OverviewCreateButton);
            await Until(() => page.IsWizardOpen && page.WizardStep == RuleWizardStep.Scope);
            page.NewScopeKind = 3; page.NewSourceId = "fixture"; page.NewBoardId = "a"; page.NewThreadId = "123"; await Settle();
            Click(page.FindControl<Button>("OpenNewRuleScopeButton")!);
            await Until(() => page.WizardStep == RuleWizardStep.Settings);
            page.SizeMode = 2; page.MegabytesText = "4"; await Settle();
            await Capture(window, page, "15-settings-thread");
            Click(page.FindControl<Button>("SaveRuleButton")!);
            await Until(() => page.IsOverviewVisible && rules.Current.Rules.Count == 2);
            await Capture(window, page, "16-overview-two-rules-draft");

            // Edit an existing rule (step 2 from the overview), then delete one.
            page.EditScope(MediaRuleScope.Global);
            await Until(() => page.WizardStep == RuleWizardStep.Settings);
            await Capture(window, page, "17-edit-global");
            page.CloseWizard();
            await Until(() => page.IsOverviewVisible);
            Button? delete = page.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "RuleOverview-delete");
            if (delete != null)
            {
                Click(delete); await Settle(); await Task.Delay(200); await Settle();
                await Capture(window, page, "18-delete-confirmation");
                CapturePopups(window, "18-delete-confirmation-popup");
                await page.DeleteRuleAsync(MediaRuleScope.Global);
                await Settle();
                await Capture(window, page, "19-overview-after-delete");
            }
            else { failures++; Console.WriteLine("RULES_WALK problem: no Delete button on the overview."); }

            // German, long labels: step 1 (thread), step 2 with own values, step 3, overview; then Light and 900 dip.
            foreach ((double w, double h, ThemeVariant variant, string lang, string tag) in new[]
            {
                (1440d, 920d, ThemeVariant.Dark, "de", "de-1440-dark"), (1440d, 920d, ThemeVariant.Light, "en", "en-1440-light"),
                (900d, 760d, ThemeVariant.Dark, "de", "de-900-dark"), (760d, 700d, ThemeVariant.Dark, "de", "de-760-dark"),
            })
            {
                await Prepare(window, w, h, variant, lang);
                await Capture(window, page, "20-overview-" + tag);
                Click(page.OverviewCreateButton);
                await Until(() => page.IsWizardOpen && page.WizardStep == RuleWizardStep.Scope);
                page.NewScopeKind = 3; await Settle();
                await Capture(window, page, "21-scope-thread-" + tag);
                page.NewScopeKind = 1; page.NewSourceId = "fixture"; await Settle();
                Click(page.FindControl<Button>("OpenNewRuleScopeButton")!);
                await Until(() => page.WizardStep == RuleWizardStep.Settings);
                page.TypesMode = 2; page.ExtensionsText = "webm, gif"; page.SizeMode = 2; page.MegabytesText = "7"; await Settle();
                await Capture(window, page, "22-settings-source-" + tag);
                await Capture(window, page, "22-settings-source-" + tag + "-end", scrollEnd: true);
                page.ShowWizardStep(RuleWizardStep.Review); await Settle();
                await Capture(window, page, "23-review-source-" + tag);
                page.DiscardCurrentDraft(); await Settle();
                page.CloseWizard();
                await Until(() => page.IsOverviewVisible);
            }
            Console.WriteLine("RULES_WALKTHROUGH PASS: evidence only; problems logged: " + failures);
        }
        finally
        {
            host.Content = retained; page.EndLifetime();
            Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme;
            await PresentationChecks.SwitchModeAsync(window, mode);
            window.Width = width; window.Height = height;
        }
    }

    private static async Task Capture(MainWindow window, RulesPage page, string name, bool scrollEnd = false)
    {
        ScrollViewer scroll = page.FindControl<ScrollViewer>("RulesScroll")!;
        scroll.Offset = default; await Settle();
        if (scrollEnd) { scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)); await Settle(); }
        Program.CaptureWindow(window, "smoke-rules-walk-" + name + ".png");
        LogGeometry(window, page, name);
    }

    /// <summary>Left edges and vertical centres of the header, the visible label/control rows, cards and expanders.</summary>
    private static void LogGeometry(MainWindow window, RulesPage page, string name)
    {
        string Box(Control? control)
        {
            if (control == null || !control.IsEffectivelyVisible) return "-";
            Point? origin = control.TranslatePoint(new Point(0, 0), window);
            return origin is { } p ? $"x={p.X:0.#} y={p.Y:0.#} w={control.Bounds.Width:0.#} h={control.Bounds.Height:0.#} cy={p.Y + control.Bounds.Height / 2:0.#}" : "-";
        }
        Console.WriteLine($"RULES_WALK {name} header back[{Box(page.FindControl<Control>("RuleWizardCloseButton"))}] title[{Box(page.FindControl<Control>("RulesEditingHeader"))}] "
            + $"steps[{Box(page.FindControl<Control>("RuleWizardSteps"))}] more[{Box(page.FindControl<Control>("RulesMoreButton"))}]");
        foreach (Grid row in page.GetVisualDescendants().OfType<Grid>().Where(grid => grid.IsEffectivelyVisible
                     && (grid.Classes.Contains("rule-row") || grid.Classes.Contains("settings-row"))))
        {
            Control? label = row.Children.OfType<Control>().FirstOrDefault(child => Grid.GetColumn(child) == 0 && Grid.GetRow(child) == 0);
            Control? value = row.Children.OfType<Control>().FirstOrDefault(child => Grid.GetColumn(child) == 1);
            Control? input = value is Panel panel ? panel.Children.OfType<Control>().FirstOrDefault(child => child.IsVisible) : value;
            Console.WriteLine($"RULES_WALK {name} row label[{Box(label)}] control[{Box(input)}]");
        }
        foreach (string id in new[] { "NewRuleScopePanel", "RuleSavedScopesExpander", "RulePreviewSection", "RuleTemplatesCard" })
            Console.WriteLine($"RULES_WALK {name} {id}[{Box(page.FindControl<Control>(id))}]");
    }

    /// <summary>Open popups (the delete confirmation) are their own top levels; a window render does not include them.</summary>
    private static void CapturePopups(MainWindow window, string name)
    {
        int index = 0;
        foreach (Avalonia.Controls.Primitives.Popup popup in window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Where(popup => popup.IsOpen))
        {
            if (popup.Child is not { } child || TopLevel.GetTopLevel(child) is not { } root || ReferenceEquals(root, window)) continue;
            root.UpdateLayout();
            using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = new(new PixelSize(Math.Max(1, (int)Math.Ceiling(root.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(root.Bounds.Height))));
            bitmap.Render(root);
            string path = Path.Combine(AppContext.BaseDirectory, "smoke-rules-walk-" + name + "-" + index++ + ".png");
            bitmap.Save(path);
            Console.WriteLine("SMOKE window render: " + path);
        }
        if (index == 0) Console.WriteLine("RULES_WALK " + name + ": no open popup found");
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task Until(Func<bool> condition)
    {
        await Program.WaitUntilAsync(condition);
        await Settle();
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
        await Task.Delay(60);
    }
}
