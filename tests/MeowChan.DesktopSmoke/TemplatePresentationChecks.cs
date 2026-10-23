using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Rules;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

internal static partial class TemplateComparisonChecks
{
    // Presentation only: explicit persisted Full23 end-state fixture, not a
    // replay of its semantic/alternative tasks and not evidence of those again.
    private static async Task RunPresentationOnlyAsync(MainWindow window)
    {
        const string baseline = "6457265bd2ea03dc231998c940d7b8d7c1f9bb46b17dcae18a37b3f3dbcc0e5c";
        string output = Environment.GetEnvironmentVariable("MEOWCHAN_TEMPLATE_COMPARISON_OUTPUT") ?? "";
        string source = Environment.GetEnvironmentVariable("MEOWCHAN_TEMPLATE_COMPARISON_SOURCE_DIGEST") ?? "";
        Require(Path.IsPathFullyQualified(output) && !Directory.Exists(output), "Presentation output must be fresh and absolute.");
        Require(source.Length == 64 && source.All(Uri.IsHexDigit), "Presentation requires the executed source SHA256.");
        Require(double.TryParse(Environment.GetEnvironmentVariable("MEOWCHAN_TEMPLATE_PRESENTATION_EXPECTED_SCALE"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double expectedScale) && double.IsFinite(expectedScale) && expectedScale > 0,
            "Set MEOWCHAN_TEMPLATE_PRESENTATION_EXPECTED_SCALE to the runner's explicit scale.");
        Directory.CreateDirectory(output);
        string profile = Path.Combine(Config.GetConfigDirectory(), "template-presentation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        MediaRuleStore rules = new(profile, lease); await rules.LoadAsync();
        RuleTemplateStore templates = new(profile, lease); await templates.LoadAsync();
        MediaRuleScope[] scopes = [MediaRuleScope.ForBoard("fixture", "a"), MediaRuleScope.ForBoard("fixture", "b"), MediaRuleScope.ForBoard("fixture", "c")];
        await rules.SaveAsync(scopes.Select((scope, index) => new MediaRule(scope, index == 2,
            new(MediaTypeSelectionMode.Selected, index == 2 ? new[] { "mp4", "webm" } : new[] { "jpg", "png" }),
            new(MediaSizeLimitMode.Limited, (index + 1) * 1_000_000))
            { ImageFilter = index == 2 ? new(MediaImageFilterMode.Plugin, "fixture.missing", "schema-filter", "2",
                new Dictionary<string, string> { ["threshold"] = "7" }) : MediaImageFilterRule.Inherit }), rules.Current.Revision);
        await templates.SaveAsync(new[] {
            new RuleTemplate(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Size only", sizeLimit: new(MediaSizeLimitMode.Limited, 1_000_000)),
            new RuleTemplate(Guid.Parse("00000000-0000-0000-0000-000000000002"), "Video", new(MediaTypeSelectionMode.Selected, new[] { "mp4", "webm" })),
            new RuleTemplate(Guid.Parse("00000000-0000-0000-0000-000000000003"), "Wallpaper", new(MediaTypeSelectionMode.Selected, new[] { "webp" })),
            new RuleTemplate(Guid.Parse("00000000-0000-0000-0000-000000000004"), "Private values", new(MediaTypeSelectionMode.Selected, new[] { "avif", "webp" }))
        }, templates.Current.Revision);
        string fixture = JsonSerializer.Serialize(new { functional_baseline_source_digest = baseline,
            preparation = "explicit persisted Full23 end-state; presentation only, not functional replay", rules = rules.Current.Rules, templates = templates.Current.Templates });
        File.WriteAllText(Path.Combine(output, "fixture.json"), fixture);
        Evidence evidence = new(output, window, source, Hash(Encoding.UTF8.GetBytes(fixture))) { Variant = "presentation-only" };
        string originalLanguage = Texts.Language, originalMode = window.SessionHost.Config.WorkspaceMode;
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        double originalWidth = window.Width, originalHeight = window.Height;
        window.FindControl<MenuItem>("RulesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ContentControl host = window.FindControl<ContentControl>("RulesPageHost")!; object? retained = host.Content;
        RulesPage page = new(() => rules.Current, (values, revision) => rules.SaveAsync(values, revision));
        page.AttachTemplateServices(() => templates.LoadAsync(), (values, revision) => templates.SaveAsync(values, revision));
        host.Content = page; evidence.Page = page;
        string FileHash(string file) => Hash(File.ReadAllBytes(Path.Combine(profile, file)));
        string Draft() => JsonSerializer.Serialize(new { page.ActiveScope, page.RuleEnabled, page.TypesMode, page.SizeMode,
            page.ExtensionsText, raw_fingerprint = page.FindControl<RuleExtensionsInput>("RuleExtensionsInput")!.State.Fingerprint,
            page.MegabytesText, page.HasValidationError, page.HasDiscardableChanges, page.CanSave });
        object State() => new { draft = Draft(), rules_hash = FileHash("media_rules.json"), templates_hash = FileHash("rule_templates.json") };
        evidence.State = State;
        List<object> completed = []; Exception? failure = null;
        try
        {
            page.IsAvailable = true; page.EditScope(scopes[1]);
            await Program.WaitUntilAsync(() => page.CanExecuteTemplateCommand("rules.template-edit") || page.CanExecuteTemplateCommand("rules.template-create"));
            string rulesHash = FileHash("media_rules.json"), templatesHash = FileHash("rule_templates.json"), raw = Draft();
            foreach (string language in new[] { "en", "de" })
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                double cellWidth = mode == "Barebones" ? 640 : 760, cellHeight = mode == "Barebones" ? 480 : 500;
                window.WindowState = WindowState.Normal; window.Width = cellWidth; window.Height = cellHeight;
                Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme;
                evidence.ExpectedLanguage = language; evidence.ExpectedTheme = theme;
                evidence.Task = language + "-" + theme.Key + "-" + mode;
                await Program.WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return Texts.Language == language && window.ActualThemeVariant == theme && page.ActualThemeVariant == theme
                        && PresentationChecks.MatchesRequestedSize(window, cellWidth, cellHeight)
                        && Math.Abs(window.RenderScaling - expectedScale) < 0.01;
                });
                await FlushAsync(); evidence.AssertPresentation();
                ComboBox picker = Find<ComboBox>("RuleTemplateList");
                object selected = picker.Items.Cast<object>().Single(value => value.ToString()!.StartsWith("Video · ", StringComparison.Ordinal));
                string pickerLabel = language == "de" ? "Regelvorlagen" : "Rule templates";
                string itemLabel = language == "de" ? "Video · Erlaubte Dateitypen" : "Video · Allowed file types";
                Require(AutomationProperties.GetName(picker) == pickerLabel && selected.ToString() == itemLabel,
                    "Template picker actual locale labels differ from the requested cell.");
                await evidence.ActionAsync(picker, "Select existing Video for presentation", () => picker.SelectedItem = selected);
                await ShowAsync(picker);
                Require(picker.Focus(NavigationMethod.Tab), "Real template picker could not take focus.");
                await evidence.ActionAsync(picker, "Open actual template picker", () => picker.IsDropDownOpen = true);
                Popup? popup = null;
                await Program.WaitUntilAsync(() =>
                {
                    window.UpdateLayout(); popup = picker.GetTemplateDescendants().OfType<Popup>().SingleOrDefault();
                    return popup?.IsOpen == true && popup.Child is { Bounds.Width: > 0, Bounds.Height: > 0 };
                });
                Control popupChild = popup!.Child!;
                Require(popupChild.ActualThemeVariant == theme && popupChild.GetVisualDescendants().OfType<TextBlock>().Any(value => value.Text == itemLabel),
                    "Actual template picker popup has wrong theme or untranslated visible item labels.");
                CapturePickerPopup(popupChild, evidence.Task + "-picker.png", theme);
                await evidence.ActionAsync(picker, "Close unchanged template picker", () => picker.IsDropDownOpen = false);
                Require(!picker.IsDropDownOpen && ReferenceEquals(picker.SelectedItem, selected), "Closing the picker changed its selected template.");
                await evidence.ActionAsync(Find<Button>("templates-edit"), "Open existing template editor", () => Find<Button>("templates-edit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                await Program.WaitUntilAsync(() => FindOrNull<TextBox>("RuleTemplateName")?.IsFocused == true);
                TextBox name = Find<TextBox>("RuleTemplateName"); ComboBox types = Find<ComboBox>("RuleTemplateTypesMode");
                CheckBox include = Find<CheckBox>("templates-includeTypes");
                Button cancel = Find<Button>("templates-cancel"), save = Find<Button>("templates-save");
                await ShowAsync(name); await FlushAsync();
                Require(name.Text == "Video" && name.PlaceholderText == (language == "de" ? "Vorlagenname" : "Template name")
                    && AutomationProperties.GetName(name) == (language == "de" ? "Vorlagenname" : "Template name")
                    && types.SelectedIndex == (int)MediaTypeSelectionMode.Selected && include.IsChecked == true,
                    "Actual template editor field identity/values/locale changed.");
                CheckLabel(include, language == "de" ? "Medienauswahl aufnehmen" : "Include media selection");
                CheckLabel(save, language == "de" ? "Vorlage speichern" : "Save template");
                CheckLabel(cancel, language == "de" ? "Abbrechen" : "Cancel");
                Require(new Control[] { name, types, include, save, cancel }.All(value => value.IsAttachedToVisualTree()
                    && value.IsEffectivelyVisible && value.ActualThemeVariant == theme && value.Bounds.Width > 0 && value.Bounds.Height > 0),
                    "Actual template editor controls are detached, unmeasured or use the wrong theme.");
                evidence.Capture(evidence.Task + "-editor.png");
                await ShowAsync(cancel);
                Require(cancel.Focus(NavigationMethod.Tab) && cancel.IsKeyboardFocusWithin,
                    "Visible template cancel action cannot receive keyboard focus.");
                await FlushAsync();
                ScrollViewer editorScroll = page.FindControl<ScrollViewer>("RulesScroll")!;
                object[] actionGeometry = new[] { save, cancel }.Select(button =>
                {
                    Point? at = button.TranslatePoint(default, editorScroll);
                    bool fullyVisible = at is { } point && point.X >= -1 && point.Y >= -1
                        && point.X + button.Bounds.Width <= editorScroll.Viewport.Width + 1
                        && point.Y + button.Bounds.Height <= editorScroll.Viewport.Height + 1;
                    TextBlock[] labels = button.GetVisualDescendants().OfType<TextBlock>()
                        .Where(value => value.IsEffectivelyVisible && !string.IsNullOrEmpty(value.Text)).ToArray();
                    bool labelsFit = labels.Length > 0 && labels.All(value =>
                        value.TextLayout.WidthIncludingTrailingWhitespace <= value.Bounds.Width + 1
                        && value.TextLayout.Height <= value.Bounds.Height + 1);
                    Require(fullyVisible && labelsFit, "Template editor action or its label is clipped: " + button.Name);
                    return (object)new { button.Name, bounds = button.Bounds.ToString(), viewportPosition = at?.ToString(),
                        viewport = editorScroll.Viewport.ToString(), fullyVisible, labelsFit,
                        keyboardFocusWithin = button.IsKeyboardFocusWithin, labels = labels.Select(value => value.Text).ToArray() };
                }).ToArray();
                evidence.Capture(evidence.Task + "-editor-actions.png");
                evidence.Write("editor-actions-geometry", new { controls = actionGeometry,
                    preparation = "existing BringIntoView scroll path, then keyboard-navigation focus; no draft mutation" });
                await evidence.ActionAsync(cancel, "Cancel untouched template editor", () => cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                await Program.WaitUntilAsync(() => !page.IsTemplateDecisionOpen && FindOrNull<TextBox>("RuleTemplateName") == null);
                Require(FileHash("media_rules.json") == rulesHash && FileHash("rule_templates.json") == templatesHash && Draft() == raw,
                    "Presentation-only picker/editor open/cancel changed persisted data or the source draft.");
                object cell = new { language, theme = window.ActualThemeVariant.Key?.ToString(), mode, bounds = window.Bounds.ToString(),
                    render_scaling = window.RenderScaling, expected_scale = expectedScale, picker_label = itemLabel,
                    editor_name_label = AutomationProperties.GetName(name), editor_bounds = name.Bounds.ToString(),
                    cancel_bounds = cancel.Bounds.ToString(), raw_disk_draft_unchanged = true };
                completed.Add(cell); evidence.Write("presentation-cell-pass", cell);
            }
            Require(completed.Count == 12, "The presentation matrix did not complete all twelve cells.");
            evidence.Write("completion", new { result = "pass", run_kind = "presentation-only", completed_cells = completed.Count, functional_baseline_source_digest = baseline });
        }
        catch (Exception exception) { failure = exception; evidence.Write("failure", new { error = exception.ToString(), state = State() }); throw; }
        finally
        {
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { package = "URSA-19", result = failure == null ? "pass" : "fail",
                source_digest = source, functional_baseline_source_digest = baseline, run_kind = "presentation-only; explicit persisted final fixture, no semantic/alternative replay",
                expected_scale = expectedScale, actual_scale = window.RenderScaling, completed_cells = completed, error = failure?.ToString(),
                observer_kind = "harness", human_open = true, native_at_ime_open = true }, new JsonSerializerOptions { WriteIndented = true }));
            page.EndLifetime(); host.Content = retained;
            await PresentationChecks.SwitchModeAsync(window, originalMode);
            Texts.SetLanguage(originalLanguage); Application.Current!.RequestedThemeVariant = originalTheme;
            window.Width = originalWidth; window.Height = originalHeight;
        }
        T? FindOrNull<T>(string name) where T : Control => page.GetVisualDescendants().OfType<T>().FirstOrDefault(value => value.Name == name);
        T Find<T>(string name) where T : Control => FindOrNull<T>(name) ?? throw new InvalidOperationException("Missing presentation control " + name);
        async Task ShowAsync(Control control)
        {
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout(); control.BringIntoView(); window.UpdateLayout();
                ScrollViewer scroll = page.FindControl<ScrollViewer>("RulesScroll")!;
                Point? at = control.TranslatePoint(default, scroll);
                return control.IsAttachedToVisualTree() && control.IsEffectivelyVisible && at is { } point
                    && point.Y >= -1 && point.Y + control.Bounds.Height <= scroll.Viewport.Height + 1
                    && point.X >= -1 && point.X + control.Bounds.Width <= scroll.Viewport.Width + 1;
            });
        }
        static void CheckLabel(Control control, string label) => Require(control.GetVisualDescendants().OfType<TextBlock>().Any(value => value.Text == label),
            "Template editor's actual rendered label does not match: " + label);
        void CapturePickerPopup(Control child, string file, ThemeVariant theme)
        {
            TopLevel? popupRoot = TopLevel.GetTopLevel(child);
            Control imageRoot = popupRoot != null && !ReferenceEquals(popupRoot, window) ? popupRoot : child;
            imageRoot.UpdateLayout(); double scale = popupRoot?.RenderScaling ?? window.RenderScaling;
            PixelPoint origin = imageRoot.PointToScreen(default);
            PixelRect? area = window.Screens.ScreenFromWindow(window)?.WorkingArea;
            PixelSize pixels = new(Math.Max(1, (int)Math.Ceiling(imageRoot.Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(imageRoot.Bounds.Height * scale)));
            Require(area is { } screen && origin.X >= screen.X - 1 && origin.Y >= screen.Y - 1
                && origin.X + pixels.Width <= screen.X + screen.Width + 1 && origin.Y + pixels.Height <= screen.Y + screen.Height + 1
                && Math.Abs(scale - expectedScale) < 0.01 && imageRoot.ActualThemeVariant == theme,
                "Actual picker popup has wrong theme/scale or lies outside its screen.");
            using RenderTargetBitmap bitmap = new(pixels, new Vector(96 * scale, 96 * scale));
            bitmap.Render(imageRoot); bitmap.Save(Path.Combine(output, file), PngBitmapEncoderOptions.Default);
            evidence.Write("popup-screenshot", new { path = file, bounds = imageRoot.Bounds.ToString(), screen_origin = origin.ToString(), render_scaling = scale,
                actual_theme = imageRoot.ActualThemeVariant.Key?.ToString(), method = "actual popup visual-tree render; parent window alone excludes native PopupRoot; not OS composition" });
        }
    }
}
