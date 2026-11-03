using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Admission;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

// Root invokes this on the native application UI thread. The fixture uses the real
// legacy registration and MakeTemplate paths; no preview, profile or network is used.
internal static class BoardAdmissionLanguageRefreshChecks
{
    private static readonly string[] Languages = ["en", "de", "es", "fr", "ru", "en"];
    private static readonly (string Id, string ControlName, string TranslationKey, bool PreserveHelp)[] Mappings =
    [
        ("admission.add-rule", "BoardAdmissionAddRule", "command.admission.add-rule", false),
        ("admission.cancel", "BoardAdmissionCancel", "command.admission.cancel", false),
        ("admission.retry-catalog", "BoardAdmissionRetry", "command.admission.retry-catalog", false),
        ("admission.reinclude", "BoardAdmissionReinclude", "command.admission.reinclude", false),
        ("admission.remove-rule", "BoardAdmissionRemoveRule", "command.admission.remove-rule", false),
        ("admission.apply", "BoardAdmissionApply", "command.admission.apply", true),
        ("admission.reset-choice", "BoardAdmissionResetChoice", "command.admission.reset-choice", true)
    ];

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        string originalLanguage = Texts.Language;
        ShortcutBindingsSection published = owner.SessionHost.Config.ShortcutBindings.Clone();
        var panel = new StackPanel { Spacing = 8 };
        var fixture = new Window
        {
            Title = "Board admission language binding fixture",
            Width = 720, Height = 300, Content = panel
        };
        try
        {
            Require(Texts.Warnings.Count == 0, "Bundled language catalog has loader warnings.");
            CheckBundledNonFallback();
            Texts.SetLanguage("en");
            using var bindings = new UiBindings();
            int calls = 0;
            var command = new DelegateCommand(() => calls++);
            Dictionary<string, Button> controls = CreateControls(panel, command);
            Dictionary<string, (TextBlock Name, TextBlock Help)> richSources = CreateRichBindings(controls, bindings);
            fixture.Show(owner);
            await SettleAsync(fixture);
            // The source bindings must own the richer values before the first real
            // LegacyCommandAdapters.Register call. Do not repair them afterwards.
            AssertInitialBindingPreconditions(controls, richSources);
            RegisterLegacyAdapters(fixture);
            AssertInitialRegistration(controls, richSources);

            await CheckRegisteredMappingsAsync(fixture, controls, richSources, published, command);
            Require(calls == 0, "Language, adapter or shortcut refresh unexpectedly executed a command.");
            command.Execute(null);
            Require(calls == 1, "Original command did not execute exactly once.");
            panel.Children.Clear();
            await CheckLateMaterializedResetAsync(fixture, panel, published);

            Console.WriteLine("SMOKE board admission language refresh: 7 real legacy mappings across EN/DE/ES/FR/RU; rich bindings installed before first registration; generic re-registration and ShortcutService.Publish preserved them; reflected MakeTemplate late reset tooltip current; zero catalog warnings/non-fallback checks passed.");
        }
        finally
        {
            Dispatcher.UIThread.VerifyAccess();
            try { fixture.Close(); }
            finally
            {
                Texts.SetLanguage(originalLanguage);
                ShortcutService.Publish(published);
                await SettleAsync();
            }
        }
    }

    private static Dictionary<string, Button> CreateControls(StackPanel panel, DelegateCommand command)
    {
        Dictionary<string, Button> controls = new(StringComparer.Ordinal);
        foreach (var mapping in Mappings)
        {
            var button = new Button
            {
                Name = mapping.ControlName,
                Content = "unchanged host label",
                Command = command,
                IsEnabled = false
            };
            if (!mapping.PreserveHelp)
                AutomationProperties.SetName(button, "unchanged host name");
            panel.Children.Add(button);
            controls.Add(mapping.Id, button);
        }
        return controls;
    }

    private static Dictionary<string, (TextBlock Name, TextBlock Help)> CreateRichBindings(
        IReadOnlyDictionary<string, Button> controls, UiBindings bindings)
    {
        var result = new Dictionary<string, (TextBlock Name, TextBlock Help)>(StringComparer.Ordinal);
        foreach (var mapping in Mappings.Where(mapping => mapping.PreserveHelp))
        {
            var name = new TextBlock { Text = "per-thread name" };
            var help = new TextBlock { Text = "dynamic pending summary" };
            bindings.Add(controls[mapping.Id].Bind(AutomationProperties.NameProperty,
                new Binding(nameof(TextBlock.Text)) { Source = name, Mode = BindingMode.OneWay }));
            bindings.Add(controls[mapping.Id].Bind(AutomationProperties.HelpTextProperty,
                new Binding(nameof(TextBlock.Text)) { Source = help, Mode = BindingMode.OneWay }));
            result.Add(mapping.Id, (name, help));
        }
        return result;
    }

    private static void AssertInitialBindingPreconditions(
        IReadOnlyDictionary<string, Button> controls,
        IReadOnlyDictionary<string, (TextBlock Name, TextBlock Help)> richSources)
    {
        foreach (var pair in richSources)
        {
            Require(AutomationProperties.GetName(controls[pair.Key]) == "per-thread name",
                pair.Key + ": Name binding was not initialized before registration.");
            Require(AutomationProperties.GetHelpText(controls[pair.Key]) == "dynamic pending summary",
                pair.Key + ": HelpText binding was not initialized before registration.");
        }
    }

    private static void RegisterLegacyAdapters(Window fixture)
    {
        Type adapters = typeof(CommandBindings).Assembly.GetType(
            "MeowChan.Desktop.Commands.LegacyCommandAdapters", throwOnError: true)!;
        MethodInfo register = adapters.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Legacy command adapter registration is missing.");
        register.Invoke(null, [fixture]);
    }

    private static void AssertInitialRegistration(
        IReadOnlyDictionary<string, Button> controls,
        IReadOnlyDictionary<string, (TextBlock Name, TextBlock Help)> richSources)
    {
        foreach (var mapping in Mappings)
        {
            Button button = controls[mapping.Id];
            string expectedTip = CommandBindings.Tooltip(mapping.Id);
            Require(button.Name == mapping.ControlName, mapping.Id + ": wrong real control name.");
            Require(Equals(ToolTip.GetTip(button), expectedTip), mapping.Id + ": initial tooltip missing.");
            if (mapping.PreserveHelp)
            {
                var source = richSources[mapping.Id];
                Require(AutomationProperties.GetName(button) == source.Name.Text,
                    mapping.Id + ": first preserve-help registration overwrote Name.");
                Require(AutomationProperties.GetHelpText(button) == source.Help.Text,
                    mapping.Id + ": first preserve-help registration overwrote HelpText.");
            }
            else
            {
                Require(AutomationProperties.GetHelpText(button) == expectedTip,
                    mapping.Id + ": initial generic HelpText missing.");
            }
        }
    }

    private static async Task CheckRegisteredMappingsAsync(
        Window fixture,
        IReadOnlyDictionary<string, Button> controls,
        IReadOnlyDictionary<string, (TextBlock Name, TextBlock Help)> richSources,
        ShortcutBindingsSection published, DelegateCommand command)
    {
        Dictionary<string, string> englishLabels = Mappings.ToDictionary(
            mapping => mapping.Id, mapping => CommandCatalog.Label(mapping.Id));
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Require(Texts.Language == language && Texts.Warnings.Count == 0,
                "Language was not effective without warnings: " + language);
            // Replay the current session configuration without editing or saving it.
            long revision = ShortcutService.Revision;
            ShortcutService.Publish(published);
            await SettleAsync(fixture);
            Require(ShortcutService.Revision > revision, "ShortcutService did not publish the current snapshot.");

            foreach (var mapping in Mappings)
            {
                Button button = controls[mapping.Id];
                string expectedTip = CommandBindings.Tooltip(mapping.Id);
                Require(Equals(ToolTip.GetTip(button), expectedTip), mapping.Id + ": stale tooltip in " + language);
                if (language != "en")
                    Require(CommandCatalog.Label(mapping.Id) != englishLabels[mapping.Id],
                        mapping.Id + ": localized tooltip label fell back to English in " + language);
                if (mapping.PreserveHelp)
                {
                    var source = richSources[mapping.Id];
                    Require(AutomationProperties.GetName(button) == source.Name.Text,
                        mapping.Id + ": language/shortcut refresh changed Name in " + language);
                    Require(AutomationProperties.GetHelpText(button) == source.Help.Text,
                        mapping.Id + ": language/shortcut refresh degraded HelpText in " + language);
                }
                else
                {
                    Require(AutomationProperties.GetHelpText(button) == expectedTip,
                        mapping.Id + ": generic HelpText did not follow the published snapshot in " + language);
                    Require(AutomationProperties.GetName(button) == "unchanged host name",
                        mapping.Id + ": generic refresh changed the untouched Name in " + language);
                }
                Require(ReferenceEquals(button.Command, command)
                    && Equals(button.Content, "unchanged host label") && !button.IsEnabled,
                    mapping.Id + ": presentation refresh changed content or enabled state.");
            }

            // A later generic registration and a second real root registration are
            // allowed to add information, never to downgrade the monotone flag.
            CommandBindings.BindExisting(controls["admission.apply"], "admission.apply");
            CommandBindings.BindExisting(controls["admission.reset-choice"], "admission.reset-choice");
            RegisterLegacyAdapters(fixture);
            ShortcutService.Publish(published);
            await SettleAsync(fixture);
            foreach (var pair in richSources)
                Require(AutomationProperties.GetName(controls[pair.Key]) == pair.Value.Name.Text
                    && AutomationProperties.GetHelpText(controls[pair.Key]) == pair.Value.Help.Text,
                    pair.Key + ": generic re-registration degraded rich bindings.");

            foreach (var pair in richSources)
            {
                pair.Value.Name.Text += "!";
                pair.Value.Help.Text += "!";
            }
            await SettleAsync(fixture);
            foreach (var pair in richSources)
            {
                Require(AutomationProperties.GetName(controls[pair.Key]) == pair.Value.Name.Text,
                    pair.Key + ": Name binding stopped live propagation in " + language);
                Require(AutomationProperties.GetHelpText(controls[pair.Key]) == pair.Value.Help.Text,
                    pair.Key + ": HelpText binding stopped live propagation in " + language);
            }
        }
    }

    private static async Task CheckLateMaterializedResetAsync(
        Window fixture, StackPanel panel, ShortcutBindingsSection published)
    {
        MethodInfo makeTemplate = typeof(BoardAdmissionWindow).GetMethod("MakeTemplate",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Board admission template factory is missing.");
        var template = (IDataTemplate)(makeTemplate.Invoke(null, null)
            ?? throw new InvalidOperationException("Board admission template factory returned null."));
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            ShortcutService.Publish(published);
            await SettleAsync(fixture);
            object row = NewPreviewRow();
            Control materialized = template.Build(row)
                ?? throw new InvalidOperationException("Late board row template did not materialize.");
            panel.Children.Add(materialized);
            try
            {
                await SettleAsync(fixture);
                Button reset = materialized.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == "BoardAdmissionResetChoice");
                string expectedTip = CommandBindings.Tooltip("admission.reset-choice");
                Require(Equals(ToolTip.GetTip(reset), expectedTip),
                    "Late reset materialization lost the current tooltip in " + language);
                Require(AutomationProperties.GetName(reset) == Texts.T(
                    "wizard.resetChoiceForThread", "#42 · Late materialized thread"),
                    "Late reset materialization lost its current rich Name in " + language);
                Require(AutomationProperties.GetHelpText(reset) == Texts.T("wizard.resetChoiceHint"),
                    "Late reset materialization lost its current rich HelpText in " + language);
            }
            finally
            {
                panel.Children.Remove(materialized);
            }
        }
    }

    private static object NewPreviewRow()
    {
        Type rowType = typeof(BoardAdmissionWindow).GetNestedType("PreviewRow",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Board admission preview row type is missing.");
        object row = Activator.CreateInstance(rowType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create synthetic board preview row.");
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        rowType.GetField("Thread", fields)!.SetValue(row,
            new BoardCatalogThread("42", "https://synthetic.invalid/thread/42",
                "Late materialized thread", "Synthetic opening post", Suppressed: true));
        rowType.GetField("Choose", fields)!.SetValue(row, (Action<bool>)(_ => { }));
        rowType.GetField("Reset", fields)!.SetValue(row, (Action)(() => { }));
        return row;
    }

    private static void CheckBundledNonFallback()
    {
        string[] keys = Mappings.Select(mapping => mapping.TranslationKey)
            .Concat(["wizard.resetChoice", "wizard.resetChoiceForThread", "wizard.resetChoiceHint"]).ToArray();
        Texts.SetLanguage("en");
        Dictionary<string, string> english = keys.ToDictionary(key => key, key => Texts.T(key));
        foreach (string language in new[] { "es", "fr", "ru" })
        {
            Require(Texts.Languages.Any(item => item.Code == language), "Bundled language unavailable: " + language);
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(
                "MeowChan.Desktop.Localization.Packs." + language + ".json")
                ?? throw new InvalidOperationException("Bundled language resource missing: " + language);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement strings = document.RootElement.GetProperty("strings");
            Texts.SetLanguage(language);
            Require(Texts.Language == language && Texts.Warnings.Count == 0,
                language + ": language was not effective without loader warnings.");
            foreach (string key in keys)
            {
                Require(strings.TryGetProperty(key, out JsonElement entry), language + ": missing own catalog key " + key);
                string? expected = entry.GetString();
                Require(!string.IsNullOrWhiteSpace(expected) && expected != key && expected != english[key],
                    language + ": empty/key/English fallback for " + key);
                Require(Texts.T(key) == expected, language + ": effective catalog does not match the bundled value for " + key);
            }
            foreach (var mapping in Mappings)
                Require(CommandCatalog.Label(mapping.Id) == strings.GetProperty(mapping.TranslationKey).GetString(),
                    language + ": command label fell back instead of using its bundled translation: " + mapping.Id);
        }
    }

    private sealed class UiBindings : IDisposable
    {
        private readonly List<IDisposable> _bindings = [];
        public void Add(IDisposable binding) => _bindings.Add(binding);
        public void Dispose()
        {
            Dispatcher.UIThread.VerifyAccess();
            for (int index = _bindings.Count - 1; index >= 0; index--)
                _bindings[index].Dispose();
        }
    }

    private static async Task SettleAsync(Window? window = null)
    {
        await Dispatcher.UIThread.InvokeAsync(() => window?.UpdateLayout(), DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Dispatcher.UIThread.VerifyAccess();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
