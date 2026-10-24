using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopSmoke;

// Read-only UI observer for an external stock-IME process. Never injects composition/text/key events.
// A .done handshake completes a checkpoint, not IME acceptance: engine and UI traces are separate gates.
internal static class NativeImeChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1")
            throw new InvalidOperationException("IME probe requires the runner's private Linux Xvfb session.");
        string directory = Environment.GetEnvironmentVariable("MEOWCHAN_IME_EXCHANGE")
            ?? throw new InvalidOperationException("An explicit private IME exchange is required.");
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new IOException("IME exchange must already exist at an absolute private path.");
        if (Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("IME exchange must be empty; stale acknowledgements are rejected.");
        string digest = Environment.GetEnvironmentVariable("MEOWCHAN_SOURCE_DIGEST") ?? "";
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
            throw new InvalidOperationException("IME probe requires the actual frozen source digest.");
        string language = Environment.GetEnvironmentVariable("MEOWCHAN_NATIVE_INPUT_LANGUAGE") ?? "en";
        if (language is not ("en" or "de")) throw new InvalidOperationException("IME UI language must be en/de.");
        Texts.SetLanguage(language);
        await PresentationChecks.SwitchModeAsync(owner, "Advanced");
        owner.Width = 1100; owner.Height = 800;
        int sourceSubmits = 0, appliedSearchChanges = 0;
        ArchivePage archive = owner.ArchivePage;
        FieldInfo searchGeneration = typeof(ArchivePage).GetField("_searchValidationGeneration", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The observed real search-generation field is missing.");
        long ReadSearchGeneration() => (long)searchGeneration.GetValue(archive)!;
        archive.AppliedSearchChanged += OnAppliedSearchChanged;
        void OnAppliedSearchChanged() => appliedSearchChanges++;
        ContentControl pluginHost = owner.FindControl<ContentControl>("PluginsPageHost")!;
        ContentControl sourceHost = owner.FindControl<ContentControl>("SourcesPageHost")!;
        object? originalPlugins = pluginHost.Content, originalSources = sourceHost.Content;
        PluginUxFixture fixture = await PluginUxFixture.CreateAsync(Path.Combine(owner.SessionHost.ProfileDirectory, "ime-plugin-fixture"));
        PluginsPage plugins = new(); plugins.Attach(fixture.Services);
        SourcesPage sources = new(_ => false, _ => { }, () => { }, () => { }, _ =>
        {
            sourceSubmits++;
            return Task.FromResult(false); // Observe the real submit boundary without network or a watch.
        });
        try
        {
            pluginHost.Content = plugins;
            Navigate("PluginsMenuItem");
            await plugins.ActivateAsync(); plugins.Select(PluginUxFixture.SourceKey);
            PluginSettingsEditor settings = await PluginSettingsFormChecks.OpenManagedScopeAsync(plugins, SettingScope.Plugin);
            TextBox label = (TextBox)settings.FindField("label")!;
            label.Text = ""; // Fixture baseline only, before any external engine interaction.
            long profileRevision = (await fixture.Store.ReadAsync()).Revision;
            await ObserveAsync("plugin-label-commit", label);
            await ObserveAsync("plugin-label-cancel", label);
            if (fixture.ForbiddenCalls != 0 || (await fixture.Store.ReadAsync()).Revision != profileRevision)
                throw new InvalidOperationException("IME input unexpectedly executed a plugin operation or persisted settings.");

            sourceHost.Content = sources;
            sources.Refresh([new("ime-fixture", "IME fixture source", new Uri("https://ime.example.test/"),
                ProviderCapabilities.ThreadWatch, ["ime.example.test"], "fixture", true)], owner.SessionHost.GetView(), true);
            sources.SelectSource("ime-fixture"); Navigate("SourcesMenuItem");
            TextBox sourceUrl = sources.FindControl<TextBox>("SourceUrlInput")!;
            sourceUrl.Text = "";
            await ObserveAsync("source-url-commit", sourceUrl);
            await ObserveAsync("source-url-cancel", sourceUrl);
            if (sourceSubmits != 0) throw new InvalidOperationException("Composition commit/cancel prematurely submitted a source URL.");
            await ObserveAsync("source-url-normal-enter", sourceUrl);
            if (sourceSubmits != 1) throw new InvalidOperationException("The separate non-composition Enter did not cross exactly one real submit boundary.");

            Navigate("ArchiveMenuItem");
            await Program.WaitUntilAsync(() => !archive.IsBusy && archive.AppliedSearchSpec != null);
            TextBox query = archive.FindControl<TextBox>("ArchiveSearchText")!;
            query.Text = "";
            long beforeCompositionSearch = ReadSearchGeneration();
            await ObserveAsync("archive-query-commit", query);
            await ObserveAsync("archive-query-cancel", query);
            if (ReadSearchGeneration() != beforeCompositionSearch)
                throw new InvalidOperationException("Composition commit/cancel prematurely invoked archive search.");
            IdentitySuggestionBox identity = archive.FindControl<IdentitySuggestionBox>("ArchiveSourceFilter")!;
            var originalCandidates = identity.Candidates;
            try
            {
                // The actual product popup/selection code receives deterministic passive fixture identities.
                identity.Candidates = (_, _) => Task.FromResult(new[] { new IdentitySuggestion("ime-fixture", null, "IME fixture source") });
                identity.Text = "";
                identity.BringIntoView(); identity.Focus(); identity.RestartSuggestions();
                await Program.WaitUntilAsync(() => identity.IsDropDownOpen);
                await ObserveAsync("archive-identity-conflict", identity);
                if (ReadSearchGeneration() != beforeCompositionSearch)
                    throw new InvalidOperationException("Identity/IME candidate handling prematurely invoked archive search.");
            }
            finally { identity.Candidates = originalCandidates; identity.Dismiss(); }
            if (owner.SessionHost.GetView().Threads.Count != 0 || owner.SessionHost.GetView().Boards.Count != 0
                || owner.SessionHost.ReceivedMediaBytes != 0 || fixture.ForbiddenCalls != 0)
                throw new InvalidOperationException("IME observation unexpectedly created work/media or invoked plugin services.");
            File.WriteAllText(Path.Combine(directory, "checkpoint-complete.json"), JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, sourceDigest = digest, language, sourceSubmits, appliedSearchChanges,
                scope = "Read-only product-state checkpoints completed; no IME PASS. Require independent real engine preedit/commit/lookup trace and observed UI evidence."
            }));
        }
        finally
        {
            archive.AppliedSearchChanged -= OnAppliedSearchChanged;
            plugins.EndLifetime(); sources.EndLifetime();
            pluginHost.Content = originalPlugins; sourceHost.Content = originalSources;
        }

        void Navigate(string menu) => owner.FindControl<MenuItem>(menu)!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        async Task ObserveAsync(string id, Control target)
        {
            target.BringIntoView();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (!target.IsEffectivelyVisible || !target.IsEffectivelyEnabled || !target.Focus())
                throw new InvalidOperationException("IME target is not reachable: " + id);
            object State() => new
            {
                id, utc = DateTimeOffset.UtcNow, pid = Environment.ProcessId, language, sourceDigest = digest,
                target = new { id = AutomationProperties.GetAutomationId(target) ?? target.Name,
                    name = AutomationProperties.GetName(target), type = target.GetType().Name,
                    target.IsFocused, target.IsKeyboardFocusWithin, target.IsEffectivelyVisible,
                    text = target is TextBox text ? text.Text : target is IdentitySuggestionBox box ? box.Text : null },
                preedit = target.GetVisualDescendants().OfType<TextPresenter>().Select(p => new
                    { text = p.PreeditText, p.IsEffectivelyVisible }).ToArray(),
                identity = target is IdentitySuggestionBox suggestion ? new
                {
                    suggestion.IsDropDownOpen, suggestion.AcceptedCandidate,
                    selected = suggestion.SelectedItem as IdentitySuggestion,
                    items = suggestion.ItemsSource?.OfType<IdentitySuggestion>().ToArray()
                } : null,
                sourceSubmitCount = sourceSubmits, appliedSearchChanges,
                searchValidationGeneration = ReadSearchGeneration(),
                searchBusy = archive.IsBusy,
                appliedSearch = archive.AppliedSearchSpec is { } spec ? ArchiveSearchSpecJson.Serialize(spec) : null,
                page = owner.SelectedPage.ToString(), windows = owner.OwnedWindows.Select(w => w.Title).ToArray(),
                scope = "Observed UI only; real stock-engine preedit/lookup/commit and native input must be proven independently."
            };
            void WriteAtomic(string file, string json)
            {
                string path = Path.Combine(directory, file);
                File.WriteAllText(path + ".tmp", json); File.Move(path + ".tmp", path, true);
            }
            string first = JsonSerializer.Serialize(State());
            WriteAtomic("state.json", first); WriteAtomic("stage.json", first);
            Console.WriteLine("NATIVE IME STAGE " + id);
            DateTime deadline = DateTime.UtcNow.AddSeconds(55);
            while (!File.Exists(Path.Combine(directory, id + ".done")))
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("External IME driver did not finish " + id);
                string current = JsonSerializer.Serialize(State());
                WriteAtomic("state.json", current);
                await File.AppendAllTextAsync(Path.Combine(directory, "state.jsonl"), current + Environment.NewLine);
                await Task.Delay(25);
            }
            WriteAtomic(id + "-final.json", JsonSerializer.Serialize(State()));
        }
    }
}
