using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;
using Tmds.DBus.Protocol;

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
        HashSet<string> observedNonces = new(StringComparer.Ordinal);
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
            using ObserverDiagnostics diagnostics = new(directory, id, digest);
            void TextInputTunnel(object? sender, TextInputEventArgs e) => diagnostics.Input("tunnel", e.Text, e.Handled);
            void TextInputBubble(object? sender, TextInputEventArgs e) => diagnostics.Input("bubble", e.Text, e.Handled);
            void TextChanging(object? sender, TextChangingEventArgs e) => diagnostics.Input("changing", (target as TextBox)?.Text, e.Handled);
            target.AddHandler(InputElement.TextInputEvent, TextInputTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
            target.AddHandler(InputElement.TextInputEvent, TextInputBubble, RoutingStrategies.Bubble, handledEventsToo: true);
            if (target is TextBox observedTextBox) observedTextBox.TextChanging += TextChanging;
            try
            {
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
                diagnostics.Mark("atomic-write:" + file);
                File.WriteAllText(path + ".tmp", json);
                diagnostics.Mark("atomic-move:" + file);
                File.Move(path + ".tmp", path, true);
                diagnostics.Mark("atomic-complete:" + file);
            }
            diagnostics.Mark("initial-state");
            object firstState = State();
            diagnostics.Mark("initial-serialize");
            string first = JsonSerializer.Serialize(firstState);
            WriteAtomic("state.json", first); WriteAtomic("stage.json", first);
            Console.WriteLine("NATIVE IME STAGE " + id);
            DateTime deadline = DateTime.UtcNow.AddSeconds(55);
            using CancellationTokenSource stageEnded = new();
            Task? contextRead = null;
            bool ownerReadFailed = false;
            try
            {
                while (!File.Exists(Path.Combine(directory, id + ".done")))
                {
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("External IME driver did not finish " + id);
                    if (ownerReadFailed) throw new InvalidOperationException("Actual owner-context read failed for " + id);
                    string requestPath = Path.Combine(directory, "context-request.json");
                    if (File.Exists(requestPath))
                    {
                        if (new FileInfo(requestPath).Length > 4096) throw new IOException("Oversized owner-context request.");
                        using JsonDocument request = JsonDocument.Parse(File.ReadAllText(requestPath));
                        JsonElement root = request.RootElement;
                        string nonce = root.GetProperty("nonce").GetString() ?? "";
                        if (nonce.Length != 64 || !nonce.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                            throw new InvalidOperationException("Owner-context request nonce is not canonical.");
                        if (!observedNonces.Contains(nonce))
                        {
                            if (contextRead is { IsCompleted: false }) throw new InvalidOperationException("Overlapping owner-context requests.");
                            if (root.GetProperty("id").GetString() != id || root.GetProperty("pid").GetInt32() != Environment.ProcessId
                                || root.GetProperty("sourceDigest").GetString() != digest)
                                throw new InvalidOperationException("Owner-context request does not match the actual stage/PID/source.");
                            observedNonces.Add(nonce);
                            // Starts on the UI thread and yields at the existing proxy's asynchronous getter.
                            // The 25 ms UI-state observer continues while this single read is in flight.
                            contextRead = ReplyOwnerContextAsync(nonce);
                        }
                    }
                    diagnostics.Mark("sample-state");
                    object currentState = State();
                    diagnostics.Mark("sample-serialize");
                    string current = JsonSerializer.Serialize(currentState);
                    WriteAtomic("state.json", current);
                    diagnostics.Mark("sample-append-await");
                    await File.AppendAllTextAsync(Path.Combine(directory, "state.jsonl"), current + Environment.NewLine);
                    diagnostics.PassCompleted();
                    diagnostics.Mark("sample-delay-await");
                    await Task.Delay(25);
                    diagnostics.Mark("sample-delay-resumed");
                }
                if (contextRead is { IsCompleted: false } || ownerReadFailed)
                    throw new InvalidOperationException("Checkpoint ended before a successful owner-context response.");
                WriteAtomic(id + "-final.json", JsonSerializer.Serialize(State()));
            }
            finally
            {
                // A late reply must never be written into a later stage. Cancellation only stops this
                // observation; it does not pretend to cancel the already sent, read-only D-Bus call.
                stageEnded.Cancel();
                if (contextRead is not null) await contextRead;
            }

            async Task ReplyOwnerContextAsync(string nonce)
            {
                object? observedOwner = null, observedEngine = null;
                string? error = null;
                bool success = false;
                const int timeoutMs = 650;
                try
                {
                    OwnerContext before = ReadOwnerContext(target);
                    TimeSpan budget = deadline - DateTime.UtcNow;
                    if (budget <= TimeSpan.Zero) throw new TimeoutException("Owner-context request exceeded its checkpoint.");
                    budget = budget < TimeSpan.FromMilliseconds(timeoutMs) ? budget : TimeSpan.FromMilliseconds(timeoutMs);
                    Stopwatch elapsed = Stopwatch.StartNew();
                    // Stock IBus 1.5.34 EngineDesc has 19 fields; VariantValue supports only 16.
                    // Use the same owner's connection/destination/path with a typed read-only reply reader.
                    Task<EngineDescriptor> pending = ReadOwnerEngineAsync(before);
                    // Observe a possible late fault after timeout without scheduling another owner read.
                    _ = pending.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    TimeSpan remaining = budget - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero) throw new TimeoutException("Actual owner-context getter exceeded 650 ms.");
                    EngineDescriptor descriptor = await pending.WaitAsync(remaining, stageEnded.Token);
                    if (elapsed.Elapsed > budget || DateTime.UtcNow >= deadline)
                        throw new TimeoutException("Actual owner-context getter exceeded its observation budget.");
                    Dispatcher.UIThread.VerifyAccess();
                    stageEnded.Token.ThrowIfCancellationRequested();
                    OwnerContext after = ReadOwnerContext(target);
                    if (!ReferenceEquals(before.Window, after.Window) || !ReferenceEquals(before.Platform, after.Platform)
                        || !ReferenceEquals(before.Ime, after.Ime) || !ReferenceEquals(before.Proxy, after.Proxy)
                        || !ReferenceEquals(before.Connection, after.Connection) || before.Xid != after.Xid
                        || before.UniqueName != after.UniqueName || before.Destination != after.Destination || before.Path != after.Path)
                        throw new InvalidOperationException("Actual owner context changed during the read.");
                    observedOwner = new
                    {
                        uniqueName = after.UniqueName, portalDestination = after.Destination, portalPath = after.Path,
                        topLevelXid = after.Xid, imeType = after.Ime.GetType().FullName, proxyType = after.Proxy.GetType().FullName,
                        connectionSame = ReferenceEquals(after.Connection, after.Proxy.Connection),
                        focus = new { targetFocused = target.IsFocused || target.IsKeyboardFocusWithin, topLevelActive = after.Window.IsActive }
                    };
                    string descriptorType = descriptor.DescriptorType, engineName = descriptor.Name;
                    observedEngine = new
                    {
                        descriptorType, name = engineName, count = descriptor.Count, nameType = descriptor.NameType,
                        signature = descriptor.Signature, timeoutMs
                    };
                    if (descriptorType != "IBusEngineDesc" || engineName != "anthy")
                        throw new InvalidOperationException("The actual owner-context engine is not stock Anthy.");
                    success = true;
                }
                catch (Exception exception)
                {
                    ownerReadFailed = true;
                    Exception cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
                    error = cause.GetType().Name + ": " + cause.Message;
                }
                if (!stageEnded.IsCancellationRequested)
                    WriteAtomic("context-response.json", JsonSerializer.Serialize(new
                    {
                        nonce, id, pid = Environment.ProcessId, sourceDigest = digest, success, error,
                        owner = observedOwner, engine = observedEngine
                    }));
            }
            }
            catch (Exception exception)
            {
                diagnostics.Fail(exception);
                throw;
            }
            finally
            {
                target.RemoveHandler(InputElement.TextInputEvent, TextInputTunnel);
                target.RemoveHandler(InputElement.TextInputEvent, TextInputBubble);
                if (target is TextBox observedTextBoxEnd) observedTextBoxEnd.TextChanging -= TextChanging;
            }
        }
    }

    // All UI observations are captured on the UI thread. The background timer reads cached data only.
    private sealed class ObserverDiagnostics : IDisposable
    {
        private readonly string _path, _stage, _digest;
        private readonly System.Threading.Timer _writer;
        private object _phase = new { name = "created" };
        private object? _tunnel, _bubble, _changing;
        private long _phaseSequence, _passCount, _passTicks;
        private int _writing, _ended, _diagnosticErrorReported;

        internal ObserverDiagnostics(string directory, string stage, string digest)
        {
            _path = Path.Combine(directory, stage + "-observer.jsonl"); _stage = stage; _digest = digest;
            _writer = new System.Threading.Timer(WriteCached, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        }

        internal void Mark(string name) => Volatile.Write(ref _phase, new
        {
            sequence = ++_phaseSequence, name, utc = DateTimeOffset.UtcNow,
            monotonic = Stopwatch.GetTimestamp(), threadId = Environment.CurrentManagedThreadId
        });

        internal void Input(string kind, string? text, bool handled)
        {
            object value = new { utc = DateTimeOffset.UtcNow, monotonic = Stopwatch.GetTimestamp(),
                threadId = Environment.CurrentManagedThreadId, text, handled };
            if (kind == "tunnel") Volatile.Write(ref _tunnel, value);
            else if (kind == "bubble") Volatile.Write(ref _bubble, value);
            else Volatile.Write(ref _changing, value);
        }

        internal void Fail(Exception exception) => Console.Error.WriteLine("NATIVE IME OBSERVER ERROR "
            + JsonSerializer.Serialize(new { id = _stage, phase = Volatile.Read(ref _phase), exception = exception.ToString() }));

        internal void PassCompleted()
        {
            Interlocked.Increment(ref _passCount);
            Interlocked.Exchange(ref _passTicks, Stopwatch.GetTimestamp());
        }

        private void WriteCached(object? state)
        {
            if (Interlocked.Exchange(ref _writing, 1) != 0) return;
            try
            {
                string json = JsonSerializer.Serialize(new
                {
                    id = _stage, pid = Environment.ProcessId, sourceDigest = _digest, utc = DateTimeOffset.UtcNow,
                    monotonic = Stopwatch.GetTimestamp(), stopwatchFrequency = Stopwatch.Frequency,
                    phase = Volatile.Read(ref _phase), observerPasses = Interlocked.Read(ref _passCount),
                    lastObserverPass = Interlocked.Read(ref _passTicks), ended = Volatile.Read(ref _ended) != 0,
                    textInputTunnel = Volatile.Read(ref _tunnel), textInputBubble = Volatile.Read(ref _bubble),
                    textChanging = Volatile.Read(ref _changing)
                });
                File.AppendAllText(_path, json + Environment.NewLine);
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _diagnosticErrorReported, 1) == 0)
                    Console.Error.WriteLine("NATIVE IME DIAGNOSTIC WRITE ERROR " + exception);
            }
            finally { Volatile.Write(ref _writing, 0); }
        }

        public void Dispose()
        {
            Volatile.Write(ref _ended, 1);
            _writer.Dispose();
        }
    }

    private sealed record EngineDescriptor(string DescriptorType, string Name, int Count, string NameType, string Signature);

    private static Task<EngineDescriptor> ReadOwnerEngineAsync(OwnerContext owner)
    {
        using var writer = owner.Connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: owner.Destination, path: owner.Path,
            @interface: "org.freedesktop.IBus.InputContext", member: "GetEngine");
        return owner.Connection.CallMethodAsync<EngineDescriptor>(writer.CreateMessage(), ReadEngineDescriptor);
    }

    private static EngineDescriptor ReadEngineDescriptor(Message message, object? state)
    {
        if (message.SignatureAsString != "v")
            throw new InvalidOperationException("Actual GetEngine reply must contain exactly one variant.");
        var reader = message.GetBodyReader();
        string signature = reader.ReadSignatureAsString();
        // Pinned IBus 1.5.34 serializer: typename, attachments, eight strings, rank uint, eight strings.
        // Validate the actual wire signature before reading; never construct a 19-field VariantValue.
        const string prefix = "(sa{sv}";
        if (signature != "(sa{sv}ssssssssussssssss)")
            throw new InvalidOperationException("Unexpected actual IBus EngineDesc wire signature: " + signature);
        reader.AlignStruct();
        string descriptorType = reader.ReadString();
        reader.SkipTo(reader.ReadDictionaryStart()); // Bounds-checked attachments; unrelated to engine identity.
        string name = reader.ReadString();
        // Consume every remaining declared field using the public primitive reader API.
        for (int i = prefix.Length + 1; i < signature.Length - 1; i++)
        {
            if (signature[i] == 's') _ = reader.ReadString();
            else if (signature[i] == 'u') _ = reader.ReadUInt32();
            else throw new InvalidOperationException("Unsupported actual engine descriptor field.");
        }
        return new(descriptorType, name, 2 + signature.Length - prefix.Length - 1,
            signature[prefix.Length] == 's' ? nameof(VariantValueType.String) : throw new InvalidOperationException("Actual engine name is not a string."), signature);
    }

    private sealed record OwnerContext(Window Window, object Platform, object Ime, DBusObject Proxy,
        DBusConnection Connection, ulong Xid, string UniqueName, string Destination, string Path);

    private static OwnerContext ReadOwnerContext(Control target)
    {
        Dispatcher.UIThread.VerifyAccess();
        Window window = TopLevel.GetTopLevel(target) as Window
            ?? throw new InvalidOperationException("The actual IME target has no owning Window.");
        object platform = window.PlatformImpl ?? throw new InvalidOperationException("The actual TopLevel has no platform implementation.");
        if (platform.GetType().FullName != "Avalonia.X11.X11Window")
            throw new InvalidOperationException("The actual IME owner is not an X11 window.");
        object ime = ReadField(platform, "_ime");
        if (ime.GetType().FullName != "Avalonia.FreeDesktop.DBusIme.IBus.IBusX11TextInputMethod")
            throw new InvalidOperationException("The actual window is not using the stock Avalonia IBus input method.");
        object context = ReadField(ime, "_context");
        if (context.GetType().FullName != "Avalonia.FreeDesktop.DBusIme.IBus.DBus.InputContext" || context is not DBusObject proxy)
            throw new InvalidOperationException("The existing IME context is not the expected IBus proxy.");
        PropertyInfo? connectionProperty = null;
        for (Type? type = ime.GetType(); type is not null && connectionProperty is null; type = type.BaseType)
            connectionProperty = type.GetProperty("Connection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        DBusConnection connection = connectionProperty?.GetValue(ime) as DBusConnection
            ?? throw new InvalidOperationException("The actual input-method connection is unavailable.");
        if (!ReferenceEquals(connection, proxy.Connection))
            throw new InvalidOperationException("The existing context is not on the actual input-method owner connection.");
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.HandleDescriptor != "XID" || handle.Handle == IntPtr.Zero)
            throw new InvalidOperationException("The actual IME TopLevel has no X11 XID.");
        if (!(target.IsFocused || target.IsKeyboardFocusWithin) || !window.IsActive)
            throw new InvalidOperationException("The actual IME target/window lost focus.");
        string unique = connection.UniqueName ?? throw new InvalidOperationException("The actual owner session connection has no unique name.");
        if (!unique.StartsWith(':')) throw new InvalidOperationException("The actual owner connection has no D-Bus unique name.");
        return new(window, platform, ime, proxy, connection, unchecked((ulong)handle.Handle.ToInt64()), unique, proxy.Destination, proxy.Path.ToString());
    }

    private static object ReadField(object instance, string name)
    {
        for (Type? type = instance.GetType(); type is not null; type = type.BaseType)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null) return field.GetValue(instance)
                ?? throw new InvalidOperationException("Actual owner field is not initialized: " + name);
        }
        throw new InvalidOperationException("Actual owner field is missing: " + name);
    }
}
