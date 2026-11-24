using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Boards.Base;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan;

internal sealed class BoardArchiveWindow : Window
{
    private readonly Func<BoardArchiveLocation, CancellationToken, Task<IReadOnlyList<BoardArchiveThread>?>> _read;
    private readonly Func<BoardArchiveThread, CancellationToken, Task<BoardArchiveThreadDetails?>> _readDetails;
    private readonly Func<BoardArchiveThread, Task<DesktopAdmissionResult>> _add;
    private readonly ComboBox _boards = new() { Name = "BoardArchiveBoards", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _rows = new() { Name = "BoardArchiveThreads", SelectionMode = SelectionMode.Single };
    private readonly Button _refresh = new() { Name = "BoardArchiveRefresh" };
    private readonly Button _retry = new() { Name = "BoardArchiveRetry" };
    private readonly Button _open = new() { Name = "BoardArchiveOpen" };
    private readonly Button _watch = new() { Name = "BoardArchiveWatch", Classes = { "primary" } };
    private readonly Button _close = new() { Name = "BoardArchiveClose", Classes = { "subtle" } };
    private readonly TextBlock _status = new() { Name = "BoardArchiveStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _title = new() { Classes = { "section-title" }, MaxLines = 2, TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _openingPost = new() { MaxLines = 3, TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis };
    private CancellationTokenSource? _catalogRead;
    private CancellationTokenSource? _detailRead;
    private BoardArchiveThreadDetails? _details;
    private string? _statusKey;
    private object? _statusArgument;
    private bool _retryDetails;
    private bool _retryAvailable;
    private bool _adding;
    private bool _opened;
    private bool _closed;

    internal BoardArchiveWindow(IReadOnlyList<BoardArchiveLocation> boards,
        Func<BoardArchiveLocation, CancellationToken, Task<IReadOnlyList<BoardArchiveThread>?>> read,
        Func<BoardArchiveThread, CancellationToken, Task<BoardArchiveThreadDetails?>> readDetails,
        Func<BoardArchiveThread, Task<DesktopAdmissionResult>> add, Action<BoardArchiveThread> open)
    {
        _read = read;
        _readDetails = readDetails;
        _add = add;
        DialogWindow.Configure(this, DialogSize.L, modal: true);
        _boards.ItemsSource = boards;
        _boards.SelectedIndex = 0;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        header.Children.Add(_boards);
        Grid.SetColumn(_refresh, 1);
        header.Children.Add(_refresh);

        var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 8 };
        body.Children.Add(_rows);
        Grid.SetRow(_title, 1);
        body.Children.Add(_title);
        Grid.SetRow(_openingPost, 2);
        body.Children.Add(_openingPost);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        DialogWindow.Frame(this, DialogFrame.Build(header, new Border { Child = body, Classes = { "workspace-dialog-body" } },
            DialogFooter.Build(_watch, [_close, _retry, _open], _status, statusAbove: true), fillBody: true), modal: true);

        Opened += async (_, _) =>
        {
            if (_closed) return;
            if (!_opened)
            {
                _opened = true;
                Texts.Changed += OnTextsChanged;
            }
            ApplyTexts();
            RefreshControls();
            await LoadBoardAsync(focusList: true);
        };
        Closed += (_, _) => EndLifetime();
        _boards.SelectionChanged += async (_, _) => { if (_opened && !_closed) await LoadBoardAsync(); };
        _rows.SelectionChanged += async (_, _) =>
        {
            if (_closed) return;
            if (_rows.SelectedItem is BoardArchiveThread thread) await LoadThreadAsync(thread);
            else if (_catalogRead == null)
            {
                _detailRead?.Cancel();
                _details = null;
                SetStatus(null);
            }
        };
        _refresh.Click += async (_, _) => await LoadBoardAsync(focusList: true);
        _retry.Click += async (_, _) =>
        {
            if (_retryDetails && _rows.SelectedItem is BoardArchiveThread thread) await LoadThreadAsync(thread);
            else await LoadBoardAsync(focusList: true);
        };
        _open.Click += (_, _) =>
        {
            if (_details != null && !_adding && _rows.SelectedItem is BoardArchiveThread thread) open(thread);
        };
        _watch.Click += async (_, _) => await AddThreadAsync();
        _close.Click += (_, _) => Close();
        DialogEscape.Attach(this);
        ApplyTexts();
        RefreshControls();
    }

    internal void EndLifetime()
    {
        if (_closed) return;
        _closed = true;
        Texts.Changed -= OnTextsChanged;
        _catalogRead?.Cancel();
        _detailRead?.Cancel();
    }

    private async Task LoadBoardAsync(bool focusList = false)
    {
        if (_closed || _adding || _boards.SelectedItem is not BoardArchiveLocation board) return;
        _catalogRead?.Cancel();
        _detailRead?.Cancel();
        using var read = new CancellationTokenSource();
        _catalogRead = read;
        _rows.ItemsSource = null;
        _details = null;
        SetStatus("wizard.loading");
        try
        {
            IReadOnlyList<BoardArchiveThread>? threads = await _read(board, read.Token);
            if (read.IsCancellationRequested) return;
            _rows.ItemsSource = threads;
            SetStatus(threads == null ? "boardArchive.unavailable" : threads.Count == 0 ? "boardArchive.empty" : null);
            if (focusList && threads is { Count: > 0 }
                && (FocusManager?.GetFocusedElement() is null || _boards.IsKeyboardFocusWithin || _refresh.IsKeyboardFocusWithin))
                _rows.Focus(NavigationMethod.Tab);
        }
        catch (Exception error) { if (!read.IsCancellationRequested) ShowFailure(error, details: false); }
        finally
        {
            if (ReferenceEquals(_catalogRead, read))
            {
                _catalogRead = null;
                if (!_closed) RefreshControls();
            }
        }
    }

    private async Task LoadThreadAsync(BoardArchiveThread thread)
    {
        _detailRead?.Cancel();
        using var read = new CancellationTokenSource();
        _detailRead = read;
        _details = null;
        SetStatus("boardArchive.loadingThread");
        try
        {
            BoardArchiveThreadDetails? details = await _readDetails(thread, read.Token);
            if (read.IsCancellationRequested) return;
            _details = details;
            SetStatus(details == null ? "boardArchive.gone" : details.AlreadyWatching ? "admission.alreadyPresent"
                : details.State == SourceThreadState.Archived ? "sources.archived" : null);
        }
        catch (Exception error) { if (!read.IsCancellationRequested) ShowFailure(error, details: true); }
        finally
        {
            if (ReferenceEquals(_detailRead, read)) _detailRead = null;
        }
    }

    private async Task AddThreadAsync()
    {
        if (_closed || _adding || _details is not { AlreadyWatching: false } details
            || _rows.SelectedItem is not BoardArchiveThread thread) return;
        _adding = true;
        SetStatus("boardArchive.adding");
        try
        {
            DesktopAdmissionResult result = await _add(thread);
            if (_closed) return;
            if (result.Status is AdmissionStatus.Added or AdmissionStatus.AlreadyPresent)
            {
                _details = details with { AlreadyWatching = true };
                SetStatus(result.Status == AdmissionStatus.Added ? "admission.added" : "admission.alreadyPresent");
            }
            else SetStatus("boardArchive.failed");
        }
        catch (Exception) { if (!_closed) SetStatus("boardArchive.failed"); }
        finally
        {
            _adding = false;
            if (!_closed)
            {
                RefreshControls();
                if (_details?.AlreadyWatching == true && FocusManager?.GetFocusedElement() is null) _rows.Focus(NavigationMethod.Tab);
            }
        }
    }

    private void ShowFailure(Exception error, bool details)
    {
        _retryAvailable = true;
        _retryDetails = details;
        if (error is SourceCheckDeferredException deferred)
            SetStatus("boardArchive.wait", deferred.RetryAt.ToLocalTime(), keepRetry: true);
        else if (error is BoardArchivePausedException)
            SetStatus("work.paused", keepRetry: true);
        else if (error is PluginWorkerException { FailureInfo.HttpStatusCode: HttpStatusCode.TooManyRequests })
            SetStatus("request.cooldown", keepRetry: true);
        else SetStatus("boardArchive.failed", keepRetry: true);
    }

    private void SetStatus(string? key, object? argument = null, bool keepRetry = false)
    {
        _statusKey = key;
        _statusArgument = argument;
        if (!keepRetry) _retryAvailable = false;
        RefreshControls();
    }

    private void RefreshControls()
    {
        _status.Text = _statusKey == null ? string.Empty : _statusArgument == null ? Texts.T(_statusKey) : Texts.T(_statusKey, _statusArgument);
        _status.IsVisible = _statusKey != null;
        _title.Text = _details?.Title;
        _title.IsVisible = !string.IsNullOrWhiteSpace(_title.Text);
        _openingPost.Text = _details?.OpeningPost;
        _openingPost.IsVisible = !string.IsNullOrWhiteSpace(_openingPost.Text);
        _boards.IsEnabled = !_adding;
        _rows.IsEnabled = !_adding && _rows.ItemsSource != null;
        _refresh.IsEnabled = !_adding && _catalogRead == null;
        _retry.IsVisible = _retryAvailable;
        _open.IsEnabled = !_adding && _details != null;
        _watch.IsEnabled = !_adding && _details is { AlreadyWatching: false };
    }

    private void OnTextsChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed)
        {
            ApplyTexts();
            RefreshControls();
        }
    });

    private void ApplyTexts()
    {
        Title = Texts.T("boardArchive.title");
        AutomationProperties.SetName(_boards, Texts.T("column.board"));
        AutomationProperties.SetName(_rows, Texts.T("boardArchive.threads"));
        _refresh.Content = Texts.T("command.archive.refresh");
        _retry.Content = Texts.T("action.retry");
        _open.Content = Texts.T("command.location.open-source");
        _watch.Content = Texts.T("boardArchive.watch");
        _close.Content = Texts.T("action.close");
    }
}
