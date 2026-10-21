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

    private enum FavoriteScope { CurrentThread, SelectedThreads, FilteredThreads, All }

    internal async Task OpenFavoritesOverviewAsync()
    {
        if (_ended || GetFavorites == null || ResolveFavoriteAssets == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        FavoriteEntry[] all = GetFavorites().ToArray();
        ResourceId? currentThread = _selectedThread;
        ResourceId[] selectedThreads = FrozenArchiveSelection();
        var query = _query;
        int workFilter = _workFilter;
        Window dialog = new()
        {
            Title = Texts.T("viewer.favorites.title"), Width = 760, Height = 580,
            MinWidth = 450, MinHeight = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        ComboBox scope = new() { Name = "FavoritesScope", ItemsSource = new[]
        {
            Texts.T("viewer.favorites.current"), Texts.T("viewer.favorites.selected"),
            Texts.T("viewer.favorites.filtered"), Texts.T("viewer.favorites.all")
        }, SelectedIndex = 3, MinWidth = 210 };
        TextBox search = new() { Name = "FavoritesSearch", PlaceholderText = Texts.T("viewer.favorites.search"), MinWidth = 180 };
        TextBlock summary = new() { Name = "FavoritesSummary", TextWrapping = TextWrapping.Wrap };
        ListBox list = new() { Name = "FavoritesList" };
        TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
        Button export = CommandBindings.CreateButton("favorites.export-originals", () => { });
        export.Name = "FavoritesExportOriginals";
        Button close = new() { Content = Texts.T("action.close") };
        WrapPanel filters = new() { ItemSpacing = 8, LineSpacing = 8 };
        filters.Children.Add(scope); filters.Children.Add(search);
        WrapPanel actions = new() { ItemSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(export); actions.Children.Add(close);
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), RowSpacing = 10, Margin = new Thickness(16) };
        layout.Children.Add(filters);
        layout.Children.Add(summary); Grid.SetRow(summary, 1);
        layout.Children.Add(list); Grid.SetRow(list, 2);
        layout.Children.Add(status); Grid.SetRow(status, 3);
        layout.Children.Add(actions); Grid.SetRow(actions, 4);
        dialog.Content = layout;
        close.Click += (_, _) => dialog.Close();
        dialog.KeyDown += (_, key) =>
        {
            if (key.Handled || key.Key != Key.Escape) return;
            key.Handled = true;
            dialog.Close();
        };

        FavoriteEntry[] scoped = all;
        FavoriteEntry[] visible = all;
        bool scopeLoading = false;
        HashSet<FavoriteAssetKey> missing = new();
        int generation = 0;
        CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        dialog.Closed += (_, _) => loading.Cancel();
        void Render()
        {
            string term = search.Text?.Trim() ?? string.Empty;
            visible = string.IsNullOrEmpty(term) ? scoped : scoped.Where(entry =>
                FavoriteLabel(entry).Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            list.ItemsSource = visible.Select(entry => (missing.Contains(entry.Key) ? Texts.T("viewer.favorites.missing") + " · " : "") + FavoriteLabel(entry)).ToArray();
            summary.Text = Texts.T("viewer.favorites.count", scoped.Length, visible.Length);
            export.IsEnabled = !scopeLoading && visible.Length > 0;
        }
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
                    FavoriteScope.FilteredThreads => await FilteredFavoriteThreadsAsync(query, workFilter, loading.Token),
                    _ => null
                };
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
            if (ResolveFavoriteAssets == null) return;
            FavoriteEntry[] frozen = visible.ToArray();
            export.IsEnabled = false;
            try
            {
                FavoriteResolutionSnapshot resolved = await ResolveFavoriteAssets(frozen, loading.Token);
                string label = scope.SelectedItem?.ToString() ?? Texts.T("viewer.favorites.all");
                if (!string.IsNullOrWhiteSpace(search.Text)) label += " · " + Texts.T("viewer.favorites.searchApplied");
                await OpenOriginalExportAsync(resolved.Available, export, resolved.Missing.Count, label);
            }
            catch (OperationCanceledException) when (loading.IsCancellationRequested) { }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { export.IsEnabled = !scopeLoading && visible.Length > 0; }
        };
        Render();
        Task shown = dialog.ShowDialog(owner);
        await LoadScopeAsync();
        await shown;
        loading.Dispose();
    }

    private async Task<HashSet<ResourceId>> FilteredFavoriteThreadsAsync(MeowChan.Archive.ArchiveThreadQuery query,
        int workFilter, CancellationToken token)
    {
        if (_openReader == null) return new HashSet<ResourceId>();
        await using var reader = await _openReader(token);
        var summaries = await reader.SelectThreadSummariesAsync(query, token);
        if (workFilter == 0) return summaries.Select(item => item.Id).ToHashSet();
        var work = (GetWorkSnapshots?.Invoke() ?? Array.Empty<ThreadWorkSnapshot>())
            .GroupBy(item => item.Url, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return summaries.Where(item => WorkMatches(work.GetValueOrDefault(item.Url), workFilter))
            .Select(item => item.Id).ToHashSet();
    }

    private static string FavoriteLabel(FavoriteEntry entry) =>
        entry.Key.Thread.Source + " /" + entry.Key.Thread.Board + "/ " + entry.Key.Thread.Thread + " · " +
        (entry.OriginalFileName ?? entry.Key.MediaIdentifier) + " · " + entry.Length.ToString("N0", Texts.Culture) + " bytes";

    internal async Task OpenOriginalExportAsync(IReadOnlyList<FavoriteExportAsset> frozenAssets, Control? origin = null,
        int missingCount = 0, string? scopeLabel = null)
    {
        if (TopLevel.GetTopLevel(origin ?? this) is not Window owner) return;
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
        if (System.IO.Directory.Exists(LastExportDirectoryPreference)) destination.Text = LastExportDirectoryPreference;
        start.IsEnabled = assets.Length > 0 && !string.IsNullOrEmpty(destination.Text);
        openFolder.IsEnabled = !string.IsNullOrEmpty(destination.Text);
        Button cancel = CommandBindings.CreateButton("export.cancel", () => { });
        WrapPanel actions = new() { ItemSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(browse); actions.Children.Add(openFolder); actions.Children.Add(start); actions.Children.Add(cancel);
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 12, Margin = new Thickness(16) };
        layout.Children.Add(scope);
        layout.Children.Add(destination); Grid.SetRow(destination, 1);
        layout.Children.Add(status); Grid.SetRow(status, 2);
        layout.Children.Add(actions); Grid.SetRow(actions, 3);
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
            status.Text = Texts.T("viewer.export.running");
            try
            {
                FavoriteExportResult result = await FavoriteOriginalExporter.ExportAsync(assets, destination.Text, run.Token);
                status.Text = Texts.T("viewer.export.result", result.Copied, result.AlreadyExists,
                    result.Missing + missingCount, result.Failed, result.Cancelled);
            }
            catch (OperationCanceledException) when (run.IsCancellationRequested) { status.Text = Texts.T("viewer.export.cancelled"); }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { running = false; browse.IsEnabled = assets.Length > 0; openFolder.IsEnabled = !string.IsNullOrWhiteSpace(destination.Text); }
        };
        cancel.Click += (_, _) => { if (running) run.Cancel(); else dialog.Close(); };
        await dialog.ShowDialog(owner);
        run.Dispose();
    }
}
