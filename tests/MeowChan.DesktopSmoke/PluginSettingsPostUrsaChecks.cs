using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;
using MeowChan.Rules;

namespace MeowChan.DesktopSmoke;

// Focused new questions only; does not replay the Full27/Full44 matrices.
internal static class PluginSettingsPostUrsaChecks
{
    internal static async Task RunAsync(MainWindow window, PluginsPage page, PluginManagementServices services,
        PluginProfileStore profile, Func<string, Task> advanceSchema)
    {
        List<string> failures = new();
        await ProbeAsync("slash-scopes", ScopeErrorsAsync);
        await ProbeAsync("long-help", LongHelpAsync);
        await ProbeAsync("schema-transitions", SchemaTransitionsAsync);
        Console.WriteLine("FORM POST URSA RESULT " + JsonSerializer.Serialize(new { failures, passed = failures.Count == 0 }));
        if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));

        async Task ProbeAsync(string name, Func<Task> run)
        {
            try { await run(); }
            catch (Exception exception)
            {
                failures.Add(name + ": " + exception.Message);
                Program.CaptureWindow(window, "smoke-form-review-" + name + "-failure.png");
                Console.WriteLine("FORM POST URSA FAILURE " + JsonSerializer.Serialize(new { name, exception = exception.ToString() }));
            }
        }

        async Task ScopeErrorsAsync()
        {
            await FlushAsync(); // Select builds logical controls; Expander templates materialize on layout.
            window.UpdateLayout();
            PluginSettingsEditor Editor(SettingScope scope) => page.GetVisualDescendants().OfType<PluginSettingsEditor>().Single(editor => editor.Draft.Scope == scope);
            TextBox slash = (TextBox)Editor(SettingScope.Plugin).FindField("fixture-source/label")!;
            TextBox source = (TextBox)Editor(SettingScope.Source).FindField("label")!;
            CheckBox set = slash.GetLogicalAncestors().OfType<StackPanel>().First().Children.OfType<CheckBox>().Single();
            long revision = (await profile.ReadAsync()).Revision;
            set.IsChecked = true;
            slash.Text = "-";
            page.FindControl<Button>("PluginSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await FlushAsync();
            string localMessage = Texts.T("plugins.integerError");
            bool localPlugin = HasError(Editor(SettingScope.Plugin), localMessage);
            bool localSource = HasError(Editor(SettingScope.Source), localMessage);
            bool localFocus = ReferenceEquals(window.FocusManager?.GetFocusedElement(), slash);
            Emit("slash-local", new { localPlugin, localSource, localFocus, pluginIdentity = AutomationProperties.GetAutomationId(slash), sourceIdentity = AutomationProperties.GetAutomationId(source), raw = slash.Text, unchanged = (await profile.ReadAsync()).Revision == revision });
            Program.CaptureWindow(window, "smoke-form-review-slash-local.png");
            // Continue to exercise the independent worker boundary even when local routing failed.
            slash.Text = "9";
            source.Text = "rejected";
            Editor(SettingScope.Source).GetLogicalAncestors().OfType<Expander>().First().IsExpanded = false;
            page.FindControl<Button>("PluginSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !page.IsBusy);
            await FlushAsync();
            bool workerPlugin = HasError(Editor(SettingScope.Plugin), "Fixture rejects slashed count nine");
            bool pluginHasSourceError = HasError(Editor(SettingScope.Plugin), "Fixture rejects this label");
            bool workerSource = HasError(Editor(SettingScope.Source), "Fixture rejects this label");
            bool sourceHasPluginError = HasError(Editor(SettingScope.Source), "Fixture rejects slashed count nine");
            bool workerFocus = ReferenceEquals(window.FocusManager?.GetFocusedElement(), slash);
            bool unchanged = (await profile.ReadAsync()).Revision == revision;
            Emit("slash-worker", new { workerPlugin, pluginHasSourceError, workerSource, sourceHasPluginError, workerFocus, unchanged, pluginRaw = slash.Text, sourceRaw = source.Text });
            Program.CaptureWindow(window, "smoke-form-review-slash-worker.png");
            source.Text = "source fixture";
            set.IsChecked = false;
            Require(localPlugin && !localSource && localFocus && workerPlugin && !pluginHasSourceError && workerSource && !sourceHasPluginError && workerFocus && unchanged,
                "Exact slash-key scope/error/focus mapping failed (see slash-local/worker JSON).");
        }

        async Task LongHelpAsync()
        {
            ContentControl host = window.FindControl<ContentControl>("PluginsPageHost")!;
            object? retained = host.Content;
            try
            {
                string help = string.Join("\n", Enumerable.Range(1, 110).Select(index => "Help line " + index + ": <literal> & text."));
                help = help.PadRight(4096, 'x')[..4096];
                SettingsSchema schema = new("review", new[] { new SettingDefinition("number", SettingScope.Plugin, SettingKind.Integer,
                    "Long label " + new string('L', 245), help, IsRequired: true) });
                PluginSettingsDraft draft = new(schema, SettingScope.Plugin, null);
                draft.SetInitialText("number", "9223372036854775808");
                PluginSettingsEditor editor = new(draft, "long-help");
                TextBox field = (TextBox)editor.FindField("number")!;
                Expander expander = new() { Header = "Long-help review", Content = editor, IsExpanded = false };
                ScrollViewer scroll = new() { Content = expander, Height = 180, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
                host.Content = scroll;
                _ = draft.Snapshot(out IReadOnlyList<FieldValidationError> errors);
                editor.ShowErrors(errors);
                Require(editor.TryFocusFirstError(), "Long-help SDK error could not be targeted.");
                await FlushAsync();
                window.UpdateLayout();
                TextBlock error = editor.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("error"));
                TextBlock helpControl = editor.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == help);
                bool focused = ReferenceEquals(window.FocusManager?.GetFocusedElement(), field);
                bool fieldVisible = VisibleInScroll(field, scroll);
                bool errorVisible = VisibleInScroll(error, scroll);
                Emit("long-help-error", new { focused, fieldVisible, errorVisible, helpLength = help.Length, labelLength = schema.Fields[0].Label.Length,
                    field = Bounds(field, scroll), error = Bounds(error, scroll), help = Bounds(helpControl, scroll), viewport = scroll.Viewport.ToString(), offset = scroll.Offset.ToString(), raw = field.Text, dirty = draft.Dirty });
                Program.CaptureWindow(window, "smoke-form-review-long-help-error.png");
                // Separately establish that the complete help can be scrolled to its end.
                helpControl.BringIntoView(new Rect(0, Math.Max(0, helpControl.Bounds.Height - 16), helpControl.Bounds.Width, Math.Min(16, helpControl.Bounds.Height)));
                await FlushAsync();
                Point? end = helpControl.TranslatePoint(new Point(0, helpControl.Bounds.Height), scroll);
                bool helpEndReachable = end.HasValue && end.Value.Y >= 0 && end.Value.Y <= scroll.Bounds.Height + 1;
                Emit("long-help-tail", new { helpEndReachable, end = end?.ToString(), offset = scroll.Offset.ToString(), retained = draft.Text("number") == "9223372036854775808" && !draft.Dirty });
                Program.CaptureWindow(window, "smoke-form-review-long-help-tail.png");
                Require(focused && fieldVisible && errorVisible && helpEndReachable && !draft.Dirty, "Long help separates focused input and its local SDK error, or is unreachable.");
            }
            finally { host.Content = retained; }
        }

        async Task SchemaTransitionsAsync()
        {
            window.FindControl<MenuItem>("RulesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ContentControl host = window.FindControl<ContentControl>("RulesPageHost")!;
            object? retained = host.Content;
            string language = Texts.Language;
            MediaRuleSet stored = new("review-initial", new[] { new MediaRule(MediaRuleScope.Global)
            { ImageFilter = new(MediaImageFilterMode.Plugin, "fixture.management", "fixture-filter", "2", new Dictionary<string, string> { ["rule-label"] = "allowed", ["rule-count"] = "2" }) } });
            int writes = 0;
            RulesPage rules = new(() => stored, (values, revision) =>
            {
                Require(revision == stored.Revision, "Schema review lost its revision.");
                writes++; stored = new(Guid.NewGuid().ToString("N"), values); return Task.FromResult(stored);
            });
            rules.AttachPluginServices(services); host.Content = rules;
            List<string> transitionFailures = new();
            try
            {
                rules.IsAvailable = true; rules.EditScope(MediaRuleScope.Global); await rules.RefreshPluginFiltersAsync();
                foreach ((string version, string raw) in new[] { ("3", "-"), ("4", "9223372036854775808") })
                {
                    TextBox Count() => (TextBox)Editor().FindField("rule-count")!;
                    PluginSettingsEditor Editor() => rules.GetVisualDescendants().OfType<PluginSettingsEditor>().Single(editor => editor.Draft.Scope == SettingScope.FilterRule);
                    Count().Text = raw;
                    foreach (string next in new[] { "de", "en", "de", "en" }) { Texts.SetLanguage(next); await FlushAsync(); Require(Count().Text == raw, "Language rebuild lost invalid raw input."); }
                    await advanceSchema(version);
                    await rules.RefreshPluginFiltersAsync(); await FlushAsync();
                    bool retainedRaw = Count().Text == raw;
                    string editorVersion = Editor().Draft.Schema.Version;
                    bool currentLabel = Editor().Draft.Schema.Fields.Single(field => field.Key == "rule-count").Label == "Rule count revision " + version;
                    Button? acknowledge = rules.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => Equals(button.Content, Texts.T("plugins.acceptSchema")));
                    int writesBefore = writes;
                    acknowledge?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await rules.SaveCurrentAsync(); await FlushAsync();
                    bool invalidBlocked = writes == writesBefore && Count().Text == raw;
                    Count().Text = "2";
                    await rules.SaveCurrentAsync(); await FlushAsync();
                    string savedVersion = stored.Rules.Single().ImageFilter.SchemaVersion!;
                    Emit("schema-" + version, new { version, editorVersion, retainedRaw, currentLabel, acknowledgement = acknowledge != null, invalidBlocked, writesBefore, writes, savedVersion, raw });
                    Program.CaptureWindow(window, "smoke-form-review-schema-" + version + ".png");
                    if (!retainedRaw || !currentLabel || !invalidBlocked || savedVersion != version || writes != writesBefore + 1)
                        transitionFailures.Add("Transition to " + version + " retained editor=" + editorVersion + ", saved=" + savedVersion + ".");
                }
                Require(transitionFailures.Count == 0, string.Join(" ", transitionFailures));
            }
            finally { rules.EndLifetime(); host.Content = retained; Texts.SetLanguage(language); }
        }

        void Emit(string scene, object state)
        {
            string json = JsonSerializer.Serialize(new { scene, state, scale = window.RenderScaling, client = window.ClientSize.ToString() });
            Console.WriteLine("FORM POST URSA " + json);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "form-post-ursa-" + scene + ".json"), json);
        }
    }

    private static object Bounds(Control control, Control relative) => new { bounds = control.Bounds.ToString(), position = control.TranslatePoint(default, relative)?.ToString(), attached = control.IsAttachedToVisualTree(), visible = control.IsEffectivelyVisible };
    private static bool VisibleInScroll(Control control, ScrollViewer scroll)
    {
        Point? point = control.TranslatePoint(default, scroll);
        return control.IsAttachedToVisualTree() && control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0 && point.HasValue
            && point.Value.Y >= -1 && point.Value.Y + control.Bounds.Height <= scroll.Bounds.Height + 1;
    }
    private static bool HasError(PluginSettingsEditor editor, string message) => editor.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Classes.Contains("error") && text.IsVisible && (text.Text ?? "").Contains(message, StringComparison.Ordinal));
    private static async Task FlushAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
