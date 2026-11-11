using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Storage;

namespace MeowChan;

// Reader entry to the existing SHA-256 search dialog. The dialog searches one captured media
// inventory; it is offered only when the open thread has exactly one, so the scope is never narrower
// than what the button promises. Read-only: no file is opened, rehashed or downloaded.
public partial class ArchivePage
{
    private bool _hashSearchOpen;
    internal MediaHashSearchDialog? MediaHashSearchWindow { get; private set; }
    internal Task MediaHashSearchTask { get; private set; } = Task.CompletedTask;

    private string[] HashSearchDirectories => _thread?.MediaDirectories
        .DistinctBy(CanonicalMediaDirectory, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();

    private bool CanOpenHashSearch => !_ended && !_hashSearchOpen && !_tagManagerOpen && !IsFilenameSearchOpen
        && !_savedSearchDialogOpen && !IsReaderDialogOpen && HashSearchDirectories.Length == 1;

    private void OnHashSearchClick(object? sender, RoutedEventArgs e) => MediaHashSearchTask = OpenHashSearchAsync(sender as Control);

    internal async Task OpenHashSearchAsync(Control? origin = null)
    {
        if (!CanOpenHashSearch || TopLevel.GetTopLevel(this) is not Window owner
            || owner.OwnedWindows.Any(child => child.IsVisible)) return;
        string directory = HashSearchDirectories[0];
        _hashSearchOpen = true;
        UpdateHashSearchAction(); UpdateTagManagerActions();
        try
        {
            MediaAssetSnapshot snapshot = await _openMedia(directory, _lifetime.Token);
            if (_ended || !owner.IsVisible) return;
            MediaHashSearchDialog dialog = new(snapshot, _lifetime.Token)
            {
                Width = Math.Max(360, Math.Min(760, owner.ClientSize.Width - 32)),
                Height = Math.Max(360, Math.Min(640, owner.ClientSize.Height - 32))
            };
            MediaHashSearchWindow = dialog;
            using CancellationTokenRegistration ending = _lifetime.Token.Register(() =>
                Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) dialog.Close(); }));
            try { await dialog.ShowDialog(owner); }
            finally { await dialog.TerminateAsync(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_ended) Message(Texts.T("reader.hashSearch.failed"), true);
        }
        finally
        {
            MediaHashSearchWindow = null;
            _hashSearchOpen = false;
            if (!_ended)
            {
                UpdateHashSearchAction(); UpdateTagManagerActions();
                if (origin is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }) origin.Focus();
            }
        }
    }

    private void UpdateHashSearchAction()
    {
        Button button = Control<Button>("ReaderHashSearchOpen");
        string label = Texts.T("reader.hashSearch.open");
        if (!Equals(button.Content, label)) button.Content = label;
        ToolTip.SetTip(button, Texts.T(HashSearchDirectories.Length > 1 ? "reader.hashSearch.multiple" : "reader.hashSearch.hint"));
        button.IsEnabled = CanOpenHashSearch;
    }
}
