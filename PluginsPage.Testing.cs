using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan;

public partial class PluginsPage
{
    private sealed record TestedCandidate(string Key, PluginArtifact Artifact, PluginDescription Description, PluginConfiguration Configuration);
    private TestedCandidate? _tested;
    private string? _testStatusKey;
    private object?[] _testStatusArguments = Array.Empty<object?>();
    private void SetTestStatus(string? key, params object?[] arguments)
    {
        _testStatusKey = key; _testStatusArguments = arguments;
        C<TextBlock>("PluginTestStatus").Text = key == null ? string.Empty : Texts.T(key, arguments);
    }

    private void UpdateTestButtons()
    {
        C<Ursa.Controls.LoadingIcon>("PluginOperationLoading").IsLoading = C<Ursa.Controls.LoadingIcon>("PluginOperationLoading").IsVisible = _busy;
        if (_busy && _testStatusKey == null) C<TextBlock>("PluginTestStatus").Text = Texts.T("feedback.working");
        bool source = Selected?.SourcePath != null;
        SetAction("PluginBuild", source ? "plugins.unavailable" : "plugins.reason.noSource", source && _services != null);
        SetAction("PluginEdit", "plugins.reason.noSource", source);
        SetAction("PluginDefaultEditor", "plugins.reason.noSource", source);
        C<Button>("PluginCancel").IsEnabled = _busy;
        C<Button>("PluginCancel").IsVisible = _busy;
        SetAction("PluginProbe", "plugins.reason.noTest", _tested is { } tested && tested.Key == _selectedKey && tested.Description.Sources.Count > 0);
        C<TextBox>("PluginProbeUrl").IsEnabled = !_busy;
        SetAction("PluginEditorSave", "plugins.reason.noEditorStore", SaveEditorOptionsAsync != null);
        ShowReasons("PluginSourceReasons", "PluginBuild", "PluginEdit", "PluginDefaultEditor", "PluginProbe", "PluginEditorSave");
        C<StackPanel>("PluginSettings").IsEnabled = !_busy;
    }
    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _pluginSectionNavigation?.CancelPendingNavigation();
        if (_ended || _busy || _services == null || CurrentDraft is not { } draft || Selected is not { } entry || entry.PluginId == null) return;
        PluginConfiguration configuration = draft.Configuration(out IReadOnlyList<FieldValidationError> errors);
        ShowFieldErrors(errors, focusFirstError: true);
        if (errors.Count > 0) return;
        PluginArtifact? artifact = entry.ConfigurationArtifact;
        if (artifact == null) { Error(Texts.T("plugins.noArtifact")); FocusFirstSettingError(); return; }
        (long generation, CancellationToken token) = BeginOperation();
        bool focusSaveError = false;
        try
        {
            await using (IPluginWorkerClient worker = await _services.StartTestWorkerAsync(artifact, configuration, token))
            {
                errors = await ValidateInWorkerAsync(worker, configuration, token);
                if (!Current(generation)) return;
                ShowFieldErrors(errors);
                if (errors.Count > 0) { focusSaveError = true; return; }
            }
            await _services.ProfileStore.SaveConfigurationAsync(entry.PluginId, draft.CandidateId, configuration, draft.ExpectedRevision, token);
            if (!Current(generation)) return;
            _drafts.Remove(entry.Key); _renderedKey = null;
            await RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (Current(generation)) { Error(exception.Message); focusSaveError = true; } }
        finally
        {
            FinishOperation(generation);
            // The test worker's asynchronous disposal must finish before focusing enabled controls.
            if (focusSaveError && !_ended && generation == _operationGeneration && ReferenceEquals(CurrentDraft, draft))
                FocusFirstSettingError();
        }
    }
    private void ShowFieldErrors(IReadOnlyList<FieldValidationError> errors, bool focusFirstError = false)
    {
        foreach ((string scope, PluginSettingsEditor editor) in _editors)
        {
            IReadOnlyList<FieldValidationError> scoped = scope.Length == 0 ? errors.Where(error => !error.FieldKey.Contains('/')).ToArray()
                : errors.Where(error => error.FieldKey.StartsWith(scope + "/", StringComparison.Ordinal))
                    .Select(error => error with { FieldKey = error.FieldKey[(scope.Length + 1)..] }).ToArray();
            editor.ShowErrors(scoped);
        }
        Error(errors.Count == 0 ? null : string.Join(Environment.NewLine, errors.Select(error => error.Message)));
        if (focusFirstError && errors.Count > 0) FocusFirstSettingError();
    }
    private static async Task<IReadOnlyList<FieldValidationError>> ValidateInWorkerAsync(IPluginWorkerClient worker,
        PluginConfiguration configuration, CancellationToken token)
    {
        List<FieldValidationError> errors = new(await worker.ValidateSettingsAsync(new(SettingScope.Plugin, configuration.PluginSettings), token));
        foreach ((string source, SettingsSnapshot settings) in configuration.SourceSettings)
        {
            IReadOnlyList<FieldValidationError> found = await worker.ValidateSettingsAsync(new(SettingScope.Source, settings, SourceId: source), token);
            errors.AddRange(found.Select(error => error with { FieldKey = source + "/" + error.FieldKey }));
        }
        return errors;
    }
    private async void OnBuildClick(object? sender, RoutedEventArgs e) => await BuildAndTestAsync();
    internal async Task BuildAndTestAsync()
    {
        if (_ended || _busy || _services == null || Selected is not { SourcePath: { } sourcePath } entry) return;
        (long generation, CancellationToken token) = BeginOperation();
        _tested = null;
        C<StackPanel>("PluginBuildDiagnostics").Children.Clear();
        C<TextBox>("PluginProbeResult").Text = string.Empty;
        SetTestStatus("plugins.building");
        try
        {
            PluginBuildResult result = await _services.Compiler.BuildAsync(sourcePath, cancellationToken: token);
            if (!Current(generation)) return;
            RenderDiagnostics(result.Diagnostics, entry);
            if (!result.Succeeded) { Error(Texts.T("plugins.buildFailed", result.Status)); SetTestStatus("plugins.buildFailed", result.Status); return; }
            SetTestStatus("plugins.testing", result.Artifact!.ExecutionRevision);
            PluginDescription description = await _services.DescribeTestAsync(result.Artifact, token);
            if (!Current(generation)) return;
            await _services.ProfileStore.ObserveCandidateAsync(new(entry.CandidateId!, description.PluginId, result.Artifact, description, CandidatePath: sourcePath), _catalog!.Profile.Revision, token);
            if (!Current(generation)) return;
            await RefreshAsync();
            if (!Current(generation)) return;
            PluginConfigurationDraft testDraft = new(entry with { Description = description, Activation = entry.Activation is { } previous ? previous with { Pinned = null } : null }, _catalog!.Profile.Revision);
            PluginConfiguration configuration = testDraft.Configuration(out IReadOnlyList<FieldValidationError> errors);
            if (errors.Count > 0) { ShowFieldErrors(errors); SetTestStatus("plugins.testFailed"); return; }
            await using (IPluginWorkerClient worker = await _services.StartTestWorkerAsync(result.Artifact, configuration, token))
            {
                errors = await ValidateInWorkerAsync(worker, configuration, token);
                if (!Current(generation)) return;
                if (errors.Count > 0) { ShowFieldErrors(errors); SetTestStatus("plugins.testFailed"); return; }
            }
            if (!Current(generation)) return;
            _tested = new(entry.Key, result.Artifact, description, configuration);
            SetTestStatus("plugins.testPassed", result.Artifact.ExecutionRevision);
            await RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (Current(generation)) { Error(exception.Message); SetTestStatus("plugins.testFailed"); } }
        finally { FinishOperation(generation); }
    }
    private void RenderDiagnostics(IReadOnlyList<PluginBuildDiagnostic> diagnostics, PluginCatalogEntry entry)
    {
        StackPanel panel = C<StackPanel>("PluginBuildDiagnostics"); panel.Children.Clear();
        foreach (PluginBuildDiagnostic diagnostic in diagnostics)
        {
            panel.Children.Add(Label(diagnostic.Code + ": " + diagnostic.Message));
            if (diagnostic.FilePath != null)
            {
                string file = diagnostic.FilePath;
                string label = file + ":" + diagnostic.Line + ":" + diagnostic.Column;
                Button location = new() { Content = Label(label), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
                Avalonia.Automation.AutomationProperties.SetName(location, label);
                location.Click += async (_, _) =>
                {
                    // The compiler reports the loader input. Never accept plugin-supplied executable strings.
                    try { await PluginEditorLauncher.OpenAsync(GetEditorOptions?.Invoke() ?? new(null, new[] { "{file}" }), file, diagnostic.Line ?? 1, diagnostic.Column ?? 1, _lifetime.Token); }
                    catch (Exception error) { if (!_ended && _selectedKey == entry.Key) Error(error.Message); }
                };
                panel.Children.Add(location);
            }
        }
    }
    private async void OnProbeClick(object? sender, RoutedEventArgs e) => await ProbeAsync();
    internal async Task ProbeAsync()
    {
        if (_ended || _busy || _services == null || _tested is not { } tested || tested.Key != _selectedKey) return;
        string url = C<TextBox>("PluginProbeUrl").Text?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("https" or "http"))
        { Error(Texts.T("plugins.invalidUrl")); return; }
        (long generation, CancellationToken token) = BeginOperation();
        SetTestStatus("plugins.probing");
        try
        {
            object result;
            await using (IPluginWorkerClient worker = await _services.StartTestWorkerAsync(tested.Artifact, tested.Configuration, token))
                result = await _services.ProbeAsync(tested.Description, url, worker, new("plugin-probe:" + tested.Key, generation), token);
            if (!Current(generation)) return;
            string formatted = FormatProbe(result);
            C<TextBox>("PluginProbeResult").Text = formatted;
            if (result is ThreadFailure or CatalogFailure) Error(formatted);
            SetTestStatus("plugins.probeFinished");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (Current(generation)) Error(exception.Message); }
        finally { FinishOperation(generation); }
    }
    private static string FormatProbe(object result) => result switch
    {
        MeowChan.PluginSdk.ThreadSnapshot thread => Texts.T("plugins.threadResult", thread.Snapshot.Posts.Count, thread.Snapshot.Posts.Sum(post => post.Attachments?.Count ?? 0))
            + "\n" + string.Join("\n", thread.Snapshot.Posts.Take(100).Select(post => post.PostId + ": " + post.Subject + " " + post.Text
                + "\n" + string.Join("\n", post.Attachments?.Select(asset => "  " + asset.MediaId + " · " + asset.OriginalFileName + " · " + asset.Url) ?? Array.Empty<string>())))
            + (thread.Snapshot.Posts.Count > 100 ? "\n" + Texts.T("plugins.previewLimited") : string.Empty),
        CatalogSnapshot catalog => Texts.T("plugins.catalogResult", catalog.Snapshot.Threads.Count) + "\n"
            + string.Join("\n", catalog.Snapshot.Threads.Take(100).Select(thread => thread.Id.ThreadId + " · " + thread.Title + " · " + thread.Url)),
        ThreadFailure failure => failure.Failure.Code + ": " + failure.Failure.Message,
        CatalogFailure failure => failure.Failure.Code + ": " + failure.Failure.Message,
        ThreadDeferred deferred => Texts.T("plugins.deferred", deferred.RetryAt, deferred.ReasonCode),
        CatalogDeferred deferred => Texts.T("plugins.deferred", deferred.RetryAt, deferred.ReasonCode),
        ThreadNotFound missing => Texts.T("plugins.notFound", missing.HttpStatusCode),
        ThreadUnchanged or CatalogUnchanged => Texts.T("plugins.unchanged"),
        _ => throw new InvalidOperationException("Unexpected plugin probe result.")
    };
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    { CancelOperation(); SetTestStatus("plugins.cancelled"); RenderDetails(); BusyChanged?.Invoke(this, EventArgs.Empty); }
    private async void OnEditClick(object? sender, RoutedEventArgs e) => await EditAsync(false);
    private async void OnDefaultEditorClick(object? sender, RoutedEventArgs e) => await EditAsync(true);
    private async Task EditAsync(bool defaultApplication)
    {
        if (_ended || Selected?.SourcePath is not { } path) return;
        string? key = _selectedKey;
        try { await PluginEditorLauncher.OpenAsync(defaultApplication ? new(null, new[] { "{file}" }) : GetEditorOptions?.Invoke() ?? new(null, new[] { "{file}" }), path, cancellationToken: _lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_ended && key == _selectedKey) Error(Texts.T("plugins.editorFallback", exception.Message)); }
    }
    private async void OnEditorSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || SaveEditorOptionsAsync == null) return;
        PluginEditorOptions options = new(C<TextBox>("PluginEditorExecutable").Text?.Trim(),
            (C<TextBox>("PluginEditorArguments").Text ?? "{file}").Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'));
        string? key = _selectedKey;
        try { _ = PluginEditorLauncher.CreateStartInfo(options, Selected?.SourcePath ?? "plugin.cs"); await SaveEditorOptionsAsync(options); }
        catch (Exception exception) { if (!_ended && key == _selectedKey) Error(exception.Message); }
    }
}
