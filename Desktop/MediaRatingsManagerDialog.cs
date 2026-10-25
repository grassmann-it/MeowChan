using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Ratings;

namespace MeowChan.Desktop;

/// <summary>Metadata only: no archive lookup, thumbnail, filesystem resolution or network.</summary>
internal sealed class MediaRatingsManagerDialog : Window
{
    private sealed record Row(MediaRatingEntry Entry)
    {
        public override string ToString() => MediaRatingText.Identity(Entry.Key) + " · " + MediaRatingText.Value(Entry.Value);
    }
    private readonly DesktopSessionHost _host;
    private IReadOnlyList<MediaRatingEntry> _entries = Array.Empty<MediaRatingEntry>();
    private readonly TextBox _search = new() { Name = "MediaRatingsSearch" };
    private readonly ListBox _list = new() { Name = "MediaRatingsList" };
    private readonly TextBox _identity = new() { Name = "MediaRatingsIdentity", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { Name = "MediaRatingsSummary", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _remove;
    private readonly Button _confirm = new() { Name = "MediaRatingsConfirmRemove", IsVisible = false };
    private readonly Button _cancel = new() { IsVisible = false };
    private readonly Button _close = new();
    private MediaRatingEntry? _frozen;
    private Control? _confirmationOrigin;
    private bool _busy;
    internal MediaRatingsManagerDialog(DesktopSessionHost host)
    {
        _host = host; Title = CommandCatalog.Label("media.rating.manage"); Name = "MediaRatingsManager";
        Width = 700; Height = 500; MinWidth = 340; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _remove = CommandBindings.CreateButton("media.rating.remove-selected", BeginRemove, () => !_busy && _frozen == null && _list.SelectedItem is Row);
        _remove.Name = "MediaRatingsRemove"; _remove.IsEnabled = false;
        _search.PlaceholderText = MediaRatingText.Get("Search identities", "Identitäten durchsuchen");
        AutomationProperties.SetName(_search, _search.PlaceholderText);
        _search.TextChanged += (_, _) => Filter();
        _summary.Classes.Add("workspace-meta");
        _list.ItemTemplate = new FuncDataTemplate<Row>((row, _) =>
        {
            if (row == null) return new TextBlock();
            TextBlock value = new() { Text = MediaRatingText.Value(row.Entry.Value) };
            value.Classes.Add("section-title");
            var key = row.Entry.Key;
            TextBlock target = new() { Text = $"{key.Thread.Source} /{key.Thread.Board}/ {key.Thread.Thread} · {key.MediaIdentifier} · {key.Variant}", TextWrapping = TextWrapping.Wrap };
            target.Classes.Add("workspace-meta");
            StackPanel item = new() { Spacing = 4, Margin = new Thickness(4, 8), Children = { value, target } };
            AutomationProperties.SetName(item, row.ToString());
            return item;
        });
        _list.SelectionChanged += (_, _) =>
        {
            if (_frozen != null) return;
            _identity.Text = (_list.SelectedItem as Row)?.ToString() ?? string.Empty;
            CommandBindings.Refresh(_remove);
        };
        _confirm.Content = MediaRatingText.Get("Remove this rating", "Diese Bewertung entfernen");
        _cancel.Content = MediaRatingText.Get("Cancel", "Abbrechen");
        _close.Content = MediaRatingText.Get("Close", "Schließen");
        _confirm.Click += async (_, _) => await ConfirmAsync();
        _cancel.Click += (_, _) => EndConfirmation();
        _close.Click += (_, _) => Close();
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive); _error.Classes.Add("workspace-error");
        _remove.Classes.Add("danger"); _confirm.Classes.Add("danger");
        _identity.MaxHeight = 120;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_identity, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        StackPanel detail = new() { Spacing = 8, Children = { _identity, _error } };
        WrapPanel buttons = new() { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (Button button in new[] { _remove, _confirm, _cancel, _close }) buttons.Children.Add(button);
        Border footer = new() { Child = buttons, Margin = new Thickness(-16, 0, -16, -16) };
        footer.Classes.Add("workspace-dialog-footer");
        StackPanel header = new() { Spacing = 8, Children = { _search, _summary } };
        Grid grid = new() { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 12 };
        grid.Children.Add(header); grid.Children.Add(_list); Grid.SetRow(_list, 1);
        grid.Children.Add(detail); Grid.SetRow(detail, 2); grid.Children.Add(footer); Grid.SetRow(footer, 3); Content = grid;
        CommandRouter router = new(this);
        router.Register(new("media.rating.remove-selected", this, () => IsVisible, () => !_busy && _frozen == null && _list.SelectedItem is Row, BeginRemove, Priority: 300));
        KeyDown += (_, e) => router.Route(e);
        Closed += (_, _) => router.Dispose();
        Closing += (_, e) => e.Cancel = _busy;
        KeyDown += (_, e) => { if (e.Key != Key.Escape) return; e.Handled = true; if (_busy) return; if (_frozen != null) EndConfirmation(); else Close(); };
        Opened += (_, _) => { Reload(); _search.Focus(); };
    }
    private void Reload()
    {
        try { _entries = _host.GetMediaRatings(); Filter(); }
        catch (Exception error) { _error.Text = MediaRatingText.LoadError(error); _remove.IsEnabled = false; }
    }
    private void Filter()
    {
        if (_frozen != null || _busy) return;
        string query = _search.Text ?? string.Empty;
        Row[] visible = _entries.Select(entry => new Row(entry)).Where(row => row.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _list.ItemsSource = visible;
        _summary.Text = visible.Length == 0
            ? MediaRatingText.Get(query.Length == 0 ? "No saved ratings." : "No matching ratings.", query.Length == 0 ? "Keine gespeicherten Bewertungen." : "Keine passenden Bewertungen.")
            : MediaRatingText.Get($"{visible.Length} shown · {_entries.Count} saved ratings", $"{visible.Length} angezeigt · {_entries.Count} gespeicherte Bewertungen");
        _list.SelectedItem = null; _identity.Text = string.Empty; _remove.IsEnabled = false;
    }
    private void BeginRemove()
    {
        if (_busy || _list.SelectedItem is not Row selected) return;
        _confirmationOrigin = FocusManager?.GetFocusedElement() as Control;
        _frozen = selected.Entry;
        _identity.Text = MediaRatingText.Get("Remove rating for this exact identity?", "Bewertung für genau diese Identität entfernen?") + "\n" + selected;
        _list.IsEnabled = _search.IsEnabled = _remove.IsEnabled = false;
        _confirm.IsVisible = _cancel.IsVisible = true; _cancel.Focus();
    }
    private void EndConfirmation(bool restoreFocus = true)
    {
        _frozen = null; _confirm.IsVisible = _cancel.IsVisible = false;
        _list.IsEnabled = _search.IsEnabled = true;
        _identity.Text = (_list.SelectedItem as Row)?.ToString() ?? string.Empty;
        CommandBindings.Refresh(_remove);
        Control? origin = _confirmationOrigin; _confirmationOrigin = null;
        if (!restoreFocus || !IsVisible) return;
        if (origin?.IsEffectivelyVisible == true && origin.IsEffectivelyEnabled && TopLevel.GetTopLevel(origin) == this)
            origin.Focus();
        else if (_remove.IsEnabled) _remove.Focus();
        else _search.Focus();
    }
    private async Task ConfirmAsync()
    {
        if (_busy || _frozen is not MediaRatingEntry entry) return;
        _busy = true; _confirm.IsEnabled = _cancel.IsEnabled = _close.IsEnabled = false; _error.Text = string.Empty;
        bool success = false;
        try { await _host.SetMediaRatingAsync(entry.Key, null); success = true; }
        catch (Exception error) { _error.Text = MediaRatingText.RemoveError(error); }
        finally { _busy = false; _confirm.IsEnabled = _cancel.IsEnabled = _close.IsEnabled = true; }
        if (success) { EndConfirmation(restoreFocus: false); Reload(); if (IsVisible) _search.Focus(); }
    }
}
