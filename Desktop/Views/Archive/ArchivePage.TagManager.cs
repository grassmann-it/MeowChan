using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan;

// Entry to the tag manager from the reader tag bar and the archive filter area. One modal at a
// time; when it closes the page re-reads the tag index so chips, rows and filter choices follow.
public partial class ArchivePage
{
    private Func<TagId, string, long, CancellationToken, Task<PrivateTagCommit>>? _renamePrivateTag;
    private Func<TagId, long, CancellationToken, Task<PrivateTagCommit>>? _deletePrivateTag;
    private Func<CancellationToken, Task<LocalMediaTagInspection?>>? _inspectLocalTagOrphans;
    private Func<IReadOnlyCollection<PrivateLocalMediaTagAssignment>, long, CancellationToken, Task<PrivateTagCommit>>? _removeLocalTagOrphans;
    private bool _tagManagerOpen;
    internal TagManagerDialog? TagManagerWindow { get; private set; }
    internal Task TagManagerTask { get; private set; } = Task.CompletedTask;

    private bool CanOpenTagManager => _available && !_ended && !_tagManagerOpen && !IsFilenameSearchOpen
        && !_savedSearchDialogOpen && !IsReaderDialogOpen && !_hashSearchOpen
        && _loadPrivateTags != null && _renamePrivateTag != null && _deletePrivateTag != null;

    private void OnManageTags(object? sender, RoutedEventArgs e) => TagManagerTask = OpenTagManagerAsync(sender as Control);

    internal async Task OpenTagManagerAsync(Control? origin = null)
    {
        if (!CanOpenTagManager || TopLevel.GetTopLevel(this) is not Window owner
            || owner.OwnedWindows.Any(child => child.IsVisible)) return;
        _tagManagerOpen = true;
        UpdateTagManagerActions(); UpdateHashSearchAction();
        try
        {
            TagManagerDialog dialog = new(new TagManagerSession(_loadPrivateTags!, _renamePrivateTag!, _deletePrivateTag!,
                _inspectLocalTagOrphans, _removeLocalTagOrphans))
            {
                Width = Math.Max(340, Math.Min(520, owner.ClientSize.Width - 32)),
                Height = Math.Max(360, Math.Min(560, owner.ClientSize.Height - 32))
            };
            TagManagerWindow = dialog;
            using CancellationTokenRegistration ending = _lifetime.Token.Register(() =>
                Dispatcher.UIThread.Post(dialog.CloseForLifetime));
            await dialog.ShowDialog(owner);
        }
        catch (Exception error) { if (!_ended) Message(error.Message); }
        finally
        {
            TagManagerWindow = null;
            _tagManagerOpen = false;
            if (!_ended)
            {
                // Renames and deletions happened in the host; show them everywhere on this page.
                ReloadPrivateTags();
                UpdatePrivateTagActions(); UpdateHashSearchAction();
                if (origin is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }) origin.Focus();
            }
        }
    }

    private void UpdateTagManagerActions()
    {
        foreach (string name in new[] { "ReaderTagManage", "ArchiveTagManage" })
        {
            Button button = Control<Button>(name);
            string label = Texts.T("tags.manage.open");
            if (!Equals(button.Content, label)) button.Content = label;
            button.IsEnabled = CanOpenTagManager;
        }
    }
}
