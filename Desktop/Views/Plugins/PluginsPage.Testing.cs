using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan;

public partial class PluginsPage
{
    private sealed record TestedCandidate(string Key, PluginArtifact Artifact, PluginDescription Description, PluginConfiguration Configuration);
    private TestedCandidate? _tested;
    private string? _testStatusKey;
    private object?[] _testStatusArguments = Array.Empty<object?>();
    private bool IsCurrentTestedCandidate(TestedCandidate tested) => Selected is { Candidate: { IsPresent: true, Problem: null, Artifact: { } artifact } } entry
        && tested.Key == entry.Key && tested.Artifact.ExecutionRevision == artifact.ExecutionRevision
        && tested.Artifact.CompilationFingerprint == artifact.CompilationFingerprint;
    private void SetTestStatus(string? key, params object?[] arguments)
    {
        _testStatusKey = key; _testStatusArguments = arguments;
        C<TextBlock>("PluginTestStatus").Text = key == null ? string.Empty : Texts.T(key, arguments);
    }

    private void UpdateTestButtons()
    {
        // While the bundled repair review is open it owns the page operation: its own footer shows progress and its Close
        // stops it, so the page shows neither a spinner nor a Cancel that the modal keeps out of reach.
        bool reviewOpen = _busy && _migrationWindow is not null;
        bool working = (_busy && !reviewOpen) || _sourceOperationBusy;
        C<Ursa.Controls.LoadingIcon>("PluginOperationLoading").IsLoading = C<Ursa.Controls.LoadingIcon>("PluginOperationLoading").IsVisible = working;
        // Only the generic text is owned here: an operation's own result (_testStatusKey) is never erased. Once nothing runs,
        // "Working…" goes away instead of staying behind after the operation ended.
        if (_testStatusKey == null)
            C<TextBlock>("PluginTestStatus").Text = working ? Texts.T("feedback.working") : reviewOpen ? Texts.T("plugins.repair.reviewOpen") : string.Empty;
        bool source = Selected?.SourcePath != null;
        SetAction("PluginBuild", source ? "plugins.unavailable" : "plugins.reason.noSource", source && _services != null);
        SetAction("PluginEdit", "plugins.reason.noSource", source);
        SetAction("PluginDefaultEditor", "plugins.reason.noSource", source);
        C<Button>("PluginCancel").IsEnabled = _busy && !reviewOpen;
        C<Button>("PluginCancel").IsVisible = _busy && !reviewOpen;
        SetAction("PluginProbe", "plugins.reason.noTest", _tested is { } tested && IsCurrentTestedCandidate(tested) && tested.Description.Sources.Count > 0);
        C<TextBox>("PluginProbeUrl").IsEnabled = !_busy;
        SetAction("PluginEditorSave", "plugins.reason.noEditorStore", SaveEditorOptionsAsync != null);
        ShowReasons("PluginSourceReasons", "PluginBuild", "PluginEdit", "PluginDefaultEditor", "PluginProbe", "PluginEditorSave");
        C<StackPanel>("PluginSettings").IsEnabled = !_busy;
    }
    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || _busy || _services == null || CurrentDraft is not { } draft || Selected is not { } entry || entry.PluginId == null) return;
        PluginConfiguration configuration = draft.Configuration(out IReadOnlyList<PluginConfigurationFieldError> errors);
        ShowFieldErrors(errors, focusFirstError: true);
        if (errors.Count > 0) return;
        PluginArtifact? artifact = entry.ConfigurationArtifact;
        if (artifact == null) { ErrorLocalized("plugins.noArtifact"); FocusFirstSettingError(); return; }
        (long generation, CancellationToken token) = BeginOperation();
        bool focusSaveError = false, revealBuildSelection = false;
        try
        {
            await using (IPluginWorkerClient worker = await _services.StartTestWorkerAsync(artifact, configuration, token))
            {
                errors = await ValidateInWorkerAsync(worker, configuration, token);
                if (!Current(generation)) return;
                ShowFieldErrors(errors);
                if (errors.Count > 0) { focusSaveError = true; return; }
            }
            PluginProfileSnapshot saved = await _services.ProfileStore.SaveConfigurationAsync(entry.PluginId, draft.CandidateId, configuration, draft.ExpectedRevision, token);
            if (!Current(generation)) return;
            PluginActivationSnapshot activation = saved.Plugins.Single(plugin => plugin.PluginId == entry.PluginId);
            revealBuildSelection = (entry with { Activation = activation }).SavedBuildDraft is not null;
            _drafts.Remove(entry.Key); _renderedKey = null; _savedNoticeKey = revealBuildSelection ? null : entry.Key;
            await RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (Current(generation)) { ErrorFailure("plugins.error.save", exception); focusSaveError = true; } }
        finally
        {
            FinishOperation(generation);
            // The test worker's asynchronous disposal must finish before focusing enabled controls.
            if (focusSaveError && !_ended && generation == _operationGeneration && ReferenceEquals(CurrentDraft, draft))
                FocusFirstSettingError();
            if (revealBuildSelection && !_ended && generation == _operationGeneration && _selectedKey == entry.Key &&
                C<Button>("PluginSelectCandidate") is { IsEffectivelyVisible: true, IsEnabled: true } selectBuild)
            {
                selectBuild.BringIntoView();
                selectBuild.Focus();
            }
        }
    }
    private void ShowFieldErrors(IReadOnlyList<PluginConfigurationFieldError> errors, bool focusFirstError = false)
    {
        foreach ((string scope, PluginSettingsEditor editor) in _editors)
        {
            IReadOnlyList<FieldValidationError> scoped = PluginConfigurationFieldError.ForScope(errors, scope);
            editor.ShowErrors(scoped);
        }
        if (errors.Count == 0) Error(null);
        else ErrorFailure("plugins.error.fields", string.Join(Environment.NewLine, errors.Select(error => error.Error.Message)));
        if (focusFirstError && errors.Count > 0) FocusFirstSettingError();
    }
    private static async Task<IReadOnlyList<PluginConfigurationFieldError>> ValidateInWorkerAsync(IPluginWorkerClient worker,
        PluginConfiguration configuration, CancellationToken token)
    {
        IReadOnlyList<FieldValidationError> pluginErrors = await worker.ValidateSettingsAsync(new(SettingScope.Plugin, configuration.PluginSettings), token);
        List<PluginConfigurationFieldError> errors = new(pluginErrors.Select(error => new PluginConfigurationFieldError(string.Empty, error)));
        foreach ((string source, SettingsSnapshot settings) in configuration.SourceSettings)
        {
            IReadOnlyList<FieldValidationError> found = await worker.ValidateSettingsAsync(new(SettingScope.Source, settings, SourceId: source), token);
            errors.AddRange(found.Select(error => new PluginConfigurationFieldError(source, error)));
        }
        return errors;
    }
    private async void OnBuildClick(object? sender, RoutedEventArgs e) => await BuildAndTestAsync();
    internal async Task BuildAndTestAsync()
    {
        if (_ended || _busy || _services == null || Selected is not { SourcePath: { } sourcePath } entry) return;
        (long generation, CancellationToken token) = BeginOperation();
        _tested = null;
        RenderDiagnostics(Array.Empty<PluginBuildDiagnostic>(), entry);
        ToolState(entry.Key).Result = null;
        RenderPluginProbeResult();
        SetTestStatus("plugins.building");
        bool revealBuildErrors = false;
        try
        {
            PluginBuildResult result = await _services.Compiler.BuildAsync(sourcePath, cancellationToken: token);
            if (!Current(generation)) return;
            RenderDiagnostics(result.Diagnostics, entry);
            if (!result.Succeeded)
            { ErrorLocalized("plugins.buildFailed", result.Status); SetTestStatus("plugins.buildFailed", result.Status); revealBuildErrors = true; return; }
            SetTestStatus("plugins.testing", result.Artifact!.ExecutionRevision);
            PluginDescription description = await _services.DescribeTestAsync(result.Artifact, token);
            if (!Current(generation)) return;
            await _services.ProfileStore.ObserveCandidateAsync(new(entry.CandidateId!, description.PluginId, result.Artifact, description, CandidatePath: sourcePath), _catalog!.Profile.Revision, token);
            if (!Current(generation)) return;
            await RefreshAsync();
            if (!Current(generation)) return;
            PluginConfigurationDraft testDraft = new(entry with { Description = description, Activation = entry.Activation is { } previous ? previous with { Pinned = null } : null }, _catalog!.Profile.Revision);
            PluginConfiguration configuration = testDraft.Configuration(out IReadOnlyList<PluginConfigurationFieldError> errors);
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
        catch (Exception exception) { if (Current(generation)) { ErrorFailure("plugins.error.test", exception); SetTestStatus("plugins.testFailed"); } }
        finally
        {
            FinishOperation(generation);
            if (revealBuildErrors && !_ended && generation == _operationGeneration && _selectedKey == entry.Key)
                FocusPluginBuildDiagnostics();
        }
    }
    private void RenderDiagnostics(IReadOnlyList<PluginBuildDiagnostic> diagnostics, PluginCatalogEntry entry)
    {
        ToolState(entry.Key).Diagnostics = diagnostics.ToArray();
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
                    if (_ended || _selectedKey != entry.Key) return;
                    // The compiler reports the loader input. Never accept plugin-supplied executable strings.
                    try { await PluginEditorLauncher.OpenAsync(GetEditorOptions?.Invoke() ?? new(null, new[] { "{file}" }), file, diagnostic.Line ?? 1, diagnostic.Column ?? 1, _lifetime.Token); }
                    catch (Exception error) { if (!_ended && _selectedKey == entry.Key) ErrorLocalized("plugins.editorFallback", error.Message); }
                };
                panel.Children.Add(location);
            }
        }
        RefreshWorkViewPresentation();
    }
    private async void OnProbeClick(object? sender, RoutedEventArgs e) => await ProbeAsync();
    internal async Task ProbeAsync()
    {
        if (_ended || _busy || _services == null || _tested is not { } tested || !IsCurrentTestedCandidate(tested)) return;
        string url = C<TextBox>("PluginProbeUrl").Text?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("https" or "http"))
        { ErrorLocalized("plugins.invalidUrl"); return; }
        (long generation, CancellationToken token) = BeginOperation();
        SetTestStatus("plugins.probing");
        try
        {
            object result;
            await using (IPluginWorkerClient worker = await _services.StartTestWorkerAsync(tested.Artifact, tested.Configuration, token))
                result = await _services.ProbeAsync(tested.Description, url, worker, new("plugin-probe:" + tested.Key, generation), token);
            if (!Current(generation)) return;
            ToolState(tested.Key).Result = PluginProbePresentation.Capture(result);
            RenderPluginProbeResult();
            string formatted = C<TextBox>("PluginProbeResult").Text ?? string.Empty;
            // The probe result is the plugin's own output for the URL the user entered; it stays verbatim.
            if (result is ThreadFailure or CatalogFailure) Error(formatted);
            SetTestStatus("plugins.probeFinished");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (Current(generation)) ErrorFailure("plugins.error.probe", exception); }
        finally { FinishOperation(generation); }
    }
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
        catch (Exception exception) { if (!_ended && key == _selectedKey) ErrorLocalized("plugins.editorFallback", exception.Message); }
    }
    private async void OnEditorSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || _busy || SaveEditorOptionsAsync == null) return;
        PluginEditorOptions options = new(C<TextBox>("PluginEditorExecutable").Text?.Trim(),
            (C<TextBox>("PluginEditorArguments").Text ?? "{file}").Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'));
        string? key = _selectedKey;
        try { _ = PluginEditorLauncher.CreateStartInfo(options, Selected?.SourcePath ?? "plugin.cs"); await SaveEditorOptionsAsync(options); }
        catch (Exception exception) { if (!_ended && key == _selectedKey) ErrorFailure("plugins.error.editorSave", exception); }
    }
}
