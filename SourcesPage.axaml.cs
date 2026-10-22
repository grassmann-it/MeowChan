using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SourcesPage : UserControl
{
    private readonly Func<DiagnosticTarget, bool>? _openWatch;
    private readonly Action<string>? _editRules;
    private readonly Action? _openDiagnostics;
    private readonly Action? _openDownloads;
    private readonly Func<string, Task<bool>>? _submit;
    private IReadOnlyList<SourceRow> _rows = Array.Empty<SourceRow>();
    private sealed record SourceCard(Button Control, TextBlock Name, TextBlock Address, TextBlock Observation);
    private readonly Dictionary<string, SourceCard> _sourceCards = new Dictionary<string, SourceCard>(StringComparer.Ordinal);
    private string? _selectedId;
    private bool _available;
    private bool _busy;
    private bool _ended;
    private bool _hasUrlResult;
    private bool _showDetails;
    private bool _narrow;
    internal Action<string>? OpenPluginSettings { get; set; }
    internal bool IsBusy => _busy;
    internal event EventHandler? BusyChanged;

    public SourcesPage() { InitializeComponent(); InitializeContextMenus(); }
    internal SourcesPage(Func<DiagnosticTarget, bool> openWatch, Action<string> editRules,
        Action openDiagnostics, Action openDownloads, Func<string, Task<bool>> submit) : this()
    {
        _openWatch = openWatch;
        _editRules = editRules;
        _openDiagnostics = openDiagnostics;
        _openDownloads = openDownloads;
        _submit = submit;
    }

    internal void Refresh(IReadOnlyList<ProviderDescriptor> providers, DesktopSessionView view, bool available)
    {
        if (_ended) return;
        _available = available;
        _rows = SourcesSnapshot.From(providers, view);
        if (!_rows.Any(row => row.Id == _selectedId)) _selectedId = _rows.FirstOrDefault()?.Id;
        this.FindControl<TextBlock>("SourceCount")!.Text = Texts.T("sources.count", _rows.Count);
        this.FindControl<TextBlock>("SourcesEmpty")!.IsVisible = _rows.Count == 0;
        StackPanel cards = this.FindControl<StackPanel>("SourceCards")!;
        foreach (string removed in _sourceCards.Keys.Where(id => !_rows.Any(row => row.Id == id)).ToArray())
        {
            cards.Children.Remove(_sourceCards[removed].Control);
            _sourceCards.Remove(removed);
        }
        foreach (SourceRow row in _rows)
        {
            if (!_sourceCards.TryGetValue(row.Id, out SourceCard? card))
            {
                TextBlock name = new TextBlock { FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold };
                TextBlock address = new TextBlock(); address.Classes.Add("muted");
                TextBlock observation = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; observation.Classes.Add("muted");
                StackPanel labels = new StackPanel { Spacing = 4 };
                labels.Children.Add(name); labels.Children.Add(address); labels.Children.Add(observation);
                Button select = new Button { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Padding = new Thickness(14) };
                string sourceId = row.Id;
                select.Click += (_, _) =>
                {
                    _selectedId = sourceId;
                    _showDetails = true;
                    this.FindControl<TextBlock>("SourcesActionError")!.Text = string.Empty;
                    RenderDetails();
                };
                select.Content = labels;
                AttachSourceContextMenu(select, sourceId);
                card = new SourceCard(select, name, address, observation);
                _sourceCards.Add(row.Id, card); cards.Children.Add(select);
            }
            card.Name.Text = (row.Id == _selectedId ? "● " : "") + row.Name;
            card.Address.Text = row.Address;
            card.Observation.Text = row.Summary;
            card.Control.IsEnabled = !_busy;
        }
        RenderDetails();
    }

    private SourceRow? Selected => _rows.FirstOrDefault(row => row.Id == _selectedId);
    private void RenderDetails()
    {
        foreach (KeyValuePair<string, SourceCard> source in _sourceCards)
        {
            source.Value.Name.Text = (source.Key == _selectedId ? "● " : "")
                + (_rows.FirstOrDefault(row => row.Id == source.Key)?.Name ?? source.Key);
            source.Value.Control.Classes.Set("selected", source.Key == _selectedId);
        }
        SourceRow? row = Selected;
        this.FindControl<Border>("SourceDetailsPanel")!.IsVisible = row != null && (!_narrow || _showDetails);
        this.FindControl<StackPanel>("SourcesListPane")!.IsVisible = !_narrow || !_showDetails || row == null;
        this.FindControl<Button>("SourceBackButton")!.IsVisible = _narrow;
        foreach (Control control in this.GetLogicalDescendants().OfType<Control>())
        {
            if (control.Tag is not string key || !control.Classes.Contains("localized")) continue;
            if (control is TextBlock text) text.Text = Texts.T(key);
            if (control is Button button && !button.Classes.Contains("command-icon")) button.Content = Texts.T(key);
        }
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceBackButton")!, "sources.back", Texts.T("nav.sources"));
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceRulesButton")!, "rules.edit-scope", Texts.T("sources.rules"));
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceDiagnosticsButton")!, "navigation.diagnostics", Texts.T("sources.diagnostics"));
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceDownloadsButton")!, "navigation.downloads", Texts.T("nav.downloads"));
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceAddButton")!, "watch.add-url", Texts.T("action.add"));
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceOpenWatchButton")!, "watch.show-in-downloads", Texts.T("diagnostics.openWatch"));
        if (row == null) return;
        this.FindControl<Button>("SourcePluginButton")!.Content = Texts.T("plugins.manageSource");
        this.FindControl<Button>("SourcePluginButton")!.IsEnabled = OpenPluginSettings != null;
        this.FindControl<TextBlock>("SourceHeading")!.Text = row.Name;
        this.FindControl<TextBlock>("SourceIdentity")!.Text = Texts.T("sources.identity", row.Id, row.Address);
        Button website = this.FindControl<Button>("SourceWebsiteButton")!;
        CommandBindings.ApplyLabeled(website, "location.open-source", Texts.T("sources.website", row.Address));
        website.IsEnabled = !_busy;
        this.FindControl<TextBlock>("SourceHosts")!.Text = Texts.T("sources.hosts", row.Descriptor.SupportedHosts.Count > 0
            ? string.Join(", ", row.Descriptor.SupportedHosts) : Texts.T("sources.unspecified"));
        this.FindControl<TextBlock>("SourceVersion")!.Text = Texts.T("sources.version", row.Descriptor.AdapterVersion ?? Texts.T("sources.unspecified"));
        this.FindControl<Expander>("AdapterDetails")!.Header = Texts.T("sources.details");
        WrapPanel capabilities = this.FindControl<WrapPanel>("CapabilityCards")!;
        capabilities.Children.Clear();
        foreach ((ProviderCapabilities capability, string label) in new[]
        {
            (ProviderCapabilities.ThreadWatch, "sources.threads"), (ProviderCapabilities.BoardCatalog, "sources.catalog"),
            (ProviderCapabilities.PostCapture, "sources.capture"), (ProviderCapabilities.OriginalMedia, "sources.originals"),
            (ProviderCapabilities.Previews, "sources.previews")
        })
        {
            string state = row.Descriptor.Capabilities is not ProviderCapabilities supported ? "sources.unspecified"
                : supported.HasFlag(capability) ? "sources.supported" : "sources.unsupported";
            StackPanel labels = new StackPanel { Spacing = 5 };
            labels.Children.Add(new TextBlock { Text = Texts.T(label), FontWeight = Avalonia.Media.FontWeight.SemiBold });
            TextBlock value = new TextBlock { Text = Texts.T(state) }; value.Classes.Add("muted"); labels.Children.Add(value);
            Border card = new Border { Child = labels, Padding = new Thickness(12), Width = 205, Margin = new Thickness(0, 0, 8, 8) };
            card.Classes.Add("panel"); capabilities.Children.Add(card);
        }
        this.FindControl<TextBlock>("SourceCaptureAvailability")!.Text = Texts.T(row.Descriptor.CaptureAvailable
            ? "sources.captureAvailable" : "sources.captureUnavailable");
        this.FindControl<TextBlock>("SourceWatchSummary")!.Text = row.Summary;
        this.FindControl<TextBox>("SourceUrlInput")!.PlaceholderText = Texts.T("downloads.urlPlaceholder");
        this.FindControl<TextBlock>("SourceUrlResult")!.Text = _hasUrlResult ? Texts.T("sources.urlResult") : string.Empty;
        DataGrid grid = this.FindControl<DataGrid>("SourceWatchesGrid")!;
        if (grid.ItemsSource is not IReadOnlyList<SourceWatchRow> displayed || !displayed.SequenceEqual(row.Watches))
        {
            DiagnosticTarget? selected = (grid.SelectedItem as SourceWatchRow)?.Target;
            grid.ItemsSource = row.Watches;
            grid.SelectedItem = row.Watches.FirstOrDefault(watch => watch.Target == selected);
        }
        grid.Columns[0].Header = Texts.T("diagnostics.resource");
        grid.Columns[1].Header = Texts.T("sources.work");
        grid.Columns[2].Header = Texts.T("sources.observation");
        foreach (string name in new[] { "SourceRulesButton", "SourceDiagnosticsButton", "SourceDownloadsButton", "SourceAddButton" })
            this.FindControl<Button>(name)!.IsEnabled = _available && !_busy;
        this.FindControl<TextBox>("SourceUrlInput")!.IsEnabled = _available && !_busy;
        RefreshWatchSelection();
    }

    private SourceWatchRow? SelectedWatch => this.FindControl<DataGrid>("SourceWatchesGrid")!.SelectedItem as SourceWatchRow;
    private void OnWatchSelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshWatchSelection();
    private void RefreshWatchSelection()
    {
        this.FindControl<Button>("SourceOpenWatchButton")!.IsEnabled = _available && !_busy && SelectedWatch != null;
        this.FindControl<TextBox>("SourceWatchDetails")!.Text = SelectedWatch is SourceWatchRow watch
            ? watch.Resource + Environment.NewLine + watch.Work + Environment.NewLine + watch.Observation : string.Empty;
    }
    private void OnPluginClick(object? sender, RoutedEventArgs e) { if (Selected is SourceRow row) OpenPluginSettings?.Invoke(row.Id); }
    internal void SelectSource(string sourceId) { if (_rows.Any(row => row.Id == sourceId)) { _selectedId = sourceId; _showDetails = true; RenderDetails(); } }
    private void OnRulesClick(object? sender, RoutedEventArgs e) { if (_available && !_busy && Selected is SourceRow row) _editRules?.Invoke(row.Id); }
    private void OnPageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // The detail grid needs 520 DIP after list, gutters and panel padding.
        bool narrow = Bounds.Width - 40 - 280 - 14 - 32 < 520;
        if (_narrow == narrow) return;
        _narrow = narrow;
        Grid split = this.FindControl<Grid>("SourcesSplit")!;
        // Preserve the track collection while SizeChanged runs inside layout.
        split.ColumnDefinitions[0].Width = new GridLength(narrow ? 0 : 280);
        split.ColumnSpacing = narrow ? 0 : 14;
        Grid.SetColumn(this.FindControl<StackPanel>("SourcesListPane")!, narrow ? 1 : 0);
        RenderDetails();
    }
    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        _showDetails = false;
        RenderDetails();
        if (_selectedId is string id && _sourceCards.TryGetValue(id, out SourceCard? card)) card.Control.Focus();
    }
    private async void OnWebsiteClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || _busy || Selected is not SourceRow row) return;
        try { await ExternalLauncher.OpenAsync(row.Address); }
        catch (Exception exception) { if (!_ended) this.FindControl<TextBlock>("SourcesActionError")!.Text = exception.Message; }
    }
    private void OnDiagnosticsClick(object? sender, RoutedEventArgs e) { if (_available && !_busy) _openDiagnostics?.Invoke(); }
    private void OnDownloadsClick(object? sender, RoutedEventArgs e) { if (_available && !_busy) _openDownloads?.Invoke(); }
    private void OnOpenWatchClick(object? sender, RoutedEventArgs e)
    {
        if (!_available || _busy || SelectedWatch == null) return;
        if (_openWatch?.Invoke(SelectedWatch.Target) != true)
            this.FindControl<TextBlock>("SourcesActionError")!.Text = Texts.T("diagnostics.removed");
    }
    private async void OnAddClick(object? sender, RoutedEventArgs e) => await SubmitAsync();
    private async void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SubmitAsync();
    }
    private async Task SubmitAsync()
    {
        if (_ended || !_available || _busy || _submit == null) return;
        string input = this.FindControl<TextBox>("SourceUrlInput")!.Text ?? string.Empty;
        _busy = true; BusyChanged?.Invoke(this, EventArgs.Empty); RenderDetails();
        try
        {
            bool accepted = await _submit(input);
            if (_ended) return;
            _hasUrlResult = true;
            if (accepted && this.FindControl<TextBox>("SourceUrlInput")!.Text == input)
                this.FindControl<TextBox>("SourceUrlInput")!.Text = string.Empty;
        }
        catch (Exception exception)
        {
            if (!_ended) this.FindControl<TextBlock>("SourcesActionError")!.Text = exception.Message;
        }
        finally
        {
            _busy = false;
            if (!_ended) { BusyChanged?.Invoke(this, EventArgs.Empty); RenderDetails(); }
        }
    }
    internal void EndLifetime() { _ended = true; _available = false; BusyChanged = null; }
}
