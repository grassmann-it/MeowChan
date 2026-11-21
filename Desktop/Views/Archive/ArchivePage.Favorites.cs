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
            || (TopLevel.GetTopLevel(ArchiveReaderPane) ?? TopLevel.GetTopLevel(this)) is not Window owner) return;
        // An unreadable favorites file is refused with its own sentence (never an exception out of the click handler); the file is left as it is.
        try { _ = GetFavorites(); }
        catch (System.IO.InvalidDataException) { CaptureActionErrorReporter()(Texts.T("archive.favoritesUnreadable")); return; }
        ResourceId? currentThread = _selectedThread;
        ResourceId[] selectedThreads = FrozenArchiveSelection();
        var query = _appliedSearch ?? ArchiveSearchRequest.Create(_query);
        var eligibleUrls = CaptureExportWorkUrls(_workFilter, GetWorkSnapshots?.Invoke() ?? Array.Empty<ThreadWorkSnapshot>());
        // Modals plan MD3: the list-and-detail step; the title stands in the title bar only.
        Window dialog = MeowChan.Desktop.Controls.DialogWindow.Create(Texts.T("viewer.favorites.title"), DialogSize.L, modal: true);
        dialog.Name = "FavoritesOverviewDialog";
        ComboBox scope = new() { Name = "FavoritesScope", ItemsSource = new[]
        {
            Texts.T("viewer.favorites.current"), Texts.T("viewer.favorites.selected"),
            Texts.T("viewer.favorites.filtered"), Texts.T("viewer.favorites.all")
        }, SelectedIndex = restore?.Scope ?? 3, MinWidth = 210 };
        TextBox search = new MeowChan.Desktop.Controls.SearchBox { Text = restore?.Search, Name = "FavoritesSearch", PlaceholderText = Texts.T("viewer.favorites.search"), MinWidth = 180 };
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
        MeowChan.Desktop.Controls.IconButtons.WithText(view, "eye");
        // A double click or Enter on a row runs the "View" button, refused exactly when the button is disabled.
        MeowChan.Desktop.Controls.ListRowActions.OnActivate(list, () =>
        {
            if (view.IsEnabled) view.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        });
        Button remove = new() { Name = "FavoritesRemove", Content = Texts.T("viewer.favorite.remove") };
        AutomationProperties.SetName(view, Texts.T("viewer.favorites.view"));
        AutomationProperties.SetName(remove, Texts.T("viewer.favorite.remove"));
        SelectableTextBlock status = new() { Name = "FavoritesStatus", TextWrapping = TextWrapping.Wrap, Focusable = true };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Assertive);
        TextBlock error = new() { Name = "FavoritesError", TextWrapping = TextWrapping.Wrap };
        // One message at a time, as before: failures in the local strip, notices in the footer.
        void Notice(string text) { status.Text = text; error.Text = string.Empty; }
        void Fail(string text) { status.Text = string.Empty; error.Text = text; }
        void FailWith(Exception failure) => Fail(FavoriteOverviewError(failure));
        Button export = CommandBindings.CreateButton("favorites.export-originals", () => { });
        export.Name = "FavoritesExportOriginals";
        MeowChan.Desktop.Controls.IconButtons.WithText(export, "download");
        Button close = new() { Content = Texts.T("action.close") };
        remove.Classes.Add("danger"); summary.Classes.Add("workspace-meta");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        WrapPanel filters = new() { ItemSpacing = 8, LineSpacing = 8 };
        filters.Children.Add(scope); filters.Children.Add(search);
        // Selection actions stay with the list; export always targets the shown set.
        WrapPanel selectionActions = new() { ItemSpacing = 8, LineSpacing = 8 };
        selectionActions.Children.Add(view); selectionActions.Children.Add(remove);
        StackPanel header = new() { Spacing = 12 };
        header.Children.Add(filters); header.Children.Add(summary);
        Grid content = new() { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 12, Margin = new Thickness(16, 0, 16, 12) };
        Grid results = new();
        results.Children.Add(list); results.Children.Add(emptyState);
        content.Children.Add(results);
        content.Children.Add(selectionActions); Grid.SetRow(selectionActions, 1);
        // Close is the way out (primary, right-most); the status keeps the full width above the buttons.
        Border footer = MeowChan.Desktop.Controls.DialogFooter.Build(close, new Control[] { export },
            new ScrollViewer { Content = status, MaxHeight = 96, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            statusAbove: true);
        // Scope and search stay fixed; the list keeps its own virtualized scrolling and fills the body
        // until the body reaches its minimum height, after which the whole body scrolls.
        Grid layout = MeowChan.Desktop.Controls.DialogFrame.Build(header, content, footer, MeowChan.Desktop.Controls.DialogFrame.ErrorStrip(error));
        ScrollViewer bodyScroll = layout.Children.OfType<ScrollViewer>().Single();
        bodyScroll.SizeChanged += (_, args) => content.Height = Math.Max(180, args.NewSize.Height - content.Margin.Bottom);
        MeowChan.Desktop.Controls.DialogWindow.Frame(dialog, layout, modal: true);
        close.Click += (_, _) => dialog.Close();
        clearSearch.Click += (_, _) => { search.Text = string.Empty; search.Focus(); };
        MeowChan.Desktop.Controls.DialogEscape.Attach(dialog);

        // Nothing is listed until the first scope has loaded: the restored scope's name must not stand over the full list.
        FavoriteEntry[] scoped = Array.Empty<FavoriteEntry>();
        FavoriteEntry[] visible = Array.Empty<FavoriteEntry>();
        bool scopeLoading = true;
        bool actionRunning = false;
        FavoriteAssetKey? selection = restore?.Selected;
        GalleryEntry[]? galleryEntries = null;
        GalleryEntryKey? galleryStart = null;
        FavoriteOverviewState? returnState = null;
        HashSet<FavoriteAssetKey> missing = new();
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        // The newest scope wins: changing the scope cancels the load that runs, waits until it has unwound and then loads the new one.
        LatestWinsLoad scopeLoads = new(loading.Token);
        HashSet<Task> operations = new();
        async Task TrackAsync(Task task)
        {
            operations.Add(task);
            try { await task; }
            finally { operations.Remove(task); }
        }
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
            string exportHint = Texts.Count("viewer.favorites.exportShownHint", visible.Length);
            ToolTip.SetTip(export, exportHint);
            AutomationProperties.SetHelpText(export, exportHint);
            clearSearch.IsEnabled = !actionRunning;
            export.IsEnabled = !scopeLoading && !actionRunning && visible.Length > 0;
            if (selected?.Missing == true) Notice(Texts.Count("viewer.favorites.unavailable", 1));
        }
        list.SelectionChanged += (_, _) => RefreshActions();
        // scopeChanged: the list of the scope that was shown is dropped at once (it must never stand under another scope's name while the new
        // one loads); a reload of the same scope (after a Remove) keeps its rows until the new ones are there.
        async Task LoadScopeAsync(bool scopeChanged = true)
        {
            if (loading.IsCancellationRequested) return;
            try
            {
                scopeLoading = true;
                if (scopeChanged) { scoped = Array.Empty<FavoriteEntry>(); missing = new HashSet<FavoriteAssetKey>(); }
                Render();
                Notice(Texts.T("viewer.favorites.loading"));
                FavoriteScope choice = (FavoriteScope)Math.Clamp(scope.SelectedIndex, 0, 3);
                FavoriteScopeLoad? loaded = await scopeLoads.RunAsync(async token =>
                {
                    HashSet<ResourceId>? threadIds = choice switch
                    {
                        FavoriteScope.CurrentThread => currentThread == null ? new HashSet<ResourceId>() : new HashSet<ResourceId> { currentThread },
                        FavoriteScope.SelectedThreads => selectedThreads.ToHashSet(),
                        FavoriteScope.FilteredThreads => await FilteredFavoriteThreadsAsync(query, eligibleUrls, token),
                        _ => null
                    };
                    FavoriteEntry[] current = GetFavorites().ToArray();
                    FavoriteEntry[] next = threadIds == null ? current : current.Where(entry => threadIds.Contains(entry.Key.Thread)).ToArray();
                    FavoriteResolutionSnapshot resolved = await ResolveFavoriteAssets(next, token);
                    return new FavoriteScopeLoad(next, resolved.Missing.Select(entry => entry.Key).ToHashSet());
                });
                // Null: a newer scope took over (it owns the state and the messages now) or the dialog closed.
                if (loaded == null || loading.IsCancellationRequested) return;
                scoped = loaded.Scoped;
                missing = loaded.Missing;
                Notice(missing.Count == 0 ? string.Empty : Texts.Count("viewer.favorites.unavailable", missing.Count));
                scopeLoading = false;
                Render();
            }
            catch (OperationCanceledException) when (loading.IsCancellationRequested) { }
            catch (Exception ex) { FailWith(ex); scopeLoading = false; Render(); }
        }
        scope.SelectionChanged += async (_, _) => await TrackAsync(LoadScopeAsync());
        search.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) Render(); };
        export.Click += async (_, _) => await TrackAsync(ExportAsync());
        async Task ExportAsync()
        {
            if (loading.IsCancellationRequested || scopeLoading || actionRunning || visible.Length == 0 || ResolveFavoriteAssets == null) return;
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
            catch (Exception ex) { FailWith(ex); }
            finally { actionRunning = false; RefreshActions(); }
        }
        remove.Click += async (_, _) => await TrackAsync(RemoveAsync());
        async Task RemoveAsync()
        {
            if (loading.IsCancellationRequested || scopeLoading || actionRunning || list.SelectedItem is not FavoriteOverviewRow selected || RemoveFavoriteAsync == null) return;
            int index = list.SelectedIndex;
            actionRunning = true; RefreshActions();
            Notice(string.Empty);
            try
            {
                await RemoveFavoriteAsync(selected.Entry);
                await LoadScopeAsync(scopeChanged: false);
                if (list.ItemCount > 0) list.SelectedIndex = Math.Min(index, list.ItemCount - 1);
                selection = (list.SelectedItem as FavoriteOverviewRow)?.Entry.Key;
            }
            catch (Exception failure) { FailWith(failure); }
            finally { actionRunning = false; RefreshActions(); }
        }
        view.Click += async (_, _) => await TrackAsync(ViewAsync());
        async Task ViewAsync()
        {
            if (loading.IsCancellationRequested || scopeLoading || actionRunning || list.SelectedItem is not FavoriteOverviewRow selected || OpenFavoritesGallery == null) return;
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
                    Notice(Texts.Count("viewer.favorites.unavailable", 1));
                    return;
                }
                dialog.Close();
            }
            catch (OperationCanceledException) when (loading.IsCancellationRequested) { }
            catch (Exception failure) { FailWith(failure); }
            finally { actionRunning = false; RefreshActions(); }
        }
        Render();
        try
        {
            Task shown = dialog.ShowDialog(owner);
            await TrackAsync(LoadScopeAsync());
            await shown;
        }
        finally
        {
            // Closing a window does not await its async event handlers. Drain every consumer before disposing the source.
            loading.Cancel();
            await Task.WhenAll(operations.ToArray());
        }
        if (galleryStart != null && galleryEntries != null && OpenFavoritesGallery != null && !_ended)
            await OpenFavoritesGallery(galleryEntries, galleryStart, () => { if (!_ended) OpenActions.Start(() => OpenFavoritesOverviewAsync(returnState), "archive.favorites.open"); });
    }

    /// <summary>What one finished scope load publishes: the favorites in the scope and which of those have no intact original.</summary>
    private sealed record FavoriteScopeLoad(FavoriteEntry[] Scoped, HashSet<FavoriteAssetKey> Missing);

    /// <summary>The overview's failure sentence: the host's English "busy" refusal becomes a localized one, any other message stays as it is.</summary>
    private static string FavoriteOverviewError(Exception failure) =>
        failure is DesktopSessionBusyException ? Texts.T("queue.error.busy") : failure.Message;

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
        Window? owner = TopLevel.GetTopLevel(origin ?? ArchiveReaderPane) as Window
            ?? TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;
        FavoriteExportAsset[] assets = frozenAssets.DistinctBy(asset => asset.Key).ToArray();
        // Modals plan MD3: the form step; the title stands in the title bar only.
        Window dialog = MeowChan.Desktop.Controls.DialogWindow.Create(Texts.T("export.dialog.title"), DialogSize.M, modal: true);
        // The same Export header as the thread dialog: files have one format, so both choices show as text.
        ExportChoiceField format = new("OriginalExportFormat");
        format.Set(Texts.T("export.format"), new[] { new ExportChoiceField.Option("originals", Texts.T("export.format.originals")) }, "originals", true);
        ExportChoiceField scope = new("OriginalExportScope");
        string scopeText = Texts.Count("viewer.export.scope", assets.Length + missingCount, scopeLabel ?? Texts.T("viewer.export.single"), assets.Length + missingCount);
        scope.Set(Texts.T("export.scope"), new[] { new ExportChoiceField.Option("files", scopeText) }, "files", true);
        WrapPanel choices = new() { Name = "OriginalExportChoices", ItemSpacing = 16, LineSpacing = 8 };
        choices.Children.Add(format); choices.Children.Add(scope);
        TextBox destination = new() { Name = "OriginalExportDestination", IsReadOnly = true, PlaceholderText = Texts.T("viewer.export.destination") };
        TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
        TextBlock error = new() { Name = "OriginalExportError", TextWrapping = TextWrapping.Wrap };
        // One message at a time, as before: failures in the local strip, notices in the footer.
        void Notice(string text) { status.Text = text; error.Text = string.Empty; }
        void Fail(string text) { status.Text = string.Empty; error.Text = text; }
        if (assets.Length == 0)
            status.Text = missingCount > 0 ? Texts.Count("viewer.favorites.unavailable", missingCount) : Texts.T("viewer.export.empty");
        Button browse = CommandBindings.CreateButton("export.browse-parent", () => { }, iconWithLabel: true);
        browse.IsEnabled = assets.Length > 0;
        Button openFolder = CommandBindings.CreateButton("location.open-folder", () => { }, iconWithLabel: true);
        Button start = CommandBindings.CreateButton("export.start-originals", () => { });
        start.Name = "OriginalExportStart";
        if (System.IO.Directory.Exists(LastExportDirectoryPreference)) destination.Text = LastExportDirectoryPreference;
        start.IsEnabled = assets.Length > 0 && !string.IsNullOrEmpty(destination.Text);
        openFolder.IsEnabled = !string.IsNullOrEmpty(destination.Text);
        Button cancel = CommandBindings.CreateButton("export.close", () => { });
        cancel.Name = "OriginalExportClose";
        cancel.Classes.Add("subtle"); start.Classes.Add("primary");
        TextBlock hint = new() { Text = Texts.T("export.originalHint"), TextWrapping = TextWrapping.Wrap };
        hint.Classes.Add("workspace-help");
        // Said before the export starts: own tags never travel with copied files (no silent drop, no implied round trip).
        TextBlock privateOmitted = new() { Name = "OriginalExportPrivateOmitted", Text = string.Join(" ", PrivateTransferDisclosure.Lines()),
            TextWrapping = TextWrapping.Wrap };
        privateOmitted.Classes.Add("workspace-help");
        TextBlock targetLabel = new() { Text = Texts.T("export.target") }; targetLabel.Classes.Add("section-title");
        TextBlock unavailable = new() { Text = Texts.Count("viewer.favorites.unavailable", missingCount), TextWrapping = TextWrapping.Wrap, IsVisible = missingCount > 0 };
        unavailable.Classes.Add("workspace-warning");
        StackPanel content = new() { Spacing = 12, Margin = new Thickness(16, 0, 16, 12) };
        // Said before the export starts: a filename template renames the copies (a file it cannot name safely keeps its name).
        if (ExportFilenameTemplate?.Invoke() is { Length: > 0 } activeTemplate)
        {
            TextBlock templateNote = new() { Name = "OriginalExportFilenameTemplateNote", Text = Texts.T("export.filenameTemplateNote", activeTemplate),
                TextWrapping = TextWrapping.Wrap };
            templateNote.Classes.Add("workspace-help");
            content.Children.Add(templateNote);
        }
        content.Children.Add(hint); content.Children.Add(privateOmitted); content.Children.Add(unavailable); content.Children.Add(targetLabel);
        content.Children.Add(destination); content.Children.Add(browse);
        AutomationProperties.SetLabeledBy(destination, targetLabel);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        // Start is the primary action right-most; Close and Open folder stay left of it; the status keeps the full width above.
        Border footer = MeowChan.Desktop.Controls.DialogFooter.Build(start, new Control[] { cancel, openFolder },
            new ScrollViewer { Content = status, MaxHeight = 140, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            statusAbove: true);
        MeowChan.Desktop.Controls.DialogWindow.Frame(dialog, MeowChan.Desktop.Controls.DialogFrame.Build(choices, content, footer,
            MeowChan.Desktop.Controls.DialogFrame.ErrorStrip(error)), modal: true);
        using CancellationTokenSource run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        bool running = false;
        dialog.Closing += (_, args) =>
        {
            if (!running) return;
            args.Cancel = true;
            run.Cancel();
        };
        dialog.Closed += (_, _) => run.Cancel();
        // Escape stops a running copy first (a local cancel), then closes the dialog.
        MeowChan.Desktop.Controls.DialogEscape.Attach(dialog, tryCancelLocal: () =>
        {
            if (!running) return false;
            run.Cancel();
            return true;
        });
        openFolder.Click += async (_, _) =>
        {
            string? path = destination.Text;
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (OpenExportFolder != null) await OpenExportFolder(path);
                else _openFolder?.Invoke(path);
            }
            catch (Exception ex) { Fail(Texts.T("export.openFailed", ex.Message)); }
        };
        browse.Click += async (_, _) =>
        {
            IStorageProvider? storage = owner.StorageProvider;
            if (storage == null || !storage.CanPickFolder) { Fail(Texts.T("settings.pickerUnavailable")); return; }
            IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = Texts.T("viewer.export.destination"), AllowMultiple = false });
            try
            {
                if (run.IsCancellationRequested) return;
                if (folders.Count == 0) return;
                string? path = folders[0].TryGetLocalPath();
                if (path == null) { Fail(Texts.T("settings.pickerLocalOnly")); return; }
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
            Notice(Texts.T("viewer.export.running"));
            try
            {
                FavoriteExportResult result = await FavoriteOriginalExporter.ExportAsync(assets, destination.Text, ExportFilenameTemplate?.Invoke(), run.Token);
                Notice(Texts.T("viewer.export.result", result.Copied, result.AlreadyExists,
                    result.Missing + missingCount, result.Failed, result.Cancelled));
                // As in the thread dialog: after the copy Close is the primary action (Start stays spent).
                start.Classes.Set("primary", false);
                cancel.Classes.Set("subtle", false);
                cancel.Classes.Set("primary", true);
            }
            catch (OperationCanceledException) when (run.IsCancellationRequested) { Notice(Texts.T("viewer.export.cancelled")); }
            catch (Exception ex) { Fail(ex.Message); }
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
    }
}
