using System;
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
/// Own tags of one exact media version, opened from the gallery, the archive or the media page. Search first: the tag field
/// on top filters one virtualized list of toggle rows (on this file, most used, all others A–Z; or most used first) and adds a
/// typed name; Danbooru source tags appear only as the field's own suggestion group. A toggle writes at once and repaints
/// only its row; the last change can be undone once. The target never changes while open.
/// </summary>
internal sealed class MediaTagDialog : Window
{
    private readonly MediaTagSession _session;
    private readonly string _mediaLabel;
    // The medium this dialog tags ("Tags for {0}"): the subtitle under the title bar, not a repeat of the title.
    private readonly TextBlock _subject = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _scope = new() { TextWrapping = TextWrapping.Wrap };
    private readonly IdentitySuggestionBox _input = new() { Name = "MediaTagInput" };
    private readonly Button _add = new() { Name = "MediaTagAdd" };
    private readonly TextBlock _status = new() { Name = "MediaTagStatus", TextWrapping = TextWrapping.Wrap };
    private readonly Button _close = new() { Name = "MediaTagClose" };
    private readonly TagToggleSurface _surface;
    private bool _closed;
    private readonly SourceTagSuggestions _sources = new();

    internal MediaTagSession Session => _session;
    internal IdentitySuggestionBox Input => _input;
    internal Button AddButton => _add;
    internal TagToggleSurface Surface => _surface;
    internal TagToggleListBox TagList => _surface.List;
    internal Button UndoButton => _surface.Undo;
    /// <summary>Danbooru source tags offered below the own tags, read once when the dialog opens.</summary>
    internal SourceTagSuggestions SourceTags => _sources;
    internal TextBlock Status => _status;

    internal MediaTagDialog(MediaTagSession session, string mediaLabel, Func<MeowChan.Archive.BooruCatalogSnapshot?>? readSourceTags = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _sources.Read = readSourceTags;
        _mediaLabel = mediaLabel ?? string.Empty;
        Name = "MediaTagDialog";
        // Modals plan MD2: a modal form-step dialog (fitted to the screen); the title stands in the title bar only.
        DialogWindow.Configure(this, DialogSize.M, modal: true);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        _surface = new TagToggleSurface(false, "MediaTag", _input, () => _session.Snapshot, _session.Coverage,
            () => _session.LastChange, () => _session.Undone);

        var input = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        input.Children.Add(_input); input.Children.Add(_add); Grid.SetColumn(_add, 1);
        // UX103 P7 frame: medium and scope on top, the (error-only) status in the local strip, the tag field and the one
        // list in the body (the list scrolls itself, so the body fills instead of scrolling), undo and Close in the footer.
        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(_subject); header.Children.Add(_scope);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 8, Margin = new Thickness(16, 0, 16, 8) };
        body.Children.Add(input);
        body.Children.Add(_surface.SummaryRow); Grid.SetRow(_surface.SummaryRow, 1);
        body.Children.Add(_surface.ListArea); Grid.SetRow(_surface.ListArea, 2);
        // The last change's notice on the left, then Undo, and Close right-most.
        var footer = DialogFooter.Build(_close, new Control[] { _surface.Undo }, _surface.Notice);
        // Every status of this dialog is a failure (unavailable index or refused change).
        var frame = DialogFrame.Build(header, body, footer, DialogFrame.ErrorStrip(_status), fillBody: true);
        DialogWindow.Frame(this, frame, modal: true, captionName: "MediaTagChrome");

        _add.Click += OnAdd; _close.Click += OnClose; _surface.Undo.Click += OnUndo;
        _surface.List.Toggled += OnToggled;
        // Own tags as suggestions while typing; the tags of this medium are not offered again.
        TagSuggestionInput.Attach(_input, () => _session.Busy ? null : _session.Snapshot,
            () => _session.AssignedIds, () => _ = AddTypedAsync(), _sources);
        _input.AddHandler(KeyDownEvent, OnInputKey, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        DialogEscape.Attach(this);
        Opened += OnOpened; Closed += OnClosed;
        _session.Updated += OnSessionUpdated; Texts.Changed += OnLanguageChanged;
        Localize();
        Refresh();
    }

    private void OnOpened(object? sender, EventArgs e) { _sources.Load(); _input.Focus(); }
    private void OnSessionUpdated() => Dispatcher.UIThread.Post(Refresh);
    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { if (!_closed) { Localize(); Refresh(); } });

    private async void OnAdd(object? sender, RoutedEventArgs e) => await AddTypedAsync();

    private async System.Threading.Tasks.Task AddTypedAsync()
    {
        string name = _input.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return;
        await _session.AddAsync(name);
        // Keep a rejected draft for correction; clear it once it became a tag on this medium.
        if (_session.MessageKey == null && !_closed) _input.Text = string.Empty;
    }

    // A click or Space on a row: on → off, off → on. The session ignores it while a write is running.
    private async void OnToggled(TagToggleRow row)
    {
        if (row.Tag is not { } tag) return;
        try
        {
            if (row.State == TagToggleState.All) await _session.RemoveAsync(tag.Id);
            else await _session.AddExistingAsync(tag.Id);
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.tags.toggle")) { /* Logged and shown. */ }
    }

    private async void OnUndo(object? sender, RoutedEventArgs e)
    {
        if (_session.LastChange is not { } change) return;
        try
        {
            await _session.UndoAsync();
            if (!_closed) _surface.FocusAfterUndo(change.Tag);
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.tags.undo")) { /* Logged and shown. */ }
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
        // Every change is saved as it is made: Enter on a list row only closes.
        if (_surface.ClosesOnEnter(e)) { e.Handled = true; Close(); return; }
        // The first Escape closes an open suggestion list; the next one reaches the dialog policy (DialogEscape) and closes it.
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        if (TagSuggestionInput.Route(_input, e) != TagFieldKey.CloseList) return;
        e.Handled = true; _input.Dismiss();
    }

    internal void CloseForLifetime() { if (!_closed) Close(); }

    private void Localize()
    {
        Title = Texts.T("tags.media.title");
        _subject.Text = Texts.T("tags.media.heading", _mediaLabel);
        _scope.Text = Texts.T(_session.IsLocal ? "tags.media.scopeLocal" : "tags.media.scope");
        TagSuggestionInput.Localize(_input);
        _add.Content = Texts.T("tags.add"); AutomationProperties.SetName(_add, Texts.T("tags.add"));
        _close.Content = Texts.T("action.close"); AutomationProperties.SetName(_close, Texts.T("action.close"));
        _surface.Localize();
    }

    // Only states change here: rows that differ repaint, and the list is laid out again only for a new vocabulary.
    private void Refresh()
    {
        if (_closed) return;
        bool ready = _session.Snapshot != null && !_session.Busy;
        _surface.Sync(ready);
        _input.IsEnabled = _add.IsEnabled = ready;
        if (!ready) _input.Dismiss();
        _status.Text = _session.Snapshot == null ? Texts.T("tags.unavailable")
            : _session.MessageKey != null ? Texts.T(_session.MessageKey) : string.Empty;
        _status.IsVisible = _status.Text.Length > 0;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        _session.Updated -= OnSessionUpdated; Texts.Changed -= OnLanguageChanged;
        _input.EndSuggestions(); _sources.End(); _surface.Dispose();
        _add.Click -= OnAdd; _close.Click -= OnClose; _surface.Undo.Click -= OnUndo;
        _surface.List.Toggled -= OnToggled;
        _input.RemoveHandler(KeyDownEvent, OnInputKey); RemoveHandler(KeyDownEvent, OnKey);
        Opened -= OnOpened; Closed -= OnClosed;
    }
}
