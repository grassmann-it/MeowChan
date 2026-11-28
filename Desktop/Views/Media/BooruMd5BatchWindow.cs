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
using MeowChan.Booru;
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
    private readonly BooruWorkspaceContent _content;
    private readonly ObservableCollection<BooruMd5Match> _matches = [];
    private readonly ListBox _results;
    private readonly TextBox _details;
    private readonly CheckBox _optIn;
    private readonly Button _runButton;
    private readonly Button _stopButton;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Classes = { "workspace-meta" } };
    private readonly TextBlock _progress = new() { TextWrapping = TextWrapping.Wrap, Classes = { "workspace-meta" } };
    private CancellationTokenSource? _run;
    private bool _saving;
    private bool _started;
    private bool _closed;
    private bool _enabled;

    private BooruContentFilter? CurrentFilter => _content.Snapshot is { HasChoice: true } snapshot ? snapshot.Filter : null;

    internal BooruMd5BatchWindow(BooruMd5Batch batch, BooruWorkspaceContent content, bool enabled, Func<bool, Task> saveOptIn,
        CancellationToken lifetime)
    {
        _batch = batch;
        _lifetime = lifetime;
        _content = content;
        _saveOptIn = saveOptIn;
        _enabled = enabled;
        Title = Texts.T(Key + "title");
        DialogWindow.Configure(this, DialogSize.M, modal: true);
        _optIn = new CheckBox
        {
            Content = new TextBlock { Text = Texts.T(Key + "optIn"), TextWrapping = TextWrapping.Wrap },
            IsChecked = enabled
        };
        AutomationProperties.SetAutomationId(_optIn, Key + "optIn");
        AutomationProperties.SetName(_optIn, Texts.T(Key + "optIn"));
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(_progress, Key + "progress");
        _optIn.Click += async (_, _) => await SaveOptInAsync(_optIn.IsChecked == true);

        _runButton = new Button { Classes = { "primary" } };
        AutomationProperties.SetAutomationId(_runButton, Key + "run");
        _runButton.Click += async (_, _) => await RunBatchAsync();
        _stopButton = new Button { Content = Texts.T(Key + "stop") };
        AutomationProperties.SetAutomationId(_stopButton, Key + "stop");
        _stopButton.Click += (_, _) => _run?.Cancel();
        Button close = new() { Content = Texts.T("action.close") };
        close.Click += (_, _) => Close();
        Opened += (_, _) =>
        {
            _content.Changed += OnContentChanged;
            OnContentChanged();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _content.Changed -= OnContentChanged;
            _run?.Cancel();
        };
        Closing += (_, e) => e.Cancel = _saving;
        DialogEscape.Attach(this, tryCancelLocal: () =>
        {
            if (_run is not { IsCancellationRequested: false } run) return false;
            run.Cancel();
            return true;
        }, isBusy: () => _saving);

        _results = new() { ItemsSource = _matches };
        AutomationProperties.SetName(_results, Texts.T(Key + "matches"));
        AutomationProperties.SetAutomationId(_results, Key + "matches");
        _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(_details, Texts.T("booru.ui.tags.label"));
        AutomationProperties.SetAutomationId(_details, Key + "details");
        _results.SelectionChanged += (_, _) => ShowSelection();
        Grid body = new() { RowDefinitions = new RowDefinitions("*,Auto,*"), RowSpacing = 8 };
        body.Children.Add(_results);
        TextBlock tagsLabel = new() { Text = Texts.T("booru.ui.tags.label"), Classes = { "section-title" } };
        Grid.SetRow(tagsLabel, 1);
        body.Children.Add(tagsLabel);
        Grid.SetRow(_details, 2);
        body.Children.Add(_details);
        StackPanel header = new() { Spacing = 8, Children = { _optIn, _progress } };
        DialogWindow.Frame(this, DialogFrame.Build(header, body,
            DialogFooter.Build(_runButton, [close, _stopButton], _status), fillBody: true), modal: true);
        _status.Text = _content.HasChoice ? string.Empty : Texts.T(Key + "contentRequired");
        Render();
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
            if (_content.HasChoice) _status.Text = string.Empty;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _optIn.IsChecked = _enabled;
            _status.Text = Texts.T(Key + "saveFailed");
        }
        finally { _saving = false; Render(); }
    }

    private async Task RunBatchAsync()
    {
        if (!_runButton.IsEnabled || _run is not null || _closed) return;
        using CancellationTokenSource run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _run = run;
        _started = true;
        _status.Text = string.Empty;
        Render();
        try
        {
            BooruCallStatus outcome = await _batch.RunAsync(ShowProgress, run.Token);
            if (!_closed) _status.Text = outcome switch
            {
                BooruCallStatus.Ok => Texts.T(Key + "finished"),
                _ when _batch.RetryAt is { } until => Texts.T("booru.ui.status.cooldown", BooruText.Time(until)),
                _ => Texts.T(Key + "interrupted")
            };
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            if (!_closed) _status.Text = Texts.T(Key + "paused");
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
        BooruContentFilter? filter = CurrentFilter;
        foreach (BooruMd5Match match in matches)
            if (BooruMd5Batch.IsAllowed(match.Post, filter)) _matches.Add(match);
        Render();
    }

    private void OnContentChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnContentChanged);
            return;
        }
        if (_closed) return;
        RemoveBlockedMatches(CurrentFilter);
        Render();
    }

    private void ShowSelection()
    {
        _details.Text = string.Empty;
        if (_closed || _results.SelectedItem is not BooruMd5Match match) return;
        BooruContentFilter? filter = _batch.ReadContentFilter();
        RemoveBlockedMatches(filter);
        Render();
        if (ReferenceEquals(_results.SelectedItem, match) && BooruMd5Batch.IsAllowed(match.Post, filter)
            && BooruPostLinks.TryCanonical(match.Post, out string url))
            _details.Text = url + Environment.NewLine + MediaSourceMenu.TagText(match.Post.Tags.Select(tag => tag.Name));
    }

    private void RemoveBlockedMatches(BooruContentFilter? filter)
    {
        if (_results.SelectedItem is BooruMd5Match selected && !BooruMd5Batch.IsAllowed(selected.Post, filter))
            _results.SelectedIndex = -1;
        for (int index = _matches.Count - 1; index >= 0; index--)
            if (!BooruMd5Batch.IsAllowed(_matches[index].Post, filter))
                _matches.RemoveAt(index);
    }

    private void Render()
    {
        bool running = _run is not null;
        _optIn.IsEnabled = !running && !_saving;
        _runButton.Content = Texts.T(Key + (_started ? "resume" : "start"));
        _runButton.IsEnabled = _enabled && _content.HasChoice && !running && !_saving && _batch.Completed < _batch.Total;
        _stopButton.IsEnabled = running;
        _progress.Text = Texts.T(Key + "progress", _batch.Completed, _batch.Total, _matches.Count, _batch.MissingHash);
    }
}
