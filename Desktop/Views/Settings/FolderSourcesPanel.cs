using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.FolderSources;

namespace MeowChan.Desktop;

/// <summary>What the media folders group of the settings page needs from the host.</summary>
internal sealed record FolderSourcePanelServices(
    Func<IReadOnlyList<FolderSourceView>> List,
    Func<FolderSourceActivity> Activity,
    Func<FolderSourceError> Blocked,
    FolderSourceDialogServices Dialog,
    Func<Guid, Task> Rescan,
    Func<Guid, Task> Remove,
    Func<bool> GetPaused,
    Action<bool> SetPaused);

/// <summary>
/// The list of the "Media folders" group in Settings › Storage and limits (PR3; it was a section of the Sources page until
/// 14.11.2026): every read-only folder source with its counts, Rescan, Rename and Remove (asked inline: the folder and its
/// files stay untouched), "Add folder…", and one line about scanning and hashing with a pause switch for background hashing.
/// The heading and the explanation belong to the settings page (they are found by its search). Nothing here touches the
/// folders; the host only lists and reads them.
/// </summary>
internal sealed class FolderSourcesPanel : UserControl
{
    private readonly FolderSourcePanelServices _services;
    private readonly TextBlock _status = new() { Name = "FolderSourcesStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _blocked = new() { Name = "FolderSourcesBlocked", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _empty = new() { Name = "FolderSourcesEmpty", TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _rows = new() { Name = "FolderSourcesRows", Spacing = 8 };
    private readonly Button _add = new() { Name = "FolderSourcesAdd" };
    private readonly Button _pause = new() { Name = "FolderSourcesPause" };
    private readonly TextBlock _error = new() { Name = "FolderSourcesError", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private Guid? _confirmRemove;
    private bool _busy, _refreshing, _refreshAgain;
    private IReadOnlyList<FolderSourceView> _views = Array.Empty<FolderSourceView>();
    private FolderSourceActivity? _activity;
    private FolderSourceError _blockedError;

    internal StackPanel Rows => _rows;
    internal Button AddButton => _add;
    internal Button PauseButton => _pause;
    internal TextBlock StatusText => _status;
    /// <summary>Opens the add/rename dialog; the shell supplies the owner window, tests a fake.</summary>
    internal Func<FolderSourceDialog, Task>? ShowDialogAsync { get; set; }

    internal FolderSourcesPanel(FolderSourcePanelServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Name = "FolderSourcesPanel";
        _status.Classes.Add("workspace-meta");
        _blocked.Classes.Add("workspace-error");
        _empty.Classes.Add("muted");
        _error.Classes.Add("workspace-error");
        _pause.Classes.Add("subtle");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        WrapPanel toolbar = new() { ItemSpacing = 8, LineSpacing = 4, Children = { _add, _pause } };
        toolbar.Classes.Add("workspace-toolbar");
        Content = new StackPanel { Spacing = 8, Children = { _blocked, _status, _rows, _empty, toolbar, _error } };
        CommandBindings.EnsureIconBeforeText(_add, "folder"); // I1b: the text stays the label; the icon is drawn before it.
        _add.Click += async (_, _) => await AddAsync();
        _pause.Click += (_, _) =>
        {
            try { _services.SetPaused(!_services.GetPaused()); }
            catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException) { }
            Refresh();
        };
        Texts.Changed += OnLanguageChanged;
        DetachedFromVisualTree += (_, _) => Texts.Changed -= OnLanguageChanged;
        AttachedToVisualTree += (_, _) => { Texts.Changed -= OnLanguageChanged; Texts.Changed += OnLanguageChanged; Refresh(); };
        Render();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Render);

    /// <summary>Reads the folder sources and their activity off the UI thread, then shows them.</summary>
    internal async void Refresh()
    {
        if (_refreshing) { _refreshAgain = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                (IReadOnlyList<FolderSourceView> views, FolderSourceActivity? activity, FolderSourceError blocked) = await Task.Run(() =>
                {
                    FolderSourceError problem = _services.Blocked();
                    if (problem != FolderSourceError.None) return ((IReadOnlyList<FolderSourceView>)Array.Empty<FolderSourceView>(), (FolderSourceActivity?)null, problem);
                    try { return (_services.List(), _services.Activity(), FolderSourceError.None); }
                    catch (FolderSourceException error) { return (Array.Empty<FolderSourceView>(), null, error.Error); }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException
                        or Microsoft.Data.Sqlite.SqliteException)
                    {
                        FolderSourceErrorText.Log(error, "list");
                        return (Array.Empty<FolderSourceView>(), null, FolderSourceError.CatalogDamaged);
                    }
                });
                (_views, _activity, _blockedError) = (views, activity, blocked);
                Render();
            }
            while (_refreshAgain);
        }
        finally { _refreshing = false; }
    }

    private void Render()
    {
        _add.Content = Texts.T("folderSource.section.add");
        AutomationProperties.SetName(_add, Texts.T("folderSource.section.add"));
        _blocked.IsVisible = _blockedError != FolderSourceError.None;
        _blocked.Text = _blocked.IsVisible ? Texts.T(FolderSourceDialog.ErrorKey(_blockedError)) : string.Empty;
        _add.IsEnabled = !_busy && _blockedError == FolderSourceError.None;
        bool paused = false;
        try { paused = _services.GetPaused(); } catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { }
        _pause.Content = Texts.T(paused ? "folderSource.section.resume" : "folderSource.section.pause");
        AutomationProperties.SetName(_pause, (string)_pause.Content!);
        _pause.IsVisible = _views.Count > 0;
        _status.Text = StatusLine(_activity, paused);
        _status.IsVisible = _status.Text.Length > 0;
        _empty.Text = Texts.T("folderSource.section.empty");
        _empty.IsVisible = _views.Count == 0 && _blockedError == FolderSourceError.None;
        _rows.Children.Clear();
        foreach (FolderSourceView view in _views) _rows.Children.Add(Row(view));
    }

    /// <summary>"Scanning …" while a scan runs, else "Hashing 12,340 of 100,000" while files are unhashed, else nothing.</summary>
    internal static string StatusLine(FolderSourceActivity? activity, bool paused)
    {
        if (activity == null) return string.Empty;
        if (activity.ScanningRoot != null)
            return Texts.T("folderSource.status.scanning", (activity.ScanProgress?.Files ?? 0).ToString("N0", Texts.Culture));
        if (activity.Files == 0 || activity.Hashed >= activity.Files) return activity.Files == 0 ? string.Empty : Texts.T("folderSource.status.hashedAll");
        string counts = Texts.T("folderSource.status.hashing", activity.Hashed.ToString("N0", Texts.Culture), activity.Files.ToString("N0", Texts.Culture));
        return paused ? counts + " · " + Texts.T("folderSource.status.paused") : counts;
    }

    private Control Row(FolderSourceView view)
    {
        FolderSourceRoot root = view.Root;
        string id = FolderSourceIdentity.Format(root.Id);
        TextBlock name = new() { Text = root.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        SelectableTextBlock path = new() { Text = root.Path, TextWrapping = TextWrapping.Wrap };
        path.Classes.Add("workspace-meta");
        TextBlock counts = new()
        {
            Name = "FolderSourceCounts_" + id, TextWrapping = TextWrapping.Wrap,
            Text = Texts.T("folderSource.row.counts", view.Counts.Files.ToString("N0", Texts.Culture),
                view.Counts.Hashed.ToString("N0", Texts.Culture), view.Counts.Missing.ToString("N0", Texts.Culture))
        };
        counts.Classes.Add("workspace-help");
        TextBlock scan = new() { TextWrapping = TextWrapping.Wrap, Text = view.Scanning ? Texts.T("folderSource.row.scanning") : ScanText(root) };
        scan.Classes.Add("workspace-help");
        Button rescan = new() { Name = "FolderSourceRescan_" + id, Content = Texts.T("folderSource.row.rescan"), IsEnabled = !_busy && !view.Scanning };
        Button rename = new() { Name = "FolderSourceRename_" + id, Content = Texts.T("folderSource.row.rename"), IsEnabled = !_busy };
        Button remove = new() { Name = "FolderSourceRemove_" + id, Content = Texts.T("folderSource.row.remove"), IsEnabled = !_busy && !view.Scanning };
        remove.Classes.Add("subtle");
        foreach (Button button in new[] { rescan, rename, remove }) AutomationProperties.SetName(button, (string)button.Content! + " · " + root.Name);
        rescan.Click += async (_, _) => await RunAsync(() => _services.Rescan(root.Id));
        rename.Click += async (_, _) => await RenameAsync(root);
        remove.Click += (_, _) => { _confirmRemove = root.Id; Render(); };
        StackPanel row = new()
        {
            Name = "FolderSourceRow_" + id, Spacing = 4,
            Children = { name, path, counts, scan, new WrapPanel { ItemSpacing = 8, LineSpacing = 4, Children = { rescan, rename, remove } } }
        };
        if (_confirmRemove == root.Id)
        {
            TextBlock ask = new() { Name = "FolderSourceRemoveAsk", Text = Texts.T("folderSource.remove.ask", root.Name), TextWrapping = TextWrapping.Wrap };
            Button yes = new() { Name = "FolderSourceRemoveConfirm", Content = Texts.T("folderSource.remove.confirm") };
            Button no = new() { Name = "FolderSourceRemoveKeep", Content = Texts.T("folderSource.remove.keep") };
            yes.Classes.Add("danger"); no.Classes.Add("subtle");
            yes.Click += async (_, _) => { _confirmRemove = null; await RunAsync(() => _services.Remove(root.Id)); };
            no.Click += (_, _) => { _confirmRemove = null; Render(); };
            row.Children.Add(new StackPanel { Spacing = 4, Children = { ask, new WrapPanel { ItemSpacing = 8, Children = { yes, no } } } });
            Dispatcher.UIThread.Post(() => no.Focus());
        }
        Border card = new() { Child = row, Padding = new Thickness(12), CornerRadius = new CornerRadius(5) };
        card.Classes.Add("workspace-card");
        return card;
    }

    private static string ScanText(FolderSourceRoot root) => root.LastScanStatus switch
    {
        null => Texts.T("folderSource.row.notScanned"),
        FolderSourceScanStatus.Complete => Texts.T("folderSource.row.scanned"),
        FolderSourceScanStatus.Incomplete => Texts.T("folderSource.row.incomplete"),
        FolderSourceScanStatus.LimitReached => Texts.T("folderSource.row.limit"),
        _ => Texts.T("folderSource.row.unavailable")
    };

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _error.IsVisible = false;
        Render();
        try { await action(); }
        catch (FolderSourceException error) { ShowError(FolderSourceDialog.ErrorKey(error.Error)); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException
            or InvalidOperationException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException)
        {
            // A passing read or write problem is not a damaged catalog: say which it is, log the codes (W3-15).
            FolderSourceErrorText.Log(error, "action");
            ShowError(FolderSourceErrorText.Key(error));
        }
        finally { _busy = false; Refresh(); }
    }

    private void ShowError(string key)
    {
        _error.Text = Texts.T(key);
        _error.IsVisible = true;
    }

    private async Task AddAsync()
    {
        if (_busy) return;
        FolderSourceDialog dialog = new(_services.Dialog);
        await Show(dialog);
        Refresh();
    }

    private async Task RenameAsync(FolderSourceRoot root)
    {
        if (_busy) return;
        FolderSourceDialog dialog = new(_services.Dialog, root);
        await Show(dialog);
        Refresh();
    }

    private Task Show(FolderSourceDialog dialog)
    {
        if (ShowDialogAsync is { } show) return show(dialog);
        return TopLevel.GetTopLevel(this) is Window owner ? dialog.ShowDialog(owner) : Task.CompletedTask;
    }
}
