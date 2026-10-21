using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Gallery;

namespace MeowChan;

public partial class ArchivePage
{
    internal ContextMenu? ArchiveMediaMenu { get; private set; }
    internal Func<GalleryEntry, bool>? IsArchiveFavorite { get; set; }
    internal Func<GalleryEntry, Task>? ToggleArchiveFavoriteAsync { get; set; }

    private void ConfigureArchiveMediaFavorites()
    {
        if (_thread == null) return;
        ResourceId threadId = _thread.Summary.Id;
        string url = _thread.Summary.Url;
        GalleryEntry Entry(ArchiveAssetRow row) => new(new GalleryAssetKey(threadId, row.Manifest.Directory, row.Asset.AssetId),
            row.Manifest, row.Asset, null, null, !row.Asset.IsCurrent, true) { ThreadUrl = url };
        foreach (ArchiveAssetRow row in _assets)
        {
            GalleryEntry entry = Entry(row);
            row.FavoriteAvailable = () => ToggleArchiveFavoriteAsync != null;
            row.IsFavorite = () => IsArchiveFavorite?.Invoke(entry) == true;
            row.ToggleFavoriteAsync = () => ToggleArchiveMediaFavoriteAsync(entry);
        }
        if (Control<ListBox>("ArchivePosts").ItemsSource is System.Collections.IEnumerable posts)
            foreach (ArchivePostRow post in posts.OfType<ArchivePostRow>())
                foreach (ArchiveAttachmentRow row in post.Attachments)
                {
                    ArchiveAssetRow? match = FindMatchingAsset(row.Attachment, _assets);
                    GalleryEntry? entry = match == null ? null : Entry(match);
                    row.FavoriteAvailable = () => entry != null && ToggleArchiveFavoriteAsync != null;
                    row.IsFavorite = () => entry != null && IsArchiveFavorite?.Invoke(entry) == true;
                    row.ToggleFavoriteAsync = () => entry == null ? Task.CompletedTask : ToggleArchiveMediaFavoriteAsync(entry);
                }
        RefreshArchiveFavoriteControls();
    }

    private async Task ToggleArchiveMediaFavoriteAsync(GalleryEntry entry)
    {
        if (ToggleArchiveFavoriteAsync == null) return;
        await ToggleArchiveFavoriteAsync(entry);
        if (_archiveFavoritesOnly) RenderMedia();
        else RefreshArchiveFavoriteControls();
    }

    internal void RefreshArchiveFavoriteControls()
    {
        foreach (ArchiveAttachmentView view in this.GetVisualDescendants().OfType<ArchiveAttachmentView>()) view.RefreshFavorite();
        foreach (ArchiveMediaTileView view in this.GetVisualDescendants().OfType<ArchiveMediaTileView>()) view.RefreshFavorite();
    }

    private void InitializeArchiveMediaCommands() =>
        AddHandler(ContextRequestedEvent, OnArchiveMediaContextRequested, RoutingStrategies.Tunnel);

    private void OnArchiveMediaContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || _ended || !_available || e.Source is not Control source) return;
        Control[] ancestors = source.GetSelfAndVisualAncestors().OfType<Control>().ToArray();
        ArchiveAttachmentView? attachmentView = ancestors.OfType<ArchiveAttachmentView>().FirstOrDefault();
        ArchiveAttachmentRow? attachment = attachmentView?.DataContext as ArchiveAttachmentRow;
        ArchiveAssetRow? asset = ancestors.Select(control => control.DataContext).OfType<ArchiveAssetRow>().FirstOrDefault();
        Control? origin = attachmentView;
        if (asset != null) origin = ancestors.First(control => control.DataContext is ArchiveAssetRow);
        if (asset == null && attachment == null && !e.TryGetPosition(this, out _))
        {
            Control? list = ancestors.FirstOrDefault(control => control.Name is "ArchiveMediaGrid" or "ArchiveMediaTiles");
            asset = list switch { DataGrid grid => grid.SelectedItem as ArchiveAssetRow, ListBox tiles => tiles.SelectedItem as ArchiveAssetRow, _ => null };
            origin = list;
        }
        if (attachment != null) asset = FindMatchingAsset(attachment.Attachment, _assets);
        if (asset == null && attachment == null) return;
        if (_selectedThread == null || _thread == null || origin == null) return;
        e.Handled = true;
        string? postId = ancestors.Select(control => control.DataContext).OfType<ArchivePostRow>().FirstOrDefault()?.Id;
        ContextMenu menu = BuildArchiveMediaMenu(asset, attachment, origin, postId);
        ArchiveMediaMenu?.Close();
        ArchiveMediaMenu = menu;
        menu.Open(origin);
    }

    private ContextMenu BuildArchiveMediaMenu(ArchiveAssetRow? asset, ArchiveAttachmentRow? attachment, Control origin, string? postId = null)
    {
        ContextMenu menu = new();
        // Freeze the current manifest references and post association together. Neither
        // a later page refresh nor a different lead may retarget an open media menu.
        var thread = _thread!;
        ResourceId threadId = thread.Summary.Id;
        ArchiveAssetRow[] assets = _assets.ToArray();
        GalleryEntry[] entries = GallerySelection.Build(thread, assets.Select(row => row.Manifest).DistinctBy(manifest => manifest.Directory),
            true, includeOtherMedia: true).Entries.ToArray();
        GalleryEntry? entry = asset == null ? null : entries.FirstOrDefault(item => item.Key == new GalleryAssetKey(threadId, asset.Manifest.Directory, asset.Asset.AssetId)
            && (postId == null || item.Post?.PostId == postId));
        if (entry == null && asset != null)
            entry = new GalleryEntry(new GalleryAssetKey(threadId, asset.Manifest.Directory, asset.Asset.AssetId), asset.Manifest, asset.Asset,
                null, null, !asset.Asset.IsCurrent, true) { ThreadUrl = thread.Summary.Url };
        GalleryEntry? target = entry;
        if (target != null && !entries.Any(item => item.Key == target.Key)) entries = entries.Append(target).ToArray();
        bool saved = target != null;
        string missing = Texts.T("archive.notSaved");
        var unavailable = new List<string>();
        void Add(string id, Func<Task> action, bool available, string? reason = null)
        {
            var item = CommandBindings.CreateMenuItem(id, async () =>
            {
                try { await action(); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception error) { Message(error.Message, true); }
            }, () => available && !_ended, () => available ? null : reason);
            item.Tag = id;
            if (!available && reason != null) unavailable.Add(CommandCatalog.Label(id) + ": " + reason);
            menu.Items.Add(item);
        }
        Add("gallery.open", async () =>
        {
            if (target != null && OpenMediaGallery != null)
                await OpenMediaGallery(entries, new GalleryEntryKey(target.Key, target.Post?.PostId, target.Attachment?.Position), origin);
        }, saved && OpenMediaGallery != null, missing);
        Add("favorites.toggle", async () =>
        {
            if (target != null) await ToggleArchiveMediaFavoriteAsync(target);
        }, saved && ToggleArchiveFavoriteAsync != null, missing);
        if (target != null && menu.Items.OfType<MenuItem>().LastOrDefault() is MenuItem favorite)
            favorite.Header = Texts.T(IsArchiveFavorite?.Invoke(target) == true ? "viewer.favorite.remove" : "viewer.favorite.add");
        Add("media.export-original", async () =>
        {
            if (target != null) await OpenOriginalExportAsync(new[] { FavoriteExportAsset.From(threadId, target.Manifest, target.Asset) }, origin);
        }, saved, missing);
        Add("media.show-in-folder", () =>
        {
            if (target != null && Path.GetDirectoryName(target.Asset.Path) is string directory) _openFolder?.Invoke(directory);
            return Task.CompletedTask;
        }, saved && _openFolder != null, missing);
        Add("gallery.show-in-thread", async () =>
        {
            if (target?.Post == null) return;
            if (_thread?.Summary.Id != threadId) await LoadThreadAsync(threadId);
            Control<TabControl>("ArchiveContentTabs").SelectedIndex = 0;
            await FocusPostAsync(target.Post.PostId);
        }, target?.Post != null, Texts.T("gallery.noPost"));
        Add("media.details", () =>
        {
            string details = target == null ? attachment?.Label + Environment.NewLine + missing
                : (target.Asset.OriginalFileName ?? target.Asset.Path) + Environment.NewLine + target.Asset.Path + Environment.NewLine
                    + target.Asset.Length.ToString("N0", Texts.Culture) + " bytes" + Environment.NewLine + "SHA-256: " + target.Asset.Sha256
                    + Environment.NewLine + threadId.Source + " /" + threadId.Board + "/ " + threadId.Thread
                    + Environment.NewLine + (target.Post?.PostId ?? Texts.T("gallery.noPost"));
            return ShowArchiveMetadataAsync(CommandCatalog.Label("media.details"), details);
        }, true);
        if (unavailable.Count > 0)
        {
            string explanation = string.Join(Environment.NewLine, unavailable);
            var info = new MenuItem { Header = Texts.T("archive.command.unavailable"), Icon = CommandBindings.Icon("thread.details") };
            Avalonia.Automation.AutomationProperties.SetHelpText(info, explanation);
            ToolTip.SetTip(info, explanation);
            info.Click += (_, _) => Message(explanation);
            menu.Items.Add(info);
        }
        return menu;
    }
}
