using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using Avalonia.Automation;
using Avalonia.Controls.Templates;
using MeowChan.Gallery;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Storage;

namespace MeowChan;

public partial class ArchivePage
{
    internal Func<IReadOnlyList<FavoriteEntry>>? GetFavorites { get; set; }
    internal Func<IReadOnlyList<FavoriteEntry>?, CancellationToken, Task<FavoriteResolutionSnapshot>>? ResolveFavoriteAssets { get; set; }

    internal Func<FavoriteEntry, Task>? RemoveFavoriteAsync { get; set; }
    internal Func<IReadOnlyList<GalleryEntry>, GalleryEntryKey, Action, Task>? OpenFavoritesGallery { get; set; }
    private sealed record FavoriteOverviewState(int Scope, string Search, FavoriteAssetKey? Selected);
    private sealed record FavoriteOverviewRow(FavoriteEntry Entry, bool Missing)
    {
        public string Label => (Missing ? Texts.T("viewer.favorites.missing") + " · " : string.Empty) + FavoriteLabel(Entry);
        public string Identity => Entry.Key.MediaIdentifier + " · " + Entry.Key.Variant + " · SHA-256: " + Entry.Key.Sha256;
    }

    private enum FavoriteScope { CurrentThread, SelectedThreads, FilteredThreads, All }

    internal Task OpenFavoritesOverviewAsync() => OpenFavoritesOverviewAsync(null);

    private async Task OpenFavoritesOverviewAsync(FavoriteOverviewState? restore)
    {
        if (_ended || GetFavorites == null || ResolveFavoriteAssets == null
            || (TopLevel.GetTopLevel(Control<Grid>("ArchiveReaderPane")) ?? TopLevel.GetTopLevel(this)) is not Window owner) return;
        FavoriteEntry[] all = GetFavorites().ToArray();
        ResourceId? currentThread = _selectedThread;
        ResourceId[] selectedThreads = FrozenArchiveSelection();
        var query = _appliedSearch ?? ArchiveSearchRequest.Create(_query);
        var eligibleUrls = CaptureExportWorkUrls(_workFilter, GetWorkSnapshots?.Invoke() ?? Array.Empty<ThreadWorkSnapshot>());
        Window dialog = new()
        {
            Name = "FavoritesOverviewDialog", Title = Texts.T("viewer.favorites.title"), Width = 760, Height = 580,
            MinWidth = 450, MinHeight = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        ComboBox scope = new() { Name = "FavoritesScope", ItemsSource = new[]
        {
            Texts.T("viewer.favorites.current"), Texts.T("viewer.favorites.selected"),
            Texts.T("viewer.favorites.filtered"), Texts.T("viewer.favorites.all")
        }, SelectedIndex = restore?.Scope ?? 3, MinWidth = 210 };
        TextBox search = new() { Text = restore?.Search, Name = "FavoritesSearch", PlaceholderText = Texts.T("viewer.favorites.search"), MinWidth = 180 };
        TextBlock summary = new() { Name = "FavoritesSummary", TextWrapping = TextWrapping.Wrap };
        TextBlock empty = new() { Name = "FavoritesEmpty", TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Button clearSearch = new() { Name = "FavoritesClearSearch", Content = Texts.T("viewer.favorites.clearSearch"),
            HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
        StackPanel emptyState = new() { Spacing = 8, Children = { empty, clearSearch } };
        ListBox list = new() { Name = "FavoritesList", SelectionMode = SelectionMode.Single };
        list.ItemTemplate = new FuncDataTemplate<FavoriteOverviewRow>((row, _) =>
        {
            StackPanel item = new() { Spacing = 4, Margin = new Thickness(4, 8) };
            item.Children.Add(new TextBlock { Text = row?.Label, TextWrapping = TextWrapping.Wrap });
            TextBlock identity = new() { Text = row?.Identity, TextWrapping = TextWrapping.Wrap };
            identity.Classes.Add("workspace-meta"); item.Children.Add(identity);
            return item;
        });
        Button view = new() { Name = "FavoritesView", Content = Texts.T("viewer.favorites.view") };
        Button remove = new() { Name = "FavoritesRemove", Content = Texts.T("viewer.favorite.remove") };
        AutomationProperties.SetName(view, Texts.T("viewer.favorites.view"));
        AutomationProperties.SetName(remove, Texts.T("viewer.favorite.remove"));
        SelectableTextBlock status = new() { Name = "FavoritesStatus", TextWrapping = TextWrapping.Wrap, Focusable = true };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Assertive);
        Button export = CommandBindings.CreateButton("favorites.export-originals", () => { });
        export.Name = "FavoritesExportOriginals";
        Button close = new() { Content = Texts.T("action.close") };
        remove.Classes.Add("danger"); summary.Classes.Add("workspace-meta");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        WrapPanel filters = new() { ItemSpacing = 8, LineSpacing = 8 };
        filters.Children.Add(scope); filters.Children.Add(search);
        WrapPanel actions = new() { ItemSpacing = 8, LineSpacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        // Selection actions stay with the list; export always targets the shown set.
        WrapPanel selectionActions = new() { ItemSpacing = 8, LineSpacing = 8 };
        selectionActions.Children.Add(view); selectionActions.Children.Add(remove);
        actions.Children.Add(export); actions.Children.Add(close);
        Border footer = new() { Child = actions, Margin = new Thickness(-16, 0, -16, -16) };
        footer.Classes.Add("workspace-dialog-footer");
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto"), RowSpacing = 12, Margin = new Thickness(16) };
        layout.Children.Add(filters);
        layout.Children.Add(summary); Grid.SetRow(summary, 1);
        Grid results = new();
        results.Children.Add(list); results.Children.Add(emptyState);
        layout.Children.Add(results); Grid.SetRow(results, 2);
        layout.Children.Add(selectionActions); Grid.SetRow(selectionActions, 3);
        ScrollViewer notice = new() { Content = status, MaxHeight = 96, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        layout.Children.Add(notice); Grid.SetRow(notice, 4);
        layout.Children.Add(footer); Grid.SetRow(footer, 5);
        dialog.Content = layout;
        close.Click += (_, _) => dialog.Close();
        clearSearch.Click += (_, _) => { search.Text = string.Empty; search.Focus(); };
        dialog.KeyDown += (_, key) =>
        {
            if (key.Handled || key.Key != Key.Escape) return;
            key.Handled = true;
            dialog.Close();
        };

        FavoriteEntry[] scoped = all;
        FavoriteEntry[] visible = all;
        bool scopeLoading = false;
        bool actionRunning = false;
        FavoriteAssetKey? selection = restore?.Selected;
        GalleryEntry[]? galleryEntries = null;
        GalleryEntryKey? galleryStart = null;
        FavoriteOverviewState? returnState = null;
        HashSet<FavoriteAssetKey> missing = new();
        int generation = 0;
        CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        dialog.Closed += (_, _) => loading.Cancel();
        void Render()
        {
            string term = search.Text?.Trim() ?? string.Empty;
            visible = string.IsNullOrEmpty(term) ? scoped : scoped.Where(entry =>
                FavoriteLabel(entry).Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            selection = (list.SelectedItem as FavoriteOverviewRow)?.Entry.Key ?? selection;
            FavoriteOverviewRow[] rows = visible.Select(entry => new FavoriteOverviewRow(entry, missing.Contains(entry.Key))).ToArray();
            list.ItemsSource = rows;
            list.SelectedItem = rows.FirstOrDefault(row => row.Entry.Key == selection);
            empty.IsVisible = !scopeLoading && rows.Length == 0;
            empty.Text = Texts.T(term.Length == 0 ? "viewer.favorites.emptyScope" : "viewer.favorites.noResults");
            clearSearch.IsVisible = empty.IsVisible && term.Length > 0;
            list.IsVisible = !empty.IsVisible;
            RefreshActions();
            summary.Text = Texts.T("viewer.favorites.count", scoped.Length, visible.Length);
        }
        void RefreshActions()
        {
            FavoriteOverviewRow? selected = list.SelectedItem as FavoriteOverviewRow;
            view.IsEnabled = !scopeLoading && !actionRunning && selected is { Missing: false } && OpenFavoritesGallery != null;
            remove.IsEnabled = !scopeLoading && !actionRunning && selected != null && RemoveFavoriteAsync != null;
            string exportLabel = Texts.T("viewer.favorites.exportShown", visible.Length);
            export.Content = exportLabel;
            AutomationProperties.SetName(export, exportLabel);
            string exportHint = Texts.T("viewer.favorites.exportShownHint", visible.Length);
            ToolTip.SetTip(export, exportHint);
            AutomationProperties.SetHelpText(export, exportHint);
            clearSearch.IsEnabled = !actionRunning;
            export.IsEnabled = !scopeLoading && !actionRunning && visible.Length > 0;
            if (selected?.Missing == true) status.Text = Texts.T("viewer.favorites.unavailable", 1);
        }
        list.SelectionChanged += (_, _) => RefreshActions();
        async Task LoadScopeAsync()
        {
            int request = ++generation;
            try
            {
                scopeLoading = true;
                Render();
                status.Text = Texts.T("viewer.favorites.loading");
                FavoriteScope choice = (FavoriteScope)Math.Clamp(scope.SelectedIndex, 0, 3);
                HashSet<ResourceId>? threadIds = choice switch
                {
                    FavoriteScope.CurrentThread => currentThread == null ? new HashSet<ResourceId>() : new HashSet<ResourceId> { currentThread },
                    FavoriteScope.SelectedThreads => selectedThreads.ToHashSet(),
                    FavoriteScope.FilteredThreads => await FilteredFavoriteThreadsAsync(query, eligibleUrls, loading.Token),
                    _ => null
                };
                all = GetFavorites().ToArray();
                FavoriteEntry[] next = threadIds == null ? all : all.Where(entry => threadIds.Contains(entry.Key.Thread)).ToArray();
                FavoriteResolutionSnapshot resolved = await ResolveFavoriteAssets(next, loading.Token);
                if (request != generation || loading.IsCancellationRequested) return;
                scoped = next;
                missing = resolved.Missing.Select(entry => entry.Key).ToHashSet();
                status.Text = missing.Count == 0 ? string.Empty : Texts.T("viewer.favorites.unavailable", missing.Count);
                scopeLoading = false;
                Render();
            }
            catch (OperationCanceledException) when (loading.IsCancellationRequested) { }
            catch (Exception ex) { if (request == generation) { status.Text = ex.Message; scopeLoading = false; Render(); } }
        }
        scope.SelectionChanged += async (_, _) => await LoadScopeAsync();
        search.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) Render(); };
        export.Click += async (_, _) =>
        {
            if (scopeLoading || actionRunning || visible.Length == 0 || ResolveFavoriteAssets == null) return;
            FavoriteEntry[] frozen = visible.ToArray();
            string label = scope.SelectedItem?.ToString() ?? Texts.T("viewer.favorites.all");
            if (!string.IsNullOrWhiteSpace(search.Text)) label += " · " + Texts.T("viewer.favorites.searchApplied");
            actionRunning = true; RefreshActions();
            try
            {
                FavoriteResolutionSnapshot resolved = await ResolveFavoriteAssets(frozen, loading.Token);
                await OpenOriginalExportAsync(resolved.Available, export, resolved.Missing.Count, label);
            }
            catch (OperationCanceledException) when (loading.IsCancellationRequested) { }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { actionRunning = false; RefreshActions(); }
        };
        remove.Click += async (_, _) =>
        {
            if (scopeLoading || actionRunning || list.SelectedItem is not FavoriteOverviewRow selected || RemoveFavoriteAsync == null) return;
            int index = list.SelectedIndex;
            actionRunning = true; RefreshActions();
            status.Text = string.Empty;
            try
            {
                await RemoveFavoriteAsync(selected.Entry);
                await LoadScopeAsync();
                if (list.ItemCount > 0) list.SelectedIndex = Math.Min(index, list.ItemCount - 1);
                selection = (list.SelectedItem as FavoriteOverviewRow)?.Entry.Key;
            }
            catch (Exception error) { status.Text = error.Message; }
            finally { actionRunning = false; RefreshActions(); }
        };
        view.Click += async (_, _) =>
        {
            if (scopeLoading || actionRunning || list.SelectedItem is not FavoriteOverviewRow selected || OpenFavoritesGallery == null) return;
            FavoriteEntry[] frozen = visible.ToArray();
            returnState = new FavoriteOverviewState(scope.SelectedIndex, search.Text ?? string.Empty, selected.Entry.Key);
            actionRunning = true; RefreshActions();
            try
            {
                FavoriteResolutionSnapshot resolved = await ResolveFavoriteAssets(frozen, loading.Token);
                galleryEntries = resolved.Available.Select(item =>
                {
                    MediaAssetReference asset = item.Manifest.FindById(item.AssetId)!;
                    return new GalleryEntry(new GalleryAssetKey(item.Key.Thread, item.Manifest.Directory, item.AssetId),
                        item.Manifest, asset, null, null, !asset.IsCurrent, true);
                }).ToArray();
                galleryStart = galleryEntries.FirstOrDefault(entry => FavoriteAssetKey.From(entry.Key.Thread, entry.Asset) == selected.Entry.Key)?.EntryKey;
                if (galleryStart == null)
                {
                    missing.Add(selected.Entry.Key); Render();
                    status.Text = Texts.T("viewer.favorites.unavailable", 1);
                    return;
                }
                dialog.Close();
            }
            catch (OperationCanceledException) when (loading.IsCancellationRequested) { }
            catch (Exception error) { status.Text = error.Message; }
            finally { actionRunning = false; RefreshActions(); }
        };
        Render();
        Task shown = dialog.ShowDialog(owner);
        await LoadScopeAsync();
        await shown;
        loading.Dispose();
        if (galleryStart != null && galleryEntries != null && OpenFavoritesGallery != null && !_ended)
            await OpenFavoritesGallery(galleryEntries, galleryStart, () => { if (!_ended) _ = OpenFavoritesOverviewAsync(returnState); });
    }

    private async Task<HashSet<ResourceId>> FilteredFavoriteThreadsAsync(MeowChan.Archive.ArchiveSearchRequest query,
        IReadOnlySet<string>? eligibleUrls, CancellationToken token)
    {
        if (_openReader == null) return new HashSet<ResourceId>();
        await using var reader = await _openReader(token);
        return (await ResolveExportResultIdsAsync(reader, query, eligibleUrls, token)).ToHashSet();
    }

    private static string FavoriteLabel(FavoriteEntry entry) =>
        entry.Key.Thread.Source + " /" + entry.Key.Thread.Board + "/ " + entry.Key.Thread.Thread + " · " +
        (entry.OriginalFileName ?? entry.Key.MediaIdentifier) + " · " + entry.Length.ToString("N0", Texts.Culture) + " bytes";

    internal async Task OpenOriginalExportAsync(IReadOnlyList<FavoriteExportAsset> frozenAssets, Control? origin = null,
        int missingCount = 0, string? scopeLabel = null)
    {
        Window? owner = TopLevel.GetTopLevel(origin ?? Control<Grid>("ArchiveReaderPane")) as Window
            ?? TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;
        FavoriteExportAsset[] assets = frozenAssets.DistinctBy(asset => asset.Key).ToArray();
        Window dialog = new()
        {
            Title = Texts.T("viewer.export.title"), Width = 600, Height = 300, MinWidth = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        TextBlock scope = new() { Text = Texts.T("viewer.export.scope", scopeLabel ?? Texts.T("viewer.export.single"), assets.Length + missingCount), TextWrapping = TextWrapping.Wrap };
        TextBox destination = new() { Name = "OriginalExportDestination", IsReadOnly = true, PlaceholderText = Texts.T("viewer.export.destination") };
        TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
        if (assets.Length == 0)
            status.Text = missingCount > 0 ? Texts.T("viewer.favorites.unavailable", missingCount) : Texts.T("viewer.export.empty");
        Button browse = CommandBindings.CreateButton("export.browse-parent", () => { });
        browse.IsEnabled = assets.Length > 0;
        Button openFolder = CommandBindings.CreateButton("location.open-folder", () => { });
        Button start = CommandBindings.CreateButton("export.start-originals", () => { });
        start.Name = "OriginalExportStart";
        if (System.IO.Directory.Exists(LastExportDirectoryPreference)) destination.Text = LastExportDirectoryPreference;
        start.IsEnabled = assets.Length > 0 && !string.IsNullOrEmpty(destination.Text);
        openFolder.IsEnabled = !string.IsNullOrEmpty(destination.Text);
        Button cancel = CommandBindings.CreateButton("export.close", () => { });
        cancel.Name = "OriginalExportClose";
        WrapPanel actions = new() { ItemSpacing = 8, LineSpacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Classes.Add("subtle"); start.Classes.Add("primary"); actions.Classes.Add("workspace-actions");
        actions.Children.Add(cancel); actions.Children.Add(openFolder); actions.Children.Add(start);
        TextBlock title = new() { Text = dialog.Title, TextWrapping = TextWrapping.Wrap }; title.Classes.Add("page-title");
        scope.Classes.Add("section-title");
        StackPanel header = new() { Spacing = 8, Margin = new Thickness(16, 16, 16, 0) };
        header.Children.Add(title); header.Children.Add(scope);
        TextBlock hint = new() { Text = Texts.T("export.originalHint"), TextWrapping = TextWrapping.Wrap };
        hint.Classes.Add("workspace-help");
        TextBlock targetLabel = new() { Text = Texts.T("export.target") }; targetLabel.Classes.Add("section-title");
        TextBlock unavailable = new() { Text = Texts.T("viewer.favorites.unavailable", missingCount), TextWrapping = TextWrapping.Wrap, IsVisible = missingCount > 0 };
        unavailable.Classes.Add("workspace-warning");
        StackPanel content = new() { Spacing = 12, Margin = new Thickness(16, 0) };
        content.Children.Add(hint); content.Children.Add(unavailable); content.Children.Add(targetLabel);
        content.Children.Add(destination); content.Children.Add(browse);
        AutomationProperties.SetLabeledBy(destination, targetLabel);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        StackPanel footerContent = new() { Spacing = 12 };
        footerContent.Children.Add(new ScrollViewer { Content = status, MaxHeight = 140, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        footerContent.Children.Add(actions);
        Border footer = new() { Child = footerContent }; footer.Classes.Add("workspace-dialog-footer");
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        layout.Children.Add(header);
        ScrollViewer scroll = new() { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        layout.Children.Add(scroll); Grid.SetRow(scroll, 1);
        layout.Children.Add(footer); Grid.SetRow(footer, 2);
        dialog.Content = layout;
        CancellationTokenSource run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        bool running = false;
        dialog.Closing += (_, args) =>
        {
            if (!running) return;
            args.Cancel = true;
            run.Cancel();
        };
        dialog.Closed += (_, _) => run.Cancel();
        dialog.KeyDown += (_, key) =>
        {
            if (key.Handled || key.Key != Key.Escape) return;
            key.Handled = true;
            if (running) run.Cancel();
            else dialog.Close();
        };
        openFolder.Click += async (_, _) =>
        {
            string? path = destination.Text;
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (OpenExportFolder != null) await OpenExportFolder(path);
                else _openFolder?.Invoke(path);
            }
            catch (Exception ex) { status.Text = Texts.T("export.openFailed", ex.Message); }
        };
        browse.Click += async (_, _) =>
        {
            IStorageProvider? storage = owner.StorageProvider;
            if (storage == null || !storage.CanPickFolder) { status.Text = Texts.T("settings.pickerUnavailable"); return; }
            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = Texts.T("viewer.export.destination"), AllowMultiple = false });
            try
            {
                if (run.IsCancellationRequested) return;
                if (folders.Count == 0) return;
                string? path = folders[0].TryGetLocalPath();
                if (path == null) { status.Text = Texts.T("settings.pickerLocalOnly"); return; }
                await RememberExportDirectoryAsync(path);
                destination.Text = path;
                start.IsEnabled = assets.Length > 0;
                openFolder.IsEnabled = true;
            }
            finally { foreach (var folder in folders) folder.Dispose(); }
        };
        start.Click += async (_, _) =>
        {
            if (running || string.IsNullOrWhiteSpace(destination.Text)) return;
            running = true; start.IsEnabled = browse.IsEnabled = openFolder.IsEnabled = false;
            CommandBindings.Apply(cancel, "export.cancel");
            status.Text = Texts.T("viewer.export.running");
            try
            {
                FavoriteExportResult result = await FavoriteOriginalExporter.ExportAsync(assets, destination.Text, run.Token);
                status.Text = Texts.T("viewer.export.result", result.Copied, result.AlreadyExists,
                    result.Missing + missingCount, result.Failed, result.Cancelled);
            }
            catch (OperationCanceledException) when (run.IsCancellationRequested) { status.Text = Texts.T("viewer.export.cancelled"); }
            catch (Exception ex) { status.Text = ex.Message; }
            finally
            {
                running = false;
                CommandBindings.Apply(cancel, "export.close");
                browse.IsEnabled = assets.Length > 0;
                openFolder.IsEnabled = !string.IsNullOrWhiteSpace(destination.Text);
            }
        };
        cancel.Click += (_, _) => { if (running) run.Cancel(); else dialog.Close(); };
        await dialog.ShowDialog(owner);
        run.Dispose();
    }
}
