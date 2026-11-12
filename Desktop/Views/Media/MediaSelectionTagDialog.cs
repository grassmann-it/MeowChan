using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// Adds or removes one own tag on the occurrences of the media page selection. Every occurrence of a tile is listed
/// and chosen by default; a deselected occurrence is left alone. Tags stay with each occurrence, threads are not tagged.
/// </summary>
internal sealed class MediaSelectionTagDialog : Window
{
    private readonly MediaSelectionTagSession _session;
    private readonly int _tiles;
    private readonly WindowChromeBar _chrome = new() { Name = "MediaSelectionTagChrome" };
    private readonly TextBlock _heading = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _scope = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _occurrencesLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _all = new() { Name = "MediaSelectionTagAll" };
    private readonly Button _none = new() { Name = "MediaSelectionTagNone" };
    private readonly StackPanel _occurrences = new() { Name = "MediaSelectionOccurrences", Spacing = 4 };
    private readonly List<(CheckBox Box, TextBlock Text)> _rows = new();
    private readonly TextBlock _removeLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _removable = new() { Name = "MediaSelectionTagRemovable", ItemSpacing = 6, LineSpacing = 6 };
    private readonly TextBlock _addLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _addable = new() { Name = "MediaSelectionTagAddable", ItemSpacing = 6, LineSpacing = 6 };
    private readonly TextBox _input = new() { Name = "MediaSelectionTagInput" };
    private readonly Button _add = new() { Name = "MediaSelectionTagAdd" };
    private readonly TextBlock _status = new() { Name = "MediaSelectionTagStatus", TextWrapping = TextWrapping.Wrap };
    private readonly Button _close = new() { Name = "MediaSelectionTagClose" };
    private bool _closed;
    private bool _syncing;

    internal MediaSelectionTagSession Session => _session;
    internal IReadOnlyList<CheckBox> OccurrenceBoxes => _rows.Select(row => row.Box).ToArray();
    internal WrapPanel RemovablePanel => _removable;
    internal WrapPanel AddablePanel => _addable;
    internal TextBox Input => _input;
    internal Button AddButton => _add;
    internal TextBlock Status => _status;

    internal MediaSelectionTagDialog(MediaSelectionTagSession session, int tiles)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _tiles = tiles;
        Name = "MediaSelectionTagDialog";
        Width = 560; Height = 640; MinWidth = 380; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true; ExtendClientAreaTitleBarHeightHint = 48;
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        for (int index = 0; index < session.Candidates.Count; index++)
        {
            int at = index;
            TextBlock text = new() { TextWrapping = TextWrapping.Wrap };
            CheckBox box = new() { Name = "MediaSelectionOccurrence", Content = text };
            box.IsCheckedChanged += (_, _) => { if (!_syncing) _session.Choose(at, box.IsChecked == true); };
            _rows.Add((box, text));
            _occurrences.Children.Add(box);
        }
        ScrollViewer list = new() { Content = _occurrences, MaxHeight = 200, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Border listFrame = new() { Child = list, Padding = new Thickness(8), Classes = { "panel" } };
        WrapPanel choose = new() { ItemSpacing = 6, LineSpacing = 6, Children = { _all, _none } };
        Grid input = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        input.Children.Add(_input); input.Children.Add(_add); Grid.SetColumn(_add, 1);
        StackPanel body = new() { Spacing = 10, Margin = new Thickness(16) };
        foreach (Control control in new Control[] { _heading, _scope, _occurrencesLabel, choose, listFrame, _removeLabel, _removable, _addLabel, _addable, input, _status })
            body.Children.Add(control);
        WrapPanel footer = new() { Margin = new Thickness(16, 0, 16, 16), HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(_close);
        Grid layout = new() { RowDefinitions = new RowDefinitions("48,*,Auto") };
        layout.Children.Add(_chrome);
        ScrollViewer scroll = new() { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        layout.Children.Add(scroll); Grid.SetRow(scroll, 1);
        layout.Children.Add(footer); Grid.SetRow(footer, 2);
        Content = layout;

        _all.Click += (_, _) => _session.ChooseAll(true);
        _none.Click += (_, _) => _session.ChooseAll(false);
        _add.Click += OnAdd; _close.Click += OnClose;
        _input.AddHandler(KeyDownEvent, OnInputKey, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Opened += OnOpened; Closed += OnClosed;
        _session.Updated += OnSessionUpdated; Texts.Changed += OnLanguageChanged;
        Refresh();
    }

    private void OnOpened(object? sender, EventArgs e) { _chrome.Attach(this); _input.Focus(); }
    private void OnSessionUpdated() => Dispatcher.UIThread.Post(Refresh);
    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        try { await AddTypedAsync(); }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.selection.tags.add")) { /* Logged and shown. */ }
    }

    private async System.Threading.Tasks.Task AddTypedAsync()
    {
        string name = _input.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return;
        await _session.AddAsync(name);
        // Keep a rejected draft for correction; clear it once it became a tag on the chosen files.
        if (_session.MessageKey == null && !_closed) _input.Text = string.Empty;
    }

    private async void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        try { await AddTypedAsync(); }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.selection.tags.add")) { /* Logged and shown. */ }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        Close();
    }

    internal void CloseForLifetime() { if (!_closed) Close(); }

    private string Describe(MediaTagCandidate candidate)
    {
        MediaLibraryEntry entry = candidate.Entry;
        string where = Texts.T("media.filter.boardOfSource", entry.Thread.Board, entry.Thread.Source) + " · "
            + (string.IsNullOrWhiteSpace(entry.ThreadTitle) ? "#" + entry.Thread.Thread : entry.ThreadTitle);
        string text = (entry.Name ?? entry.MediaId ?? string.Empty) + " · " + where;
        return candidate.Block switch
        {
            MediaTagBlock.NotSaved => text + " — " + Texts.T("media.tile.missing"),
            MediaTagBlock.NoPostVersion => text + " — " + Texts.T("tags.media.boundElsewhere"),
            MediaTagBlock.ProviderMedium => text + " — " + Texts.T("booru.media.tags.unavailable"),
            _ => text
        };
    }

    private void Refresh()
    {
        if (_closed) return;
        Title = Texts.T("media.selection.tag.title");
        _heading.Text = Texts.T("media.selection.tag.heading", _tiles, _session.Candidates.Count);
        _scope.Text = Texts.T("media.selection.tag.scope");
        _occurrencesLabel.Text = Texts.T("media.selection.tag.occurrences", _session.Chosen.Count, _session.Candidates.Count);
        _removeLabel.Text = Texts.T("media.selection.tag.removeLabel");
        _addLabel.Text = Texts.T("media.selection.tag.addLabel");
        _input.Watermark = Texts.T("tags.input");
        AutomationProperties.SetName(_input, Texts.T("tags.input"));
        _add.Content = Texts.T("tags.add"); AutomationProperties.SetName(_add, Texts.T("tags.add"));
        _all.Content = Texts.T("media.selection.tag.all"); _none.Content = Texts.T("media.selection.tag.none");
        _close.Content = Texts.T("tags.media.close"); AutomationProperties.SetName(_close, Texts.T("tags.media.close"));
        AutomationProperties.SetName(_occurrences, Texts.T("media.selection.tag.occurrences", _session.Chosen.Count, _session.Candidates.Count));
        AutomationProperties.SetName(_removable, Texts.T("media.selection.tag.removeLabel"));
        AutomationProperties.SetName(_addable, Texts.T("media.selection.tag.addLabel"));

        bool ready = _session.Snapshot != null && !_session.Busy;
        bool chosen = _session.Chosen.Count > 0;
        bool focusInChips = _removable.IsKeyboardFocusWithin || _addable.IsKeyboardFocusWithin;
        _syncing = true;
        try
        {
            for (int index = 0; index < _rows.Count; index++)
            {
                (CheckBox box, TextBlock text) = _rows[index];
                MediaTagCandidate candidate = _session.Candidates[index];
                text.Text = Describe(candidate);
                box.IsChecked = _session.IsChosen(index);
                box.IsEnabled = candidate.Subject != null && !_session.Busy;
                AutomationProperties.SetName(box, text.Text);
            }
        }
        finally { _syncing = false; }
        _all.IsEnabled = _none.IsEnabled = !_session.Busy;

        _removable.Children.Clear();
        foreach ((PrivateTagDefinition tag, int count) in _session.Removable())
        {
            TagId id = tag.Id;
            Button chip = new() { Content = new TextBlock { Text = tag.Name.Display + " (" + count + ")  ✕" }, IsEnabled = ready && chosen };
            string hint = Texts.T("media.selection.tag.remove", tag.Name.Display, count);
            AutomationProperties.SetName(chip, hint); ToolTip.SetTip(chip, hint);
            chip.Click += async (_, _) => await _session.RemoveAsync(id);
            _removable.Children.Add(chip);
        }
        if (_removable.Children.Count == 0)
            _removable.Children.Add(new TextBlock { Text = Texts.T("media.selection.tag.none.assigned"), VerticalAlignment = VerticalAlignment.Center });

        _addable.Children.Clear();
        IReadOnlyList<(PrivateTagDefinition Tag, int Count)> addable = _session.Addable();
        foreach ((PrivateTagDefinition tag, int count) in addable)
        {
            TagId id = tag.Id;
            Button option = new() { Content = new TextBlock { Text = "+ " + tag.Name.Display + " (" + count + ")" }, IsEnabled = ready && chosen };
            string hint = Texts.T("media.selection.tag.add", tag.Name.Display, count);
            AutomationProperties.SetName(option, hint); ToolTip.SetTip(option, hint);
            option.Click += async (_, _) => await _session.AddExistingAsync(id);
            _addable.Children.Add(option);
        }
        _addLabel.IsVisible = _addable.IsVisible = addable.Count > 0;

        _input.IsEnabled = _add.IsEnabled = ready && chosen;
        _status.Text = _session.Snapshot == null ? Texts.T("tags.unavailable")
            : _session.MessageKey != null ? Texts.T(_session.MessageKey) : string.Empty;
        _status.IsVisible = _status.Text.Length > 0;
        // The acted-on chip is gone after a rebuild; keep keyboard users in the dialog.
        if (focusInChips && ready) _input.Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        _session.Updated -= OnSessionUpdated; Texts.Changed -= OnLanguageChanged;
        _add.Click -= OnAdd; _close.Click -= OnClose;
        _input.RemoveHandler(KeyDownEvent, OnInputKey); RemoveHandler(KeyDownEvent, OnKey);
        Opened -= OnOpened; Closed -= OnClosed;
    }
}
