using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// Main-page host of the complete workbench, without window chrome or a way back/close.
/// One page belongs to exactly one service bundle. The shell must dispose and replace it when
/// the session/profile composition changes, even if the replacement has the same provider id.
/// Temporary navigation/detachment is not the end of its lifetime.
/// </summary>
internal sealed class BooruPage : UserControl, IDisposable
{
    private readonly BooruWorkbenchView _view;
    private BooruTabPersistence? _tabPersistence;
    private bool _disposed;

    internal BooruPage(BooruWorkbenchServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Name = "BooruPage";
        BooruWorkbenchModel model = new(services);
        try
        {
            // Keep the view itself in the tree: focus, clipboard, clock and future media hooks
            // belong to it, not to three controls reparented out of the view.
            _view = new BooruWorkbenchView(model, ownsModel: true, buildLayout: BuildPageLayout);
        }
        catch
        {
            model.Dispose();
            throw;
        }
        Content = _view;
        KeyDown += OnKeyDown;
    }

    /// <summary>Reference identity, not provider equality, is the shell's reuse criterion.</summary>
    internal BooruWorkbenchServices Services => _view.Model.Services;
    /// <summary>The actual hosted view, for the existing gallery/media callback wiring.</summary>
    internal BooruWorkbenchView View => _view;
    internal BooruWorkbenchModel Model => _view.Model;

    internal void RestoreSearchTabs(BooruSearchTabStore store, Config config) =>
        _tabPersistence = new BooruTabPersistence(_view, store, config);

    internal void FlushSearchTabs() => _tabPersistence?.Dispose();

    /// <summary>
    /// Explicit first-show hook. Delegates the view's once-only local watcher-list initialization;
    /// neither constructing nor showing this page submits a search or starts a watcher check.
    /// </summary>
    internal Task ActivateAsync() => _disposed ? Task.CompletedTask : _view.ActivateAsync();

    /// <summary>Shows an unsent query on the search tab; the user still has to press Search.</summary>
    internal void ShowQuery(string query)
    {
        if (!_disposed) _view.ShowQuery(query);
    }

    /// <summary>Applies the owning shell's work-view contract without replacing the workbench tree.</summary>
    internal void ApplyWorkView(WorkView view, bool viewChanged)
    {
        if (!_disposed) _view.ApplyWorkView(view, viewChanged);
    }

    internal void AddTagToSearch(string tag)
    {
        if (!_disposed) _view.AddTagToSearch(tag);
    }

    internal void FocusSearchInput()
    {
        if (!_disposed) _view.FocusSearchInput();
    }

    internal bool TryClearQuery() => !_disposed && _view.TryClearQuery();

    private static Control BuildPageLayout(Control header, Control body, Control footer)
    {
        // The existing parts retain every content name and action. Only the host layout changes:
        // normal page insets, an independently scrolling body, and the workbench's notes below.
        Grid layout = new() { Name = "BooruPageLayout", RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        header.Classes.Add("workspace-page-header");
        Border heading = new() { Name = "BooruPageHeader", Child = header, Margin = new Thickness(12, 8, 12, 4) };
        ScrollViewer scroll = new()
        {
            Name = "BooruPageScroll", Content = body, Padding = new Thickness(12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Border surface = new()
        {
            Name = "BooruPageBody", Child = scroll, Margin = new Thickness(12, 0, 12, 8),
            Classes = { "workspace-section-flush" }
        };
        footer.Margin = new Thickness(12, 0, 12, 8);
        Border notes = new() { Name = "BooruPageNotes", Child = footer };
        layout.Children.Add(heading);
        Grid.SetRow(surface, 1); layout.Children.Add(surface);
        Grid.SetRow(notes, 2); layout.Children.Add(notes);
        return layout;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_disposed || e.Handled || ShortcutService.IsCapturing || e.Key != Key.Escape
            || e.KeyModifiers != KeyModifiers.None || _view.IsModeDropDownOpen) return;
        // SearchBox normally consumes the first Escape. Do not clear a draft from elsewhere
        // on the page, and do not turn an empty query into navigation or a window close.
        if (SearchBox.OwnsEscape(e.Source) && TryClearQuery()) { e.Handled = true; return; }
        if (_view.TryCancelRemoval()) e.Handled = true;
    }

    internal void EndLifetime() => Dispose();

    /// <summary>Final shell teardown only; the owning view disposes its model and UI resources once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        KeyDown -= OnKeyDown;
        _tabPersistence?.Dispose();
        _view.Dispose();
    }
}
