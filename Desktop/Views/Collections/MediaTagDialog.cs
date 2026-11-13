using System;
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
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan;

/// <summary>
/// Own tags of one exact media version, opened from the gallery. Assigned tags are removable chips,
/// existing tags are one click away, and a new name can be typed. The target never changes while open.
/// </summary>
internal sealed class MediaTagDialog : Window
{
    private readonly MediaTagSession _session;
    private readonly string _mediaLabel;
    private readonly WindowChromeBar _chrome = new() { Name = "MediaTagChrome" };
    private readonly TextBlock _heading = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _scope = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _assignedLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _assigned = new() { Name = "MediaTagAssigned", ItemSpacing = 6, LineSpacing = 6 };
    private readonly TextBlock _availableLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _available = new() { Name = "MediaTagAvailable", ItemSpacing = 6, LineSpacing = 6 };
    private readonly IdentitySuggestionBox _input = new() { Name = "MediaTagInput" };
    private readonly Button _add = new() { Name = "MediaTagAdd" };
    private readonly TextBlock _status = new() { Name = "MediaTagStatus", TextWrapping = TextWrapping.Wrap };
    private readonly Button _close = new() { Name = "MediaTagClose" };
    private bool _closed;
    private readonly SourceTagSuggestions _sources = new();

    internal MediaTagSession Session => _session;
    internal WrapPanel AssignedPanel => _assigned;
    internal WrapPanel AvailablePanel => _available;
    internal IdentitySuggestionBox Input => _input;
    internal Button AddButton => _add;
    /// <summary>Danbooru source tags offered below the own tags, read once when the dialog opens.</summary>
    internal SourceTagSuggestions SourceTags => _sources;
    internal TextBlock Status => _status;

    internal MediaTagDialog(MediaTagSession session, string mediaLabel, Func<MeowChan.Archive.BooruCatalogSnapshot?>? readSourceTags = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _sources.Read = readSourceTags;
        _mediaLabel = mediaLabel ?? string.Empty;
        Name = "MediaTagDialog";
        Width = 480; Height = 460; MinWidth = 340; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true; ExtendClientAreaTitleBarHeightHint = 48;
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");

        var input = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        input.Children.Add(_input); input.Children.Add(_add); Grid.SetColumn(_add, 1);
        // UX103 P7 frame: medium and scope on top, the (error-only) status in the local strip,
        // chips and input in the scrolling body, Close in the fixed footer.
        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(_heading); header.Children.Add(_scope);
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(16, 0, 16, 8) };
        foreach (Control control in new Control[] { _assignedLabel, _assigned, _availableLabel, _available, input })
            body.Children.Add(control);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(_close);
        // Every status of this dialog is a failure (unavailable index or refused change).
        var frame = DialogFrame.Build(header, body, footer, DialogFrame.ErrorStrip(_status));
        var layout = new Grid { RowDefinitions = new RowDefinitions("48,*") };
        layout.Children.Add(_chrome);
        layout.Children.Add(frame); Grid.SetRow(frame, 1);
        Content = layout;

        _add.Click += OnAdd; _close.Click += OnClose;
        // Own tags as suggestions while typing; the tags of this medium are not offered again.
        TagSuggestionInput.Attach(_input, () => _session.Busy ? null : _session.Snapshot,
            () => _session.Assigned.Select(tag => tag.Id).ToHashSet(), () => _ = AddTypedAsync(), _sources);
        _input.AddHandler(KeyDownEvent, OnInputKey, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Opened += OnOpened; Closed += OnClosed;
        _session.Updated += OnSessionUpdated; Texts.Changed += OnLanguageChanged;
        Refresh();
    }

    private void OnOpened(object? sender, EventArgs e) { _chrome.Attach(this); _sources.Load(); _input.Focus(); }
    private void OnSessionUpdated() => Dispatcher.UIThread.Post(Refresh);
    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private async void OnAdd(object? sender, RoutedEventArgs e) => await AddTypedAsync();

    private async System.Threading.Tasks.Task AddTypedAsync()
    {
        string name = _input.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return;
        await _session.AddAsync(name);
        // Keep a rejected draft for correction; clear it once it became a tag on this medium.
        if (_session.MessageKey == null && !_closed) _input.Text = string.Empty;
    }

    // Runs after the field's own handler: an Enter that took a highlighted suggestion is already handled there.
    private async void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        _input.Dismiss();
        await AddTypedAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        // The first Escape closes an open suggestion list, the next one the dialog.
        if (TagSuggestionInput.Route(_input, e) == TagFieldKey.CloseList) _input.Dismiss();
        else Close();
    }

    internal void CloseForLifetime() { if (!_closed) Close(); }

    private void Refresh()
    {
        if (_closed) return;
        Title = Texts.T("tags.media.title");
        _heading.Text = Texts.T("tags.media.heading", _mediaLabel);
        _scope.Text = Texts.T(_session.IsLocal ? "tags.media.scopeLocal" : "tags.media.scope");
        _assignedLabel.Text = Texts.T("tags.media.assigned");
        _availableLabel.Text = Texts.T("tags.media.available");
        TagSuggestionInput.Localize(_input);
        _add.Content = Texts.T("tags.add"); AutomationProperties.SetName(_add, Texts.T("tags.add"));
        _close.Content = Texts.T("tags.media.close"); AutomationProperties.SetName(_close, Texts.T("tags.media.close"));
        AutomationProperties.SetName(_assigned, Texts.T("tags.media.assigned"));
        AutomationProperties.SetName(_available, Texts.T("tags.media.available"));

        bool ready = _session.Snapshot != null && !_session.Busy;
        bool focusInChips = _assigned.IsKeyboardFocusWithin || _available.IsKeyboardFocusWithin;
        _assigned.Children.Clear();
        foreach (PrivateTagDefinition tag in _session.Assigned)
        {
            TagId id = tag.Id;
            // A TextBlock keeps underscores in names literal instead of access-key markers.
            Button chip = new() { Content = new TextBlock { Text = tag.Name.Display + "  ✕" }, IsEnabled = ready };
            string hint = Texts.T("tags.media.remove", tag.Name.Display);
            AutomationProperties.SetName(chip, hint); ToolTip.SetTip(chip, hint);
            chip.Click += async (_, _) => await _session.RemoveAsync(id);
            _assigned.Children.Add(chip);
        }
        if (_session.Assigned.Count == 0)
            _assigned.Children.Add(new TextBlock { Text = Texts.T("tags.media.none"), VerticalAlignment = VerticalAlignment.Center });

        _available.Children.Clear();
        foreach (PrivateTagDefinition tag in _session.Available)
        {
            TagId id = tag.Id;
            Button option = new() { Content = new TextBlock { Text = "+ " + tag.Name.Display }, IsEnabled = ready };
            string hint = Texts.T("tags.media.addExisting", tag.Name.Display);
            AutomationProperties.SetName(option, hint); ToolTip.SetTip(option, hint);
            option.Click += async (_, _) => await _session.AddExistingAsync(id);
            _available.Children.Add(option);
        }
        _availableLabel.IsVisible = _available.IsVisible = _session.Available.Count > 0;

        _input.IsEnabled = _add.IsEnabled = ready;
        if (!ready) _input.Dismiss();
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
        _input.EndSuggestions(); _sources.End();
        _add.Click -= OnAdd; _close.Click -= OnClose;
        _input.RemoveHandler(KeyDownEvent, OnInputKey); RemoveHandler(KeyDownEvent, OnKey);
        Opened -= OnOpened; Closed -= OnClosed;
    }
}
