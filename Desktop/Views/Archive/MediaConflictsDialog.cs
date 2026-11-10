using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// Lists recorded media alternatives (same place, different original bytes) and lets the user keep
/// or discard each one. The active value is never changed here; every decision is explicit, and
/// discarding needs a second confirmation that names the size.
/// </summary>
internal sealed class MediaConflictsDialog : Window
{
    private const int PageSize = 100;
    private readonly Func<int, int, CancellationToken, Task<MediaConflictPage>> _list;
    private readonly Func<string, MediaConflictUserAction, CancellationToken, Task<MediaConflictActionOutcome>> _decide;
    private readonly WindowChromeBar _chrome = new();
    private readonly TextBlock _heading = new() { Name = "MediaConflictsHeading" };
    private readonly TextBlock _status = new() { Name = "MediaConflictsStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly StackPanel _rows = new() { Name = "MediaConflictsRows", Spacing = 8 };
    private readonly Button _more = new() { Name = "MediaConflictsMore" };
    private readonly Button _close = new() { Name = "MediaConflictsClose" };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<MediaConflictRow> _loaded = new();
    private string? _confirming;
    private string? _confirmingPromote;
    private int _total;
    private bool _busy;

    internal MediaConflictsDialog(Func<int, int, CancellationToken, Task<MediaConflictPage>> list,
        Func<string, MediaConflictUserAction, CancellationToken, Task<MediaConflictActionOutcome>> decide)
    {
        _list = list ?? throw new ArgumentNullException(nameof(list));
        _decide = decide ?? throw new ArgumentNullException(nameof(decide));
        Name = "MediaConflictsDialog";
        Width = 760; Height = 560; MinWidth = 480; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 48;
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        _heading.Classes.Add("section-title");
        _status.Classes.Add("workspace-meta");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _more.Click += async (_, _) => await LoadAsync(append: true);
        _close.Click += (_, _) => Close();
        StackPanel body = new() { Spacing = 12, Margin = new Thickness(16) };
        body.Children.Add(_heading); body.Children.Add(_status); body.Children.Add(_rows); body.Children.Add(_more);
        WrapPanel actions = new() { ItemSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_close);
        Border footer = new() { Child = actions, Margin = new Thickness(16, 0, 16, 16) };
        footer.Classes.Add("workspace-dialog-footer");
        Grid layout = new() { RowDefinitions = new RowDefinitions("48,*,Auto") };
        layout.Children.Add(_chrome);
        ScrollViewer scroll = new() { Content = body,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        layout.Children.Add(scroll); Grid.SetRow(scroll, 1);
        layout.Children.Add(footer); Grid.SetRow(footer, 2);
        Content = layout;
        Title = Texts.T("transfer.conflicts.title");
        Opened += async (_, _) => { _chrome.Attach(this); await LoadAsync(append: false); };
        Closed += (_, _) => { Texts.Changed -= OnLanguageChanged; _lifetime.Cancel(); _lifetime.Dispose(); };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (_confirming != null || _confirmingPromote != null) { _confirming = null; _confirmingPromote = null; Render(); } else Close();
        }, RoutingStrategies.Tunnel);
        Texts.Changed += OnLanguageChanged;
        Render();
    }

    internal StackPanel RowsForTests => _rows;
    internal TextBlock StatusForTests => _status;
    internal Task LoadForTestsAsync() => LoadAsync(append: false);

    private void OnLanguageChanged(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Render);

    private async Task LoadAsync(bool append)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            int offset = append ? _loaded.Count : 0;
            MediaConflictPage page = await _list(offset, PageSize, _lifetime.Token);
            if (!append) _loaded.Clear();
            _loaded.AddRange(page.Rows);
            _total = page.Total;
            _status.Text = string.Empty;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { _status.Text = Texts.T("transfer.conflicts.error.read"); }
        finally { _busy = false; }
        Render();
    }

    private async Task DecideAsync(MediaConflictRow row, MediaConflictUserAction action)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            MediaConflictActionOutcome outcome = await _decide(row.Id, action, _lifetime.Token);
            _status.Text = outcome is MediaConflictActionOutcome.Applied or MediaConflictActionOutcome.AlreadyApplied
                ? string.Empty : Texts.T(outcome == MediaConflictActionOutcome.Unavailable
                    ? "transfer.conflicts.error.promoteUnavailable" : "transfer.conflicts.error.action");
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { _status.Text = Texts.T("transfer.conflicts.error.action"); }
        finally { _busy = false; _confirming = null; _confirmingPromote = null; }
        await LoadAsync(append: false);
    }

    private void Render()
    {
        _heading.Text = Texts.T("transfer.conflicts.title");
        Title = _heading.Text;
        _close.Content = Texts.T("transfer.import.close");
        _more.Content = Texts.T("transfer.conflicts.more", _loaded.Count, _total);
        _more.IsVisible = _loaded.Count < _total;
        _rows.Children.Clear();
        if (_loaded.Count == 0 && string.IsNullOrEmpty(_status.Text)) _status.Text = Texts.T("transfer.conflicts.empty");
        else if (_loaded.Count > 0 && _status.Text == Texts.T("transfer.conflicts.empty")) _status.Text = string.Empty;
        foreach (MediaConflictRow row in _loaded) _rows.Children.Add(BuildRow(row));
    }

    private Control BuildRow(MediaConflictRow row)
    {
        TextBlock text = new()
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = string.Format(Texts.Culture, Texts.T("transfer.conflicts.row"), row.Resource.Source, row.Resource.Board,
                row.Resource.Thread, row.ActiveBytes, row.CandidateBytes, StatusLabel(row.Status),
                row.CandidateSha256[..12])
        };
        AutomationProperties.SetName(text, text.Text);
        WrapPanel actions = new() { ItemSpacing = 8, LineSpacing = 8 };
        bool open = row.Status is MediaConflictUserStatus.Open or MediaConflictUserStatus.Kept;
        Button keep = new() { Name = "MediaConflictKeep", Content = Texts.T("transfer.conflicts.keep"),
            IsEnabled = row.Status == MediaConflictUserStatus.Open };
        keep.Click += async (_, _) => await DecideAsync(row, MediaConflictUserAction.Keep);
        bool confirm = _confirming == row.Id;
        Button discard = new() { Name = "MediaConflictDiscard", IsEnabled = open,
            Content = confirm ? Texts.T("transfer.conflicts.confirmDiscard", row.CandidateBytes) : Texts.T("transfer.conflicts.discard") };
        discard.Click += async (_, _) =>
        {
            if (_confirming == row.Id) await DecideAsync(row, MediaConflictUserAction.Discard);
            else { _confirmingPromote = null; _confirming = row.Id; Render(); }
        };
        // Promote needs its own confirmation: it changes which bytes the slot serves. The old active
        // version stays referenced until a later cleanup, so the action is not destructive by itself.
        bool confirmPromote = _confirmingPromote == row.Id;
        Button promote = new() { Name = "MediaConflictPromote", IsEnabled = open,
            Content = confirmPromote ? Texts.T("transfer.conflicts.confirmPromote", row.CandidateBytes) : Texts.T("transfer.conflicts.promote") };
        promote.Click += async (_, _) =>
        {
            if (_confirmingPromote == row.Id) await DecideAsync(row, MediaConflictUserAction.Promote);
            else { _confirming = null; _confirmingPromote = row.Id; Render(); }
        };
        actions.Children.Add(keep); actions.Children.Add(promote); actions.Children.Add(discard);
        StackPanel panel = new() { Spacing = 4 };
        panel.Children.Add(text); panel.Children.Add(actions);
        Border border = new() { Child = panel, Padding = new Thickness(8), Name = "MediaConflictRow" };
        border.Classes.Add("workspace-card");
        return border;
    }

    private static string StatusLabel(MediaConflictUserStatus status) => Texts.T(status switch
    {
        MediaConflictUserStatus.Kept => "transfer.conflicts.status.kept",
        MediaConflictUserStatus.Discarded => "transfer.conflicts.status.discarded",
        MediaConflictUserStatus.Promoted => "transfer.conflicts.status.promoted",
        _ => "transfer.conflicts.status.open"
    });
}
