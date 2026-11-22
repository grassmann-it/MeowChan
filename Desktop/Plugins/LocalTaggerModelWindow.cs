using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginHost.Taggers;

namespace MeowChan.Desktop.Plugins;

internal sealed class LocalTaggerModelWindow : Window
{
    private readonly LocalTaggerModelStore _store;
    private readonly CancellationToken _pageLifetime;
    private readonly Button _choose = new();
    private readonly Button _import = new();
    private readonly Button _cancel = new();
    private readonly Button _close = new();
    private readonly TextBlock _availability = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _engine = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _limits = new() { TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock _candidate = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _space = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true };
    private LocalTaggerModelPackage? _installed;
    private LocalTaggerModelPreview? _preview;
    private CancellationTokenSource? _operation;
    private bool _installedChecked;
    private bool _installedInvalid;
    private bool _picking;
    private bool _closed;
    private string? _statusKey;

    internal LocalTaggerModelWindow(LocalTaggerModelStore store, CancellationToken pageLifetime)
    {
        _store = store;
        _pageLifetime = pageLifetime;
        DialogWindow.Configure(this, DialogSize.M, modal: true, heightToContent: true);
        AutomationProperties.SetLiveSetting(_availability, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _limits.Classes.Add("workspace-help");
        _import.Classes.Add("primary");
        StackPanel body = new()
        {
            Margin = new Thickness(WorkspaceMetrics.SectionInset, 0, WorkspaceMetrics.SectionInset, WorkspaceMetrics.SectionInset),
            Spacing = 12,
            Children = { _availability, _engine, _limits, _candidate, _space, _progress }
        };
        Border footer = DialogFooter.Build(_import, [_close, _cancel, _choose], _status, statusAbove: true);
        DialogWindow.Frame(this, DialogFrame.Build(header: null, body, footer), modal: true);
        DialogEscape.Attach(this, CancelOperation);
        _choose.Click += async (_, _) => await ChooseAsync();
        _import.Click += async (_, _) => await ImportAsync();
        _cancel.Click += (_, _) => CancelOperation();
        _close.Click += (_, _) => Close();
        Opened += async (_, _) => await CheckInstalledAsync();
        Texts.Changed += OnLanguageChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            Texts.Changed -= OnLanguageChanged;
            _operation?.Cancel();
        };
        Render();
    }

    private bool CancelOperation()
    {
        if (_operation is not { } operation) return false;
        operation.Cancel();
        return true;
    }

    private void OnLanguageChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(Render);

    private Task CheckInstalledAsync() => RunAsync("localTagger.checking", async token =>
    {
        try { _installed = await Task.Run(() => _store.VerifyInstalledAsync(token), token); }
        catch (Exception error) when (IsModelFailure(error)) { _installedInvalid = true; }
        _installedChecked = true;
    });

    private async Task ChooseAsync()
    {
        if (_operation != null || _picking) return;
        _picking = true;
        Render();
        string? path = null;
        try
        {
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Texts.T("localTagger.choose"),
                FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
                AllowMultiple = false
            });
            try
            {
                if (files.Count == 1)
                {
                    path = files[0].TryGetLocalPath();
                    if (path is null) _statusKey = "localTagger.readError";
                }
            }
            finally { foreach (IStorageFile file in files) file.Dispose(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            _statusKey = "localTagger.readError";
        }
        finally
        {
            _picking = false;
            Render();
        }
        if (_closed || path is null) return;
        _preview = null;
        await RunAsync("localTagger.checking", async token => _preview = await Task.Run(() => _store.PreviewAsync(path, token), token));
    }

    private Task ImportAsync()
    {
        if (_preview is not { } candidate || _operation != null || _picking) return Task.CompletedTask;
        return RunAsync("localTagger.importing", async token =>
        {
            _installed = await Task.Run(() => _store.ImportAsync(candidate, token), token);
            _installedChecked = true;
            _installedInvalid = false;
            _preview = null;
        });
    }

    private async Task RunAsync(string activity, Func<CancellationToken, Task> work)
    {
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetime);
        _operation = operation;
        _statusKey = activity;
        Render();
        try
        {
            await work(operation.Token);
            _statusKey = null;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { _statusKey = "localTagger.cancelled"; }
        catch (LocalTaggerModelException error)
        {
            _statusKey = error.Issue switch
            {
                LocalTaggerModelIssue.Limit => "localTagger.limitError",
                LocalTaggerModelIssue.FreeSpace => "localTagger.spaceError",
                _ => "localTagger.integrityError"
            };
        }
        catch (Exception error) when (error is JsonException or InvalidDataException) { _statusKey = "localTagger.manifestError"; }
        catch (Exception error) when (IsModelFailure(error)) { _statusKey = "localTagger.readError"; }
        finally
        {
            _operation = null;
            Render();
        }
    }

    private static bool IsModelFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or JsonException or InvalidDataException or NotSupportedException or InvalidOperationException;

    private void Render()
    {
        if (_closed) return;
        Title = Texts.T("localTagger.title");
        _choose.Content = Texts.T("localTagger.choose");
        _import.Content = Texts.T("localTagger.import");
        _cancel.Content = Texts.T("plugins.cancel");
        _close.Content = Texts.T("action.close");
        bool busy = _operation != null || _picking;
        _choose.IsEnabled = !busy && StorageProvider.CanOpen;
        _import.IsVisible = _preview != null;
        _import.IsEnabled = !busy;
        _cancel.IsVisible = _operation != null;
        _progress.IsVisible = _operation != null;
        _availability.Text = _installed is { } active
            ? Texts.T("localTagger.installed", Describe(active))
            : _installedInvalid ? Texts.T("localTagger.invalid") : Texts.T("localTagger.empty");
        _availability.IsVisible = _installedChecked;
        _engine.Text = Texts.T("localTagger.engineUnavailable");
        _limits.Text = Texts.T("localTagger.limits", SizeText.Format(_store.Settings.MaximumManifestBytes), SizeText.Format(_store.Settings.MaximumWeightsBytes));
        _candidate.IsVisible = _space.IsVisible = _preview != null;
        if (_preview is { } preview)
        {
            _candidate.Text = Describe(preview.Package);
            _space.Text = preview.FreeBytes is long free
                ? Texts.T("localTagger.space", SizeText.Format(preview.Package.RequiredBytes), SizeText.Format(free))
                : Texts.T("localTagger.spaceUnknown", SizeText.Format(preview.Package.RequiredBytes));
        }
        _status.Text = _statusKey is { } key ? Texts.T(key) : null;
        _status.IsVisible = _statusKey != null;
    }

    private static string Describe(LocalTaggerModelPackage package) => Texts.T("localTagger.modelInfo", package.Manifest.ModelId,
        package.Manifest.Publisher, package.Manifest.Revision, package.Manifest.License, SizeText.Format(package.RequiredBytes));
}
