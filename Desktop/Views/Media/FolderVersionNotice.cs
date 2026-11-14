using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.FolderSources;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>What the media page needs from the host to tell a changed folder file's story: find the earlier tagged version, copy its
/// tags to this one (the person asked for it), and take exactly that copy away again.</summary>
internal sealed record FolderVersionServices(Func<MediaLibraryEntry, FolderFileSuccession?> Find,
    Func<Guid, CancellationToken, Task<FolderTagCopyResult>> Copy, Func<FolderTagCopy, CancellationToken, Task<bool>> Undo);

/// <summary>
/// The hint in the details of a folder file that changed on disk after it was tagged: tags belong to the exact version of a file,
/// so they stayed with the previous version. "Copy tags from the previous version" asks once, copies only what this version lacks and
/// can be undone right away. It looks the file up in the background and stays invisible when there is nothing to say.
/// </summary>
internal sealed class FolderVersionNoticeControl : StackPanel
{
    private readonly MediaLibraryEntry _entry;
    private readonly FolderVersionServices _services;
    private readonly Action? _changed;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _note = new() { Name = "FolderVersionNote", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _copy = new() { Name = "FolderVersionCopy", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _confirm = new() { Name = "FolderVersionConfirm", TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
    private readonly WrapPanel _confirmRow = new() { Name = "FolderVersionConfirmRow", ItemSpacing = 8, LineSpacing = 4, IsVisible = false };
    private readonly Button _yes = new() { Name = "FolderVersionConfirmYes" };
    private readonly Button _no = new() { Name = "FolderVersionConfirmNo" };
    private readonly TextBlock _status = new() { Name = "FolderVersionStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
    private readonly Button _undo = new() { Name = "FolderVersionUndo", HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
    private FolderTagCopy? _done;
    private bool _busy, _found;

    internal FolderVersionNoticeControl(MediaLibraryEntry entry, FolderVersionServices services, Action? changed = null)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _changed = changed;
        Name = "FolderVersionNoticeHost";
        Spacing = 4;
        IsVisible = false;
        foreach (TextBlock text in new[] { _note, _confirm, _status }) text.Classes.Add("workspace-meta");
        _copy.Classes.Add("subtle"); _undo.Classes.Add("subtle"); _no.Classes.Add("subtle"); _yes.Classes.Add("primary");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _confirmRow.Children.Add(_yes); _confirmRow.Children.Add(_no);
        Children.Add(_note); Children.Add(_copy); Children.Add(_confirm); Children.Add(_confirmRow); Children.Add(_status); Children.Add(_undo);
        _copy.Click += (_, _) => Ask(true);
        _no.Click += (_, _) => Ask(false);
        _yes.Click += async (_, _) => await CopyAsync();
        _undo.Click += async (_, _) => await UndoAsync();
        DetachedFromVisualTree += (_, _) => _lifetime.Cancel();
        Texts.Changed += OnLanguageChanged;
        DetachedFromVisualTree += (_, _) => Texts.Changed -= OnLanguageChanged;
        ApplyTexts();
        _ = LookUpAsync();
    }

    internal bool Found => _found;
    internal TextBlock NoteText => _note;
    internal Button CopyButton => _copy;
    internal TextBlock ConfirmText => _confirm;
    internal Button ConfirmYesButton => _yes;
    internal Button ConfirmNoButton => _no;
    internal TextBlock StatusText => _status;
    internal Button UndoButton => _undo;
    /// <summary>Completes when the lookup is done (tests and smokes wait on it).</summary>
    internal Task Lookup { get; private set; } = Task.CompletedTask;

    private string? _statusKey;

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(ApplyTexts);

    private void ApplyTexts()
    {
        _note.Text = Texts.T("folderSource.version.note");
        _copy.Content = Texts.T("folderSource.version.copy");
        AutomationProperties.SetName(_copy, Texts.T("folderSource.version.copy"));
        _confirm.Text = Texts.T("folderSource.version.confirm");
        _yes.Content = Texts.T("folderSource.version.confirmYes");
        _no.Content = Texts.T("folderSource.version.confirmNo");
        _undo.Content = Texts.T("folderSource.version.undo");
        _status.Text = _statusKey == null ? string.Empty : Texts.T(_statusKey);
    }

    private Task LookUpAsync() => Lookup = LookUpCoreAsync();

    private async Task LookUpCoreAsync()
    {
        try
        {
            FolderFileSuccession? found = await Task.Run(() => _services.Find(_entry), _lifetime.Token);
            if (found == null || _lifetime.IsCancellationRequested) return;
            _found = true;
            IsVisible = true;
        }
        catch (OperationCanceledException) { }
    }

    private void Ask(bool asking)
    {
        if (_busy) return;
        _confirm.IsVisible = _confirmRow.IsVisible = asking;
        _copy.IsVisible = !asking;
        if (asking) _yes.Focus(); else _copy.Focus();
    }

    private void SetStatus(string? key)
    {
        _statusKey = key;
        _status.Text = key == null ? string.Empty : Texts.T(key);
        _status.IsVisible = key != null;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (Button button in new[] { _copy, _yes, _no, _undo }) button.IsEnabled = !busy;
    }

    /// <summary>Copies the tags once the person confirmed. The file is read once first (hashing), then written in one step.</summary>
    internal async Task CopyAsync()
    {
        if (_busy || _lifetime.IsCancellationRequested || _entry.FolderItemId is not { } id || !FolderSourceIdentity.TryParseId(id, out Guid item)) return;
        SetBusy(true);
        try
        {
            FolderTagCopyResult result = await _services.Copy(item, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            _confirm.IsVisible = _confirmRow.IsVisible = false;
            switch (result.Outcome)
            {
                case FolderTagCopyOutcome.Copied:
                    _done = result.Copy;
                    _note.IsVisible = false; _copy.IsVisible = false;
                    _undo.IsVisible = true;
                    SetStatus("folderSource.version.done");
                    _changed?.Invoke();
                    break;
                case FolderTagCopyOutcome.NothingToCopy:
                    _note.IsVisible = false; _copy.IsVisible = false;
                    SetStatus("folderSource.version.none");
                    break;
                default:
                    _copy.IsVisible = true;
                    SetStatus("folderSource.version.failed");
                    break;
            }
        }
        catch (OperationCanceledException) { }
        finally { SetBusy(false); }
    }

    /// <summary>Removes exactly the tags the last copy added.</summary>
    internal async Task UndoAsync()
    {
        if (_busy || _done is not { } copy || _lifetime.IsCancellationRequested) return;
        SetBusy(true);
        try
        {
            bool undone = await _services.Undo(copy, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            if (undone)
            {
                _done = null;
                _undo.IsVisible = false;
                _note.IsVisible = true; _copy.IsVisible = true;
                SetStatus("folderSource.version.undone");
                _changed?.Invoke();
            }
            else SetStatus("folderSource.version.undoFailed");
        }
        catch (OperationCanceledException) { }
        finally { SetBusy(false); }
    }
}
