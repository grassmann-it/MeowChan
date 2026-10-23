using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Rules;
using Ursa.Controls;
using Semi.Avalonia;
using Ursa.Themes.Semi;

namespace MeowChan.DesktopSmoke;

// Opt-in interaction comparison. It never operates on the session's rule/template stores.
internal static partial class TemplateComparisonChecks
{
    private sealed class Evidence(string output, Window window, string source, string fixture)
    {
        private readonly List<object> _events = [];
        internal string Task = "setup", Variant = "copy";
        internal RulesPage? Page;
        internal Func<object>? State;
        internal string ExpectedLanguage = "en";
        internal ThemeVariant ExpectedTheme = ThemeVariant.Dark;
        private object Presentation() => new { expected_language = ExpectedLanguage, language = Texts.Language,
            actual_theme = window.ActualThemeVariant.Key?.ToString(), expected_theme = ExpectedTheme.Key?.ToString(),
            requested_theme = Application.Current!.RequestedThemeVariant?.Key?.ToString(),
            root = Page?.IsAttachedToVisualTree() == true ? "product RulesPage" : "fixed-English rival walkthrough",
            rendered_save_label = Page?.IsAttachedToVisualTree() == true ? Page.FindControl<Button>("SaveRuleButton")?.GetVisualDescendants().OfType<TextBlock>().Select(value => value.Text).ToArray() : null,
            actual_root_theme = Page?.IsAttachedToVisualTree() == true ? Page.ActualThemeVariant.Key?.ToString() : window.ActualThemeVariant.Key?.ToString() };
        internal void AssertPresentation()
        {
            Require(Texts.Language == ExpectedLanguage && window.ActualThemeVariant == ExpectedTheme,
                "Comparison actual language/theme differs from expected: " + JsonSerializer.Serialize(Presentation()));
            CultureInfo culture = CultureInfo.GetCultureInfo(ExpectedLanguage == "de" ? "de-DE" : "en-US");
            SemiTheme semi = Application.Current!.Styles.OfType<SemiTheme>().Single();
            UrsaSemiTheme ursa = Application.Current.Styles.OfType<UrsaSemiTheme>().Single();
            Require(Equals(semi.Locale, culture) && Equals(ursa.Locale, culture), "Vendor control locale does not match rendered-language task.");
            if (Page?.IsAttachedToVisualTree() == true)
            {
                string label = ExpectedLanguage == "de" ? "Regel speichern" : "Save rule";
                string heading = ExpectedLanguage == "de" ? "Regelvorlagen" : "Rule templates";
                Button save = Page.FindControl<Button>("SaveRuleButton")!;
                Require(Page.ActualThemeVariant == ExpectedTheme && AutomationProperties.GetName(save) == label
                    && save.GetVisualDescendants().OfType<TextBlock>().Any(value => value.Text == label)
                    && Page.GetVisualDescendants().OfType<TextBlock>().Any(value => value.IsEffectivelyVisible && value.Text == heading),
                    "Comparison rendered product labels/theme differ from expected: " + JsonSerializer.Serialize(Presentation()));
            }
            else Require(ExpectedLanguage == "en" && window.GetVisualDescendants().OfType<TextBlock>().Any(value => value.IsEffectivelyVisible
                && value.Text?.StartsWith("URSA-19 limited ", StringComparison.Ordinal) == true), "Rival walkthrough's fixed English labels are not the current rendered surface.");
        }
        internal void Write(string kind, object data)
        {
            _events.Add(new { sequence = _events.Count, package = "URSA-19", task = Task, variant = Variant, kind,
                observer_kind = "harness", human_open = true, input_method = "in-process control events/properties; not OSinput",
                source_digest = source, fixture_digest = fixture, language = Texts.Language, theme = window.ActualThemeVariant.Key?.ToString(), presentation = Presentation(),
                window = window.Bounds.ToString(), render_scaling = window.RenderScaling, data });
            File.WriteAllText(Path.Combine(output, "events.json"), JsonSerializer.Serialize(_events, new JsonSerializerOptions { WriteIndented = true }));
        }
        internal void Predict(string task, string variant, object expected) { AssertPresentation(); Task = task; Variant = variant; Write("prediction-before-action", expected); }
        internal async Task ActionAsync(Control control, string operation, Action action)
        {
            AssertPresentation();
            control.BringIntoView(); window.UpdateLayout();
            try
            {
                await Program.WaitUntilAsync(() => control.IsAttachedToVisualTree() && control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control.Bounds.Width > 0 && control.Bounds.Height > 0);
            }
            catch (TimeoutException exception)
            {
                object state = new { control = control.Name, attached = control.IsAttachedToVisualTree(), visible = control.IsEffectivelyVisible,
                    enabled = control.IsEffectivelyEnabled, bounds = control.Bounds.ToString(), state = State?.Invoke(),
                    ancestors = control.GetVisualAncestors().OfType<Control>().Select(value => new { type = value.GetType().Name, value.Name,
                        visible = value.IsEffectivelyVisible, enabled = value.IsEffectivelyEnabled, bounds = value.Bounds.ToString(),
                        scroll = value is ScrollViewer scroll ? new { offset = scroll.Offset.ToString(), viewport = scroll.Viewport.ToString(), extent = scroll.Extent.ToString() } : null }).ToArray() };
                Write("unreachable-control", state); Capture("unreachable-" + (control.Name ?? "control") + ".png");
                throw new TimeoutException("Control readiness failed without performing its action: " + JsonSerializer.Serialize(state), exception);
            }
            string? before = Focus(); control.Focus(NavigationMethod.Tab); action();
            Write("actual-action", new { control = control.Name ?? control.GetType().Name, operation,
                accessible_name = AutomationProperties.GetName(control), focus_before = before, focus_after = Focus(),
                scroll_preparation = "programmatic BringIntoView; not a counted user scroll" });
            await FlushAsync();
        }
        private string? Focus() => window.FocusManager?.GetFocusedElement() is Control control ? control.Name ?? control.GetType().Name : null;
        internal void Capture(string name)
        {
            window.UpdateLayout(); AssertPresentation(); double scale = window.RenderScaling;
            using RenderTargetBitmap bitmap = new(new PixelSize(Math.Max(1, (int)Math.Ceiling(window.Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(window.Bounds.Height * scale))), new Vector(96 * scale, 96 * scale));
            bitmap.Render(window); bitmap.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default);
            Write("screenshot", new { path = name });
        }
    }
    internal static async Task RunAsync(MainWindow window)
    {
        if (Environment.GetEnvironmentVariable("MEOWCHAN_TEMPLATE_COMPARISON_PRESENTATION_ONLY") == "1")
        {
            await RunPresentationOnlyAsync(window);
            return;
        }
        string output = Environment.GetEnvironmentVariable("MEOWCHAN_TEMPLATE_COMPARISON_OUTPUT") ?? throw new InvalidOperationException("Set fresh absolute MEOWCHAN_TEMPLATE_COMPARISON_OUTPUT.");
        string source = Environment.GetEnvironmentVariable("MEOWCHAN_TEMPLATE_COMPARISON_SOURCE_DIGEST") ?? throw new InvalidOperationException("Set executed snapshot MEOWCHAN_TEMPLATE_COMPARISON_SOURCE_DIGEST.");
        Require(Path.IsPathFullyQualified(output) && !Directory.Exists(output), "Comparison output must be fresh and absolute.");
        Require(source.Length == 64 && source.All(Uri.IsHexDigit), "Comparison source digest must be the executed snapshot's full SHA256.");
        Directory.CreateDirectory(output);
        string profile = Path.Combine(Config.GetConfigDirectory(), "template-comparison-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(profile);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        MediaRuleStore rules = new(profile, lease); await rules.LoadAsync();
        RuleTemplateStore templates = new(profile, lease); await templates.LoadAsync();
        MediaRuleScope[] scopes = [MediaRuleScope.ForBoard("fixture", "a"), MediaRuleScope.ForBoard("fixture", "b"), MediaRuleScope.ForBoard("fixture", "c")];
        await rules.SaveAsync(scopes.Select((scope, i) => new MediaRule(scope, i == 2,
            new(MediaTypeSelectionMode.Selected, new[] { new[] { "png", "jpg", "gif" }[i] }), new(MediaSizeLimitMode.Limited, (i + 1) * 1_000_000))
            { ImageFilter = i == 2 ? new(MediaImageFilterMode.Plugin, "fixture.missing", "schema-filter", "2", new Dictionary<string, string> { ["threshold"] = "7" }) : MediaImageFilterRule.Inherit }), rules.Current.Revision);
        await templates.SaveAsync(Array.Empty<RuleTemplate>(), templates.Current.Revision);
        object fixture = new { boards = TemplateRivalWalkthrough.Initial, raw_a = "png tag + image/jpeg rest", video = "mp4/webm", wallpaper = "jpg/png", incompatible = "fixture.missing/schema-filter@2 threshold=7" };
        string fixtureJson = JsonSerializer.Serialize(fixture); string fixtureHash = Hash(Encoding.UTF8.GetBytes(fixtureJson));
        File.WriteAllText(Path.Combine(output, "fixture.json"), fixtureJson);
        Evidence evidence = new(output, window, source, fixtureHash); Exception? failure = null;
        string language = Texts.Language; ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        string mode = window.SessionHost.Config.WorkspaceMode; double width = window.Width, height = window.Height;
        window.FindControl<MenuItem>("RulesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ContentControl host = window.FindControl<ContentControl>("RulesPageHost")!; object? retained = host.Content;
        RulesPage page = new(() => rules.Current, (values, revision) => rules.SaveAsync(values, revision));
        page.AttachTemplateServices(() => templates.LoadAsync(), (values, revision) => templates.SaveAsync(values, revision)); host.Content = page;
        object Snapshot() => new { rules_hash = FileHash("media_rules.json"), templates_hash = FileHash("rule_templates.json"), rules = rules.Current.Rules.Select(rule => new {
            board = rule.Scope.Board, rule.Enabled, types = rule.Types.Extensions, size = rule.SizeLimit.Bytes, filter = rule.ImageFilter.Mode.ToString(), schema = rule.ImageFilter.SchemaVersion, parameters = rule.ImageFilter.Parameters }),
            draft = new { scope = page.ActiveScope.Board, raw = page.ExtensionsText, size = page.MegabytesText, enabled = page.RuleEnabled, validation = page.HasValidationError } };
        string FileHash(string file) => Hash(File.ReadAllBytes(Path.Combine(profile, file)));
        evidence.Page = page; evidence.State = Snapshot;
        try
        {
            await PresentationChecks.SwitchModeAsync(window, "Advanced"); window.Width = 1100; window.Height = 850;
            Texts.SetLanguage("en"); Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            page.IsAvailable = true; page.EditScope(scopes[0]); await Program.WaitUntilAsync(() => page.CanExecuteTemplateCommand("rules.template-create"));
            await VerifyPresentationAsync("en", ThemeVariant.Dark);
            // Explicit fixture preparation, not counted as successful disabled-rule keyboard entry.
            RuleExtensionsInput original = page.FindControl<RuleExtensionsInput>("RuleExtensionsInput")!;
            page.ExtensionsText = "image/jpeg"; // Fixture setup marks the draft dirty through its normal notification path.
            original.State.Tags.Add(".png"); original.Bind(original.State);
            string originalRaw = page.ExtensionsText; string originalRuleHash = FileHash("media_rules.json");
            evidence.Write("fixture-preparation", new { fixture_digest = fixtureHash, state = Snapshot(), original_rule_hash = originalRuleHash, original_template_hash = FileHash("rule_templates.json") });

            evidence.Predict("size-only-from-invalid-a", "copy", new { selected_group = "size", omitted_invalid_raw = originalRaw, rule_write = false, size_bytes = 1_000_000 });
            await CreateAsync("Size only", size: true);
            Require(templates.Current.Templates.Single().Types == null && templates.Current.Templates.Single().SizeLimit!.Bytes == 1_000_000 && FileHash("media_rules.json") == originalRuleHash && page.ExtensionsText == originalRaw,
                "Size-only template copied an omitted invalid group, lost raw input, or wrote a rule.");
            evidence.Write("observed-result", Snapshot());

            evidence.Predict("create-user-video", "copy", new { extensions = new[] { ".mp4", ".webm" }, omitted = "size/filter/enabled", rule_write = false });
            await CreateAsync("Video", types: "mp4,webm");
            Require(FileHash("media_rules.json") == originalRuleHash, "Creating Video wrote active rules.");
            for (int i = 0; i < 3; i++)
            {
                await OpenScopeAsync(i);
                string beforeHash = FileHash("media_rules.json"); string rawBefore = page.ExtensionsText;
                evidence.Predict("video-target-" + i, "copy", new { board = scopes[i].Board, extensions = new[] { ".mp4", ".webm" }, size = (i + 1) * 1_000_000, enabled = i == 2, filter_unchanged = true, persisted_before_save = beforeHash });
                await ApplyAsync("Video");
                Require(FileHash("media_rules.json") == beforeHash && page.MegabytesText == (i + 1).ToString() && page.RuleEnabled == (i == 2), "Preview/Apply wrote early or changed omitted size/enabled.");
                if (i == 0)
                {
                    await ClickAsync("templates-undo-apply"); Require(page.ExtensionsText == rawBefore && rawBefore == originalRaw, "Immediate Undo failed to restore A's exact invalid rest.");
                    evidence.Write("undo-restored-raw", Snapshot()); await ApplyAsync("Video");
                }
                await SaveRuleAsync();
                MediaRule actual = rules.Current.Rules.Single(rule => rule.Scope == scopes[i]);
                Require(actual.Types.Extensions.SequenceEqual(new[] { ".mp4", ".webm" }) && actual.SizeLimit.Bytes == (i + 1) * 1_000_000 && actual.Enabled == (i == 2), "Video saved wrong target values.");
                if (i == 2) Require(actual.ImageFilter.Parameters["threshold"] == "7" && actual.ImageFilter.SchemaVersion == "2", "Omitted incompatible filter was altered.");
                evidence.Write("observed-result", Snapshot());
            }
            evidence.Capture("copy-three-boards.png");
            await RunRivalAsync("bulk");
            await RunRivalAsync("link");
            host.Content = page; await FlushAsync();
            await OpenScopeAsync(0); await CreateAsync("Wallpaper", types: "jpg,png");
            for (int i = 0; i < 2; i++) { await OpenScopeAsync(i); evidence.Predict("wallpaper-target-" + i, "copy", new { target = scopes[i].Board, types = "jpg/png", other_groups_unchanged = true }); await ApplyAsync("Wallpaper"); await SaveRuleAsync(); }
            string copiedRules = FileHash("media_rules.json");
            evidence.Predict("catalog-change-after-copy", "copy", new { changed_boards = Array.Empty<string>(), catalog_types = "webp", stored_rules_hash = copiedRules });
            await SelectTemplateAsync("Wallpaper"); await ClickAsync("templates-edit"); await SetTypesAsync("webp"); await ClickAsync("templates-save");
            await Program.WaitUntilAsync(() => !page.IsBusy && NamedOrNull<TextBox>("RuleTemplateName") == null);
            Require(FileHash("media_rules.json") == copiedRules && rules.Current.Rules.Where(rule => rule.Scope == scopes[0] || rule.Scope == scopes[1]).All(rule => rule.Types.Extensions.SequenceEqual(new[] { ".jpg", ".png" })), "Catalog edit changed independently copied rules.");
            evidence.Write("observed-result", Snapshot());
            await RunRivalAsync("preset"); host.Content = page; await FlushAsync();
            evidence.Predict("private-custom-values", "copy", new { extensions = "webp/avif", preset_fixed_values_insufficient = true, rule_write = false });
            await CreateAsync("Private values", types: "webp,avif");
            Require(templates.Current.Templates.Single(template => template.Name == "Private values").Types!.Extensions.SequenceEqual(new[] { ".avif", ".webp" }) && FileHash("media_rules.json") == copiedRules, "User-owned custom template failed or wrote rules.");
            evidence.Write("observed-result", Snapshot());

            await OpenScopeAsync(2); string beforeIncompatible = FileHash("rule_templates.json"); string beforeRules = FileHash("media_rules.json"); string beforeRaw = page.ExtensionsText;
            evidence.Predict("incompatible-included-filter", "copy", new { blocked = true, no_writes = true, raw = beforeRaw });
            await ClickAsync("templates-create"); await EditAsync(Named<TextBox>("RuleTemplateName"), "Incompatible filter");
            await SetCheckAsync(Named<CheckBox>("templates-includeFilter"), true); await ClickAsync("templates-save");
            await Program.WaitUntilAsync(() => NamedOrNull<TextBlock>("RuleTemplateEditorError")?.IsVisible == true);
            Require(FileHash("rule_templates.json") == beforeIncompatible && FileHash("media_rules.json") == beforeRules && page.ExtensionsText == beforeRaw, "Incompatible filter attempt wrote or lost a draft.");
            evidence.Write("observed-blocked-result", new { state = Snapshot(), message = Named<TextBlock>("RuleTemplateEditorError").Text });
            evidence.Capture("copy-incompatible-filter.png");
            await ClickAsync("templates-cancel");
            PopConfirm confirmation = page.GetVisualDescendants().OfType<PopConfirm>().Single(value => value.IsDropdownOpen);
            await evidence.ActionAsync(confirmation, "confirm explicit template discard", () => confirmation.ConfirmCommand!.Execute(null));

            foreach ((string presentation, double w, double h) in new[] { ("Barebones", 640d, 480d), ("Simple", 760d, 500d), ("Advanced", 760d, 500d) })
            {
                await PresentationChecks.SwitchModeAsync(window, presentation); window.Width = w; window.Height = h;
                // Switching the real workspace mode reapplies saved settings. Apply and
                // verify language/theme afterwards; requested labels alone are not evidence.
                Texts.SetLanguage("de"); Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                await VerifyPresentationAsync("de", ThemeVariant.Light);
                evidence.Predict("control-probe-" + presentation, "copy", new { intended = "same Video preview, cancel preserves raw/store", source = "DE Light 1x control probe; native AT/IME and other scales open" });
                await SelectTemplateAsync("Video"); ComboBox list = Named<ComboBox>("RuleTemplateList");
                await evidence.ActionAsync(list, "KeyDown End on actual template ComboBox", () => list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.End }));
                await SelectTemplateAsync("Video"); await ClickAsync("templates-preview");
                await Program.WaitUntilAsync(() => NamedOrNull<TextBlock>("RuleTemplatePreviewHeading")?.IsFocused == true);
                evidence.Capture("copy-controls-" + presentation + ".png"); await ClickAsync("templates-cancel");
                Require(FileHash("media_rules.json") == beforeRules && page.ExtensionsText == beforeRaw, "Responsive preview/cancel changed values.");
                evidence.Write("observed-result", Snapshot());
            }
            await CheckExternalRevisionAsync();
            evidence.Write("completion", new { result = "pass", interpretation = "retain-provisionally", human_open = true, native_at_ime_open = true, performance_or_preference_claim = false });
        }
        catch (Exception exception) { failure = exception; evidence.Write("failure", new { error = exception.ToString(), state = Snapshot() }); throw; }
        finally
        {
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { package = "URSA-19", result = failure == null ? "pass" : "fail", source_digest = source, fixture_digest = fixtureHash, implementation_basis_digest = "abdfdb7818e1b2b9edf6d4a3c2ffbeff0cf3800fb34168be58acf257df0931bd",
                observer_kind = "harness", human_open = true, native_at_ime_open = true, interpretation = failure == null ? "retain-provisionally" : "adjust", input_method = "in-process control events/properties; not OSinput",
                os = Environment.OSVersion.ToString(), backend = Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1" ? "X11 software smoke" : "platform-detected", render_scaling = window.RenderScaling,
                error = failure?.ToString(), scope = "bounded interaction evidence only; not native accessibility, timing preference, or human judgment" }, new JsonSerializerOptions { WriteIndented = true }));
            page.EndLifetime(); host.Content = retained; Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme;
            await PresentationChecks.SwitchModeAsync(window, mode); window.Width = width; window.Height = height;
        }
        T? NamedOrNull<T>(string name) where T : Control => page.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);
        T Named<T>(string name) where T : Control => NamedOrNull<T>(name) ?? throw new InvalidOperationException("Missing control " + name);
        Task ClickAsync(string name) => evidence.ActionAsync(Named<Button>(name), "Click", () => Named<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        Task EditAsync(TextBox input, string text) => evidence.ActionAsync(input, "Text=" + text, () => input.Text = text);
        Task SetCheckAsync(CheckBox input, bool value) => evidence.ActionAsync(input, "IsChecked=" + value, () => input.IsChecked = value);
        async Task SetTypesAsync(string value)
        {
            ComboBox types = Named<ComboBox>("RuleTemplateTypesMode"); await evidence.ActionAsync(types, "SelectedIndex=Selected", () => types.SelectedIndex = (int)MediaTypeSelectionMode.Selected);
            RuleExtensionsInput tags = Named<RuleExtensionsInput>("RuleTemplateExtensions"); TextBox? text = null;
            await Program.WaitUntilAsync(() => { window.UpdateLayout(); text = tags.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(); return text?.IsAttachedToVisualTree() == true; });
            // Existing copied tags must be removed through their actual controls before entering the new choice.
            foreach (ClosableTag tag in tags.GetVisualDescendants().OfType<ClosableTag>().ToArray())
                await evidence.ActionAsync(tag, "KeyDown Delete", () => tag.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete }));
            await EditAsync(text!, value);
            await evidence.ActionAsync(text!, "KeyDown Enter commits tags", () => text!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }));
        }
        async Task CreateAsync(string name, bool size = false, string? types = null)
        {
            string beforeRaw = page.ExtensionsText, beforeRules = FileHash("media_rules.json");
            bool beforeValidation = page.HasValidationError;
            RuleExtensionsInput ruleTags = page.FindControl<RuleExtensionsInput>("RuleExtensionsInput")!;
            TextBox? ruleEditor = ruleTags.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            evidence.Write("before-template-create-input", new { template = name, fixture_setup = true,
                model_rest = ruleTags.State.Rest, model_tags = ruleTags.State.Tags.ToArray(), raw = beforeRaw,
                editor_text = ruleEditor?.Text, editor_attached = ruleEditor?.IsAttachedToVisualTree(),
                editor_enabled = ruleEditor?.IsEffectivelyEnabled, validation = beforeValidation });
            await ClickAsync("templates-create");
            Require(page.ExtensionsText == beforeRaw && page.HasValidationError == beforeValidation && FileHash("media_rules.json") == beforeRules,
                "Opening template creation changed the source rule's omitted raw group/validation or wrote a rule: " + JsonSerializer.Serialize(Snapshot()));
            await EditAsync(Named<TextBox>("RuleTemplateName"), name);
            if (size) await SetCheckAsync(Named<CheckBox>("templates-includeSize"), true);
            if (types != null) { await SetCheckAsync(Named<CheckBox>("templates-includeTypes"), true); await SetTypesAsync(types); }
            await ClickAsync("templates-save"); await Program.WaitUntilAsync(() => !page.IsBusy && NamedOrNull<TextBox>("RuleTemplateName") == null);
        }
        async Task SelectTemplateAsync(string name)
        {
            ComboBox list = Named<ComboBox>("RuleTemplateList"); object item = list.Items.Cast<object>().Single(value => value.ToString()!.StartsWith(name + " ·", StringComparison.Ordinal));
            await evidence.ActionAsync(list, "Select template " + name, () => list.SelectedItem = item);
        }
        async Task OpenScopeAsync(int index)
        {
            ComboBox list = page.FindControl<ComboBox>("RulesScopeList")!; RuleScopeChoice choice = page.ScopeChoices.Single(value => value.Scope == scopes[index]);
            await evidence.ActionAsync(list, "Select scope " + scopes[index].Board, () => list.SelectedItem = choice); await ClickAsync("OpenSavedRuleButton");
        }
        async Task ApplyAsync(string name)
        {
            await SelectTemplateAsync(name); await ClickAsync("templates-preview");
            await Program.WaitUntilAsync(() => NamedOrNull<TextBlock>("RuleTemplatePreviewHeading")?.IsFocused == true);
            // Meaningful identity regression: refresh the exact same immutable rules
            // revision before applying, including the initially clean b/c targets.
            page.RefreshConfirmedRules();
            Require(page.CanExecuteTemplateCommand("rules.template-apply"), "Same-revision refresh invalidated a template's clean target identity.");
            evidence.Write("same-revision-preview-retained", new { revision = rules.Current.Revision, scope = page.ActiveScope.Board, can_apply = true });
            evidence.Write("preview-read", new { heading = Named<TextBlock>("RuleTemplatePreviewHeading").Text, before = Snapshot(), visible_text = page.GetVisualDescendants().OfType<TextBlock>().Where(value => value.IsEffectivelyVisible).Select(value => value.Text).ToArray() });
            await ClickAsync("templates-apply"); await Program.WaitUntilAsync(() => !page.IsBusy && NamedOrNull<TextBlock>("RuleTemplatePreviewHeading") == null);
            RuleTemplate applied = templates.Current.Templates.Single(value => value.Name == name);
            Require(applied.Types == null || page.ExtensionsText == string.Join(", ", applied.Types.Extensions),
                "Template Apply closed its preview without copying the selected type group: " + JsonSerializer.Serialize(Snapshot()));
            Require(page.CanSave, "Template Apply did not produce a saveable draft: " + JsonSerializer.Serialize(Snapshot()));
        }
        async Task VerifyPresentationAsync(string expectedLanguage, ThemeVariant expectedTheme)
        {
            evidence.ExpectedLanguage = expectedLanguage; evidence.ExpectedTheme = expectedTheme;
            await Program.WaitUntilAsync(() => Texts.Language == expectedLanguage && window.ActualThemeVariant == expectedTheme && page.ActualThemeVariant == expectedTheme);
            await FlushAsync(); window.UpdateLayout(); evidence.AssertPresentation();
            evidence.Write("verified-presentation-setup", new { fixture_setup = true, config_mode = window.SessionHost.Config.WorkspaceMode,
                expected_language = expectedLanguage, actual_theme = window.ActualThemeVariant.Key?.ToString(), labels_checked = "literal Save rule/Regel speichern and Rule templates/Regelvorlagen plus Semi/Ursa locale" });
        }
        async Task CheckExternalRevisionAsync()
        {
            await OpenScopeAsync(1); await SelectTemplateAsync("Video"); await ClickAsync("templates-preview");
            await Program.WaitUntilAsync(() => NamedOrNull<TextBlock>("RuleTemplatePreviewHeading")?.IsFocused == true);
            evidence.Predict("external-revision-invalidates-preview", "copy", new { apply_blocked = true, no_draft_or_extra_write = true });
            string raw = page.ExtensionsText;
            // Deliberate external revision setup, not an operation exposed as a product action.
            await rules.SaveAsync(rules.Current.Rules, rules.Current.Revision);
            string changedHash = FileHash("media_rules.json"); page.RefreshConfirmedRules();
            Require(!page.CanExecuteTemplateCommand("rules.template-apply"), "External revision failed to invalidate the old preview.");
            evidence.Write("external-revision-setup-and-guard", new { fixture_setup = true, changed_revision = rules.Current.Revision, raw_before = raw, state = Snapshot(), apply_blocked = true });
            await ClickAsync("templates-cancel");
            Require(FileHash("media_rules.json") == changedHash && page.ExtensionsText == raw, "Cancelling stale preview changed its draft or added a write.");
        }
        async Task SaveRuleAsync()
        {
            string revision = rules.Current.Revision; await ClickAsync("SaveRuleButton"); await Program.WaitUntilAsync(() => !page.IsBusy && (page.HasError || rules.Current.Revision != revision));
            Require(!page.HasError && rules.Current.Revision != revision, "Explicit rule save failed: " + page.ErrorMessage);
        }
        async Task RunRivalAsync(string variant)
        {
            // Bulk repeats the earlier Video task from its frozen initial fixture. Link and
            // Preset start from the exact stored values before their corresponding copy tasks.
            var start = variant == "bulk" ? TemplateRivalWalkthrough.Initial : scopes.Select(scope =>
            {
                MediaRule rule = rules.Current.Rules.Single(value => value.Scope == scope);
                return new TemplateRivalWalkthrough.Board(scope.Board!, string.Join("/", rule.Types.Extensions.Select(value => value.TrimStart('.'))),
                    (int)(rule.SizeLimit.Bytes!.Value / 1_000_000), rule.Enabled, scope == scopes[2] ? "incompatible-v2" : "compatible");
            }).ToArray();
            TemplateRivalWalkthrough rival = new(variant, start); host.Content = new ScrollViewer { Content = rival }; await FlushAsync();
            object[] before = rival.State.Cast<object>().ToArray();
            evidence.Predict(variant + "-targets", variant, new { starting_state = before, intent = variant == "preset" ? "private webp + threshold7 cannot be represented" : variant == "bulk" ? "a,b succeed, c unchanged conflict" : "bind a,b then exclude b; update changes a only" });
            await EditAsync(rival.Prediction, variant == "preset" ? "unsupported, all unchanged" : variant == "bulk" ? "a,b video; c conflict unchanged" : "a,b wallpaper; later only a changes if b excluded");
            await SetCheckAsync(rival.A, true); await SetCheckAsync(rival.B, true);
            if (variant == "bulk") await SetCheckAsync(rival.C, true);
            await evidence.ActionAsync(rival.Choice, "Select " + (variant == "preset" ? "custom" : variant == "bulk" ? "Video" : "Wallpaper"), () => rival.Choice.SelectedIndex = variant == "preset" ? 2 : variant == "bulk" ? 1 : 0);
            await evidence.ActionAsync(rival.Cancel, "Click Cancel", () => rival.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Require(rival.State.SequenceEqual(rival.InitialState), "Rival Cancel changed initial boards.");
            evidence.Write("target-preview-before-confirm", new { prediction = rival.Prediction.Text, preview = rival.PreviewRows.Text, before = rival.State });
            await evidence.ActionAsync(rival.Confirm, "Click Confirm targets", () => rival.Confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            if (variant == "link")
            {
                evidence.Predict("linked-template-change-with-exclusion", variant, new { changed_boards = new[] { "a" }, b = "jpg/png", c = rival.InitialState[2].Types, human_prediction = false });
                await EditAsync(rival.Prediction, "a changes to webp; b excluded and unchanged; c unchanged"); await SetCheckAsync(rival.ExcludeB, true);
                await evidence.ActionAsync(rival.Change, "Click change linked template", () => rival.Change.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                Require(rival.State[0].Types == "webp" && rival.State[1].Types == "jpg/png" && rival.State[2] == rival.InitialState[2], "Linked fixture changed wrong targets.");
            }
            if (variant == "bulk") Require(rival.State[0].Types == "mp4/webm" && rival.State[1].Types == "mp4/webm" && rival.State[2] == rival.InitialState[2], "Bulk conflict did not preserve c.");
            if (variant == "preset") Require(rival.Outcome == "unsupported-by-design" && rival.State.SequenceEqual(rival.InitialState), "Fixed preset silently gained a custom editor or changed targets.");
            evidence.Write("walkthrough-observed-result", new { result = rival.Outcome, before, after = rival.State, displayed = rival.Rows.Text, explanation = rival.Result.Text, persisted = "none: disposable interactive state only", human_open = true });
            evidence.Capture("rival-" + variant + ".png");
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static async Task FlushAsync() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
