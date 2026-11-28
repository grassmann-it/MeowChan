using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;

namespace MeowChan.Desktop.Booru;

internal sealed class BooruMd5BatchWindow : Window
{
    private const string Key = "booru.md5.batch.";
    private readonly BooruMd5Batch _batch;
    private readonly Func<bool, Task> _saveOptIn;
    private readonly CancellationToken _lifetime;
    private readonly bool _contentReady;
    private readonly ObservableCollection<BooruMd5Match> _matches = [];
    private readonly CheckBox _optIn;
    private readonly ListBox _results;
    private readonly Button _runButton;
    private readonly Button _stopButton;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Classes = { "workspace-meta" } };
    private readonly TextBlock _progress = new() { TextWrapping = TextWrapping.Wrap, Classes = { "workspace-meta" } };
    private CancellationTokenSource? _run;
    private bool _saving;
    private bool _started;
    private bool _closed;
    private bool _enabled;
    private string? _statusKey;
    private DateTimeOffset? _retryAt;

    internal BooruMd5BatchWindow(BooruMd5Batch batch, bool contentReady, bool enabled, Func<bool, Task> saveOptIn,
        CancellationToken lifetime)
    {
        _batch = batch;
        _lifetime = lifetime;
        _contentReady = contentReady;
        _saveOptIn = saveOptIn;
        _enabled = enabled;
        _statusKey = contentReady ? null : Key + "contentRequired";
        DialogWindow.Configure(this, DialogSize.M, modal: true);
        TextBlock optInLabel = new() { TextWrapping = TextWrapping.Wrap };
        _optIn = new CheckBox { Content = optInLabel, IsChecked = enabled };
        AutomationProperties.SetAutomationId(_optIn, Key + "optIn");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(_status, Key + "status");
        AutomationProperties.SetAutomationId(_progress, Key + "progress");
        _optIn.Click += async (_, _) => await SaveOptInAsync(_optIn.IsChecked == true);

        _runButton = new Button { Classes = { "primary" } };
        AutomationProperties.SetAutomationId(_runButton, Key + "run");
        _runButton.Click += async (_, _) => await RunBatchAsync();
        _stopButton = new Button();
        AutomationProperties.SetAutomationId(_stopButton, Key + "stop");
        _stopButton.Click += (_, _) => _run?.Cancel();
        Button close = new();
        close.Click += (_, _) => Close();
        Closing += (_, e) => e.Cancel = _saving;
        DialogEscape.Attach(this, tryCancelLocal: () =>
        {
            if (_run is not { IsCancellationRequested: false } run) return false;
            run.Cancel();
            return true;
        }, isBusy: () => _saving);

        _results = new ListBox { ItemsSource = _matches };
        AutomationProperties.SetAutomationId(_results, Key + "matches");
        TextBox details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(details, Key + "details");
        _results.SelectionChanged += (_, _) => details.Text = _results.SelectedItem is BooruMd5Match match
            && BooruPostLinks.TryCanonical(match.Post, out string url)
                ? url + Environment.NewLine + MediaSourceMenu.TagText(match.Post.Tags.Select(tag => tag.Name)) : string.Empty;
        Grid body = new() { RowDefinitions = new RowDefinitions("*,Auto,*"), RowSpacing = 8 };
        body.Children.Add(_results);
        TextBlock tagsLabel = new() { Classes = { "section-title" } };
        Grid.SetRow(tagsLabel, 1);
        body.Children.Add(tagsLabel);
        Grid.SetRow(details, 2);
        body.Children.Add(details);
        StackPanel header = new() { Spacing = 8, Children = { _optIn, _progress } };
        DialogWindow.Frame(this, DialogFrame.Build(header, body,
            DialogFooter.Build(_runButton, [close, _stopButton], _status), fillBody: true), modal: true);
        Opened += (_, _) =>
        {
            Texts.Changed -= OnLanguageChanged;
            Texts.Changed += OnLanguageChanged;
            Localize();
            (_runButton.IsEnabled ? (Control)_runButton : _optIn).Focus();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            Texts.Changed -= OnLanguageChanged;
            _run?.Cancel();
        };
        Localize();

        void OnLanguageChanged(object? sender, EventArgs e)
        {
            if (Dispatcher.UIThread.CheckAccess()) Localize();
            else Dispatcher.UIThread.Post(Localize);
        }

        void Localize()
        {
            if (_closed) return;
            Title = Texts.T(Key + "title");
            optInLabel.Text = Texts.T(Key + "optIn");
            AutomationProperties.SetName(_optIn, optInLabel.Text);
            _stopButton.Content = Texts.T(Key + "stop");
            close.Content = Texts.T("action.close");
            AutomationProperties.SetName(_results, Texts.T(Key + "matches"));
            tagsLabel.Text = Texts.T("booru.ui.tags.label");
            AutomationProperties.SetName(details, tagsLabel.Text);
            Render();
        }
    }

    private async Task SaveOptInAsync(bool enabled)
    {
        if (_saving || _run is not null || _closed) return;
        _saving = true;
        Render();
        try
        {
            await _saveOptIn(enabled);
            _enabled = enabled;
            _statusKey = _contentReady ? null : Key + "contentRequired";
            _retryAt = null;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _optIn.IsChecked = _enabled;
            _statusKey = Key + "saveFailed";
            _retryAt = null;
        }
        finally { _saving = false; Render(); }
    }

    private async Task RunBatchAsync()
    {
        if (!_runButton.IsEnabled || _run is not null || _closed) return;
        using CancellationTokenSource run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _run = run;
        _started = true;
        _statusKey = null;
        _retryAt = null;
        Render();
        try
        {
            BooruCallStatus outcome = await _batch.RunAsync(ShowProgress, run.Token);
            _retryAt = outcome == BooruCallStatus.Ok ? null : _batch.RetryAt;
            _statusKey = outcome switch
            {
                BooruCallStatus.Ok => Key + "finished",
                _ when _retryAt.HasValue => "booru.ui.status.cooldown",
                _ => Key + "interrupted"
            };
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            _statusKey = Key + "paused";
        }
        finally
        {
            _run = null;
            if (!_closed) Render();
        }
    }

    private void ShowProgress(IEnumerable<BooruMd5Match> matches)
    {
        if (_closed) return;
        bool first = _matches.Count == 0;
        foreach (BooruMd5Match match in matches) _matches.Add(match);
        if (first && _matches.Count > 0) _results.SelectedIndex = 0;
        Render();
    }

    private void Render()
    {
        bool running = _run is not null;
        _optIn.IsEnabled = !running && !_saving;
        _runButton.Content = Texts.T(Key + (_started ? "resume" : "start"));
        _runButton.IsEnabled = _enabled && _contentReady && !running && !_saving && _batch.Completed < _batch.Total;
        _stopButton.IsEnabled = running;
        _status.Text = _statusKey == null ? string.Empty
            : _retryAt is { } until ? Texts.T(_statusKey, BooruText.Time(until)) : Texts.T(_statusKey);
        _progress.Text = Texts.T(Key + "progress", _batch.Completed, _batch.Total, _matches.Count, _batch.MissingHash);
    }
}
