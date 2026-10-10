using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using oChan.Desktop;
using oChan.Desktop.Localization;

namespace oChan;

public partial class SourcesPage : UserControl
{
    private readonly Func<DiagnosticTarget, bool>? _openWatch;
    private readonly Action<string>? _editRules;
    private readonly Action? _openDiagnostics;
    private readonly Action? _openDownloads;
    private readonly Func<string, Task<bool>>? _submit;
    private IReadOnlyList<SourceRow> _rows = Array.Empty<SourceRow>();
    private sealed record SourceCard(Border Container, TextBlock Name, TextBlock Address, TextBlock Observation, Button Select);
    private readonly Dictionary<string, SourceCard> _sourceCards = new Dictionary<string, SourceCard>(StringComparer.Ordinal);
    private string? _selectedId;
    private bool _available;
    private bool _busy;
    private bool _ended;
    private bool _hasUrlResult;
    internal bool IsBusy => _busy;
    internal event EventHandler? BusyChanged;

    public SourcesPage() { InitializeComponent(); }
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
            cards.Children.Remove(_sourceCards[removed].Container);
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
                Button select = new Button { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                string sourceId = row.Id;
                select.Click += (_, _) =>
                {
                    _selectedId = sourceId;
                    this.FindControl<TextBlock>("SourcesActionError")!.Text = string.Empty;
                    RenderDetails();
                };
                Grid heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
                heading.Children.Add(labels); Grid.SetColumn(select, 1); heading.Children.Add(select);
                Border container = new Border { Child = heading, Padding = new Thickness(14) }; container.Classes.Add("panel");
                card = new SourceCard(container, name, address, observation, select);
                _sourceCards.Add(row.Id, card); cards.Children.Add(container);
            }
            card.Name.Text = row.Name;
            card.Address.Text = row.Address;
            card.Observation.Text = row.Summary;
            card.Select.IsEnabled = !_busy;
        }
        RenderDetails();
    }

    private SourceRow? Selected => _rows.FirstOrDefault(row => row.Id == _selectedId);
    private void RenderDetails()
    {
        foreach (KeyValuePair<string, SourceCard> source in _sourceCards)
            source.Value.Select.Content = Texts.T(source.Key == _selectedId ? "sources.selected" : "sources.select");
        SourceRow? row = Selected;
        this.FindControl<Border>("SourceDetailsPanel")!.IsVisible = row != null;
        foreach (Control control in this.GetLogicalDescendants().OfType<Control>())
        {
            if (control.Tag is not string key || !control.Classes.Contains("localized")) continue;
            if (control is TextBlock text) text.Text = Texts.T(key);
            if (control is Button button) button.Content = Texts.T(key);
        }
        if (row == null) return;
        this.FindControl<TextBlock>("SourceHeading")!.Text = row.Name;
        this.FindControl<TextBlock>("SourceIdentity")!.Text = Texts.T("sources.identity", row.Id, row.Address);
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
    private void OnRulesClick(object? sender, RoutedEventArgs e) { if (_available && !_busy && Selected is SourceRow row) _editRules?.Invoke(row.Id); }
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
