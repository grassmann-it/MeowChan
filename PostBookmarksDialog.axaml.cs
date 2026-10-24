using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan;

internal partial class PostBookmarksDialog : Window
{
    private readonly DesktopSessionHost _owner;
    private readonly Func<PostBookmarkKey, CancellationToken, Task<bool>> _open;
    private readonly CancellationTokenSource _lifetime = new();
    private PostBookmarkSnapshot? _snapshot;
    private PostBookmarkKey? _key;
    private string _original = "";
    private bool _postEditor, _exists, _busy, _switching, _forceClose, _confirming, _conflicted, _orphanDraft;
    private int _resolveGeneration;
    private bool _targetAvailable;
    private Vector? _overviewOffset;
    private readonly SemaphoreSlim _rowResolves = new(4);
    private event Action? AvailabilityChanged;
    private event Action? RowLanguageChanged;
    private string? _statusKey;
    private bool _actionError;
    private object?[] _statusArguments = [];
    private readonly List<IDisposable> _registrations = new();
    private readonly CommandRouter _router;
    private ListBox List => this.FindControl<ListBox>("Bookmarks")!;
    private TextBox NoteInput => this.FindControl<TextBox>("Note")!;
    private TextBlock StatusText => this.FindControl<TextBlock>("Status")!;
    private Button B(string name) => this.FindControl<Button>(name)!;
    private bool Dirty
    {
        get
        {
            try { return PostBookmarkStore.NormalizeNote(NoteInput.Text ?? "") != _original; }
            catch (ArgumentException) { return true; }
        }
    }

    internal PostBookmarksDialog(DesktopSessionHost owner, Func<PostBookmarkKey, CancellationToken, Task<bool>> open)
    {
        AvaloniaXamlLoader.Load(this);
        _owner = owner; _open = open;
        Title = Texts.T("bookmarks.title");
        Texts.Changed += OnLanguageChanged;
        this.FindControl<TextBlock>("Intro")!.Text = Texts.T(_postEditor ? "bookmarks.edit" : "bookmarks.select");
        this.FindControl<TextBlock>("NoteLabel")!.Text = Texts.T("bookmarks.note");
        AutomationProperties.SetName(NoteInput, Texts.T("bookmarks.note"));
        AutomationProperties.SetName(List, Title);
        AutomationProperties.SetLiveSetting(StatusText, AutomationLiveSetting.Polite);
        List.ItemTemplate = new FuncDataTemplate<PostBookmark>((entry, _) => entry == null ? new TextBlock() : CreateRow(entry));
        List.SelectionChanged += async (_, _) =>
        {
            if (_switching || List.SelectedItem is not PostBookmark selected || selected.Key == _key) return;
            if (_busy || !await CanDiscardAsync()) { SelectKey(); return; }
            SetEditor(selected.Key); await ResolveAsync();
        };
        NoteInput.TextChanged += (_, _) =>
        {
            if (!_switching && _key != null && !ValidNote) SetStatus("bookmarks.invalidNote");
            Refresh();
        };
        Bind("Save", "post-bookmarks.save", SaveAsync, () => CanSave);
        Bind("Remove", "post-bookmarks.remove", RemoveAsync, () => CanRemove);
        Bind("OpenTarget", "post-bookmarks.open-target", OpenAsync, () => CanOpen);
        B("Reload").Content = Texts.T("bookmarks.retry");
        B("Reload").Click += async (_, _) => await ReloadAsync(preserveDraft: true);
        B("Cancel").Content = Texts.T("action.close");
        B("Cancel").Click += async (_, _) => await RequestCloseAsync();
        Closing += (_, e) =>
        {
            if (_forceClose) return;
            e.Cancel = true;
            if (!_busy) _ = RequestCloseAsync();
        };
        _router = new CommandRouter(this);
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            Texts.Changed -= OnLanguageChanged;
            AvailabilityChanged = null; RowLanguageChanged = null;
            foreach (IDisposable registration in _registrations) registration.Dispose();
            _router.Dispose(); _lifetime.Dispose();
        };
        void Register(string id, Func<bool> allowed, Func<Task> execute) => _registrations.Add(_router.Register(
            new CommandRegistration(id, this, () => IsVisible && IsActive && !_confirming,
                allowed, async () => await execute(), Priority: 300)));
        Register("post-bookmarks.save", () => CanSave, SaveAsync);
        Register("post-bookmarks.remove", () => CanRemove, RemoveAsync);
        Register("post-bookmarks.open-target", () => CanOpen, OpenAsync);
        KeyDown += async (_, e) =>
        {
            _router.Route(e);
            // Bubble handler lets native text/IME handling consume Escape first.
            if (!e.Handled && e.Key == Key.Escape && !_busy && !_confirming)
            { e.Handled = true; await RequestCloseAsync(); }
        };
        Opened += (_, _) => { if (_key != null) NoteInput.Focus(); else List.Focus(); };
        Refresh();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnLanguageChanged(sender, e)); return; }
        if (_lifetime.IsCancellationRequested) return;
        Title = Texts.T("bookmarks.title");
        this.FindControl<TextBlock>("Intro")!.Text = Texts.T(_postEditor ? "bookmarks.edit" : "bookmarks.select");
        this.FindControl<TextBlock>("NoteLabel")!.Text = Texts.T("bookmarks.note");
        AutomationProperties.SetName(NoteInput, Texts.T("bookmarks.note"));
        AutomationProperties.SetName(List, Title);
        CommandBindings.Apply(B("Save"), "post-bookmarks.save");
        CommandBindings.Apply(B("Remove"), "post-bookmarks.remove");
        CommandBindings.Apply(B("OpenTarget"), "post-bookmarks.open-target");
        B("Reload").Content = Texts.T("bookmarks.retry"); B("Cancel").Content = Texts.T("action.close");
        RenderStatus();
        RowLanguageChanged?.Invoke();
        Refresh();
    }

    // Resolve only materialized list rows. Detached/recycled rows cancel their local reads.
    private Control CreateRow(PostBookmark entry)
    {
        TextBlock availability = new() { Text = Texts.T("bookmarks.loading"), TextWrapping = TextWrapping.Wrap };
        StackPanel row = new() { Spacing = 3, Margin = new Thickness(4), Children =
        {
            new TextBlock { Text = entry.Key.ToString(), TextWrapping = TextWrapping.Wrap }, availability,
            new TextBlock { Text = Preview(entry.Note), TextWrapping = TextWrapping.Wrap, IsVisible = entry.Note.Length > 0 }
        } };
        string rowStatusKey = "bookmarks.loading";
        object?[] rowStatusArguments = [];
        void RenderRowStatus() => availability.Text = Texts.T(rowStatusKey, rowStatusArguments);
        void SetRowStatus(string key, params object?[] arguments)
        { rowStatusKey = key; rowStatusArguments = arguments; RenderRowStatus(); }
        CancellationTokenSource? current = null;
        int generation = 0;
        async void ResolveRow()
        {
            if (_lifetime.IsCancellationRequested) return;
            current?.Cancel();
            var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            current = request;
            int ownGeneration = ++generation;
            bool entered = false;
            try
            {
                await _rowResolves.WaitAsync(request.Token); entered = true;
                await using ArchiveReader reader = await _owner.OpenArchiveReaderAsync(request.Token);
                PostBookmarkTarget? target = await PostBookmarkTarget.ResolveAsync(reader, entry.Key, request.Token);
                if (!request.IsCancellationRequested && ownGeneration == generation)
                {
                    if (target == null) SetRowStatus("bookmarks.missing");
                    else SetRowStatus("bookmarks.available", target.View.Summary.Title);
                }
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (!request.IsCancellationRequested && ownGeneration == generation)
                {
                    if (error is FileNotFoundException) SetRowStatus("bookmarks.missing");
                    else SetRowStatus("bookmarks.failed", error.Message);
                }
            }
            finally
            {
                if (entered) _rowResolves.Release();
                if (ReferenceEquals(current, request)) current = null;
                request.Dispose();
            }
        }
        row.AttachedToVisualTree += (_, _) => { AvailabilityChanged += ResolveRow; RowLanguageChanged += RenderRowStatus; RenderRowStatus(); ResolveRow(); };
        row.DetachedFromVisualTree += (_, _) => { AvailabilityChanged -= ResolveRow; RowLanguageChanged -= RenderRowStatus; ++generation; current?.Cancel(); };
        return row;
    }

    private static string Preview(string text)
    {
        // Never split a surrogate pair in the bounded plain-text preview.
        string first = string.Concat(text.EnumerateRunes().Take(100).Select(rune => rune.ToString()));
        return first.Replace('\n', ' ') + (first.Length < text.Length ? "…" : "");
    }
    private void Bind(string name, string id, Func<Task> execute, Func<bool> available)
    {
        Button button = B(name);
        CommandBindings.Apply(button, id);
        button.Command = new DelegateCommand(async () => await execute(), available);
    }
    private bool CanSave => !_busy && !_conflicted && !_orphanDraft && _snapshot != null && _key != null && ValidNote;
    private bool CanRemove => !_busy && !_conflicted && _snapshot != null && _key != null && _exists;
    private bool CanOpen => !_postEditor && !_busy && _key != null && _targetAvailable;
    private bool ValidNote
    {
        get { try { PostBookmarkStore.NormalizeNote(NoteInput.Text ?? ""); return true; } catch (ArgumentException) { return false; } }
    }
    private void Refresh()
    {
        CommandBindings.Refresh(B("Save")); CommandBindings.Refresh(B("Remove")); CommandBindings.Refresh(B("OpenTarget"));
        B("Reload").IsEnabled = !_busy; B("Cancel").IsEnabled = !_busy;
        NoteInput.IsEnabled = !_busy && _key != null; List.IsEnabled = !_busy;
    }
    private void SetStatus(string? key, params object?[] arguments)
    { _actionError = false; _statusKey = key; _statusArguments = arguments; RenderStatus(); }
    private void RenderStatus() => StatusText.Text = _statusKey == null ? "" : Texts.T(_statusKey, _statusArguments);
    private string ErrorDetails(Exception error) => System.IO.Path.Combine(_owner.ProfileDirectory, "post-bookmarks.json") + "\n" + error.Message;
    private void ShowError(Exception error)
    { SetStatus("bookmarks.failed", ErrorDetails(error)); _actionError = true; }
    private void SetAvailabilityStatus(string key, params object?[] arguments)
    {
        // Availability can finish after an edit, conflict or failed write. Its
        // result must not hide the action needed to recover the user's draft.
        if (_conflicted) SetStatus("bookmarks.conflict");
        else if (_orphanDraft) SetStatus("bookmarks.removedDraft");
        else if (!ValidNote) SetStatus("bookmarks.invalidNote");
        else if (!_actionError) SetStatus(key, arguments);
    }

    internal async Task InitializeAsync(PostBookmarkKey? edit = null)
    {
        _key = edit; _postEditor = edit != null;
        List.IsVisible = !_postEditor;
        B("OpenTarget").IsVisible = !_postEditor;
        if (_postEditor && Content is Grid layout) layout.RowDefinitions[1].Height = new GridLength(0);
        this.FindControl<TextBlock>("Intro")!.Text = Texts.T(_postEditor ? "bookmarks.edit" : "bookmarks.select");
        await ReloadAsync(false);
        if (!_lifetime.IsCancellationRequested && _postEditor) NoteInput.Focus();
    }
    private async Task ReloadAsync(bool preserveDraft)
    {
        if (_busy) return;
        _busy = true; Refresh();
        string raw = NoteInput.Text ?? "";
        bool retain = preserveDraft && (Dirty || _conflicted || _orphanDraft);
        bool previouslyExisted = _exists || _orphanDraft;
        int previousIndex = _snapshot?.Bookmarks.ToList().FindIndex(item => item.Key == _key) ?? 0;
        try
        {
            PostBookmarkSnapshot result = await _owner.ReadPostBookmarksAsync(_lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            _snapshot = result; _conflicted = false;
            _switching = true; List.ItemsSource = result.Bookmarks; _switching = false;
            _key ??= result.Bookmarks.FirstOrDefault()?.Key;
            bool removed = previouslyExisted && result.Bookmarks.All(item => item.Key != _key);
            if (!_postEditor && !retain && removed)
                _key = result.Bookmarks.ElementAtOrDefault(Math.Clamp(previousIndex, 0, Math.Max(0, result.Bookmarks.Count - 1)))?.Key;
            SetEditor(_key);
            if (_postEditor && removed) _orphanDraft = true;
            if (retain)
            {
                NoteInput.Text = raw;
                _orphanDraft = previouslyExisted && !_exists;
                SetStatus(_orphanDraft ? "bookmarks.removedDraft" : "bookmarks.conflict");
            }
            else SetStatus(removed ? "bookmarks.removedDraft"
                : result.Bookmarks.Count == 0 && _key == null ? "bookmarks.empty" : null);
            // A removed selection may have been replaced by an existing neighbour above.
            // Resolve that new exact key; only retained/orphan drafts stay unavailable.
            if (!retain && _key != null && !_orphanDraft) await ResolveAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _snapshot = null;
            if (!_lifetime.IsCancellationRequested) ShowError(error);
        }
        finally { _busy = false; if (!_lifetime.IsCancellationRequested) Refresh(); }
    }
    private void SelectKey()
    {
        _switching = true;
        List.SelectedItem = _snapshot?.Bookmarks.FirstOrDefault(entry => entry.Key == _key);
        _switching = false;
    }
    private void SetEditor(PostBookmarkKey? key)
    {
        ++_resolveGeneration; _targetAvailable = false; _orphanDraft = false;
        if (key != _key) SetStatus(null); // A failure belongs to its previous exact editor target.
        _key = key;
        PostBookmark? entry = _snapshot?.Bookmarks.FirstOrDefault(item => item.Key == key);
        _exists = entry != null; _original = entry?.Note ?? ""; NoteInput.Text = _original;
        this.FindControl<TextBlock>("Target")!.Text = key?.ToString() ?? "";
        SelectKey(); Refresh();
    }
    internal void InvalidateAvailability()
    {
        ++_resolveGeneration; _targetAvailable = false; Refresh();
        AvailabilityChanged?.Invoke();
        if (IsVisible) _ = ResolveAsync();
    }
    private async Task ResolveAsync()
    {
        PostBookmarkKey? key = _key;
        if (key == null) return;
        int generation = ++_resolveGeneration;
        bool IsCurrent() => !_lifetime.IsCancellationRequested && generation == _resolveGeneration && key == _key;
        SetAvailabilityStatus("bookmarks.loading");
        try
        {
            await using ArchiveReader reader = await _owner.OpenArchiveReaderAsync(_lifetime.Token);
            PostBookmarkTarget? target = await PostBookmarkTarget.ResolveAsync(reader, key, _lifetime.Token);
            if (!IsCurrent()) return;
            _targetAvailable = target != null;
            if (_targetAvailable) SetAvailabilityStatus("bookmarks.available", target!.View.Summary.Title);
            else SetAvailabilityStatus("bookmarks.missing");
        }
        catch (FileNotFoundException) { if (IsCurrent()) SetAvailabilityStatus("bookmarks.missing"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (IsCurrent()) SetAvailabilityStatus("bookmarks.failed", ErrorDetails(error)); }
        finally { if (IsCurrent()) Refresh(); }
    }
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        _busy = true; Refresh();
        try
        {
            PostBookmarkKey key = _key!;
            PostBookmarkSnapshot result = await _owner.SavePostBookmarkAsync(key, NoteInput.Text ?? "", _snapshot!.Revision, _exists, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            _snapshot = result; _switching = true; List.ItemsSource = result.Bookmarks; _switching = false;
            SetEditor(key); SetStatus("bookmarks.saved");
            // SetEditor invalidates availability. Re-read the exact local target after commit,
            // including the case where an archive deletion made the old availability obsolete.
            await ResolveAsync();
            if (_lifetime.IsCancellationRequested) return;
            List.ScrollIntoView(List.SelectedItem!); List.Focus();
        }
        catch (PostBookmarkConflictException) { _conflicted = true; SetStatus("bookmarks.conflict"); }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!_lifetime.IsCancellationRequested) ShowError(error); }
        finally { _busy = false; if (!_lifetime.IsCancellationRequested) Refresh(); }
    }
    private async Task RemoveAsync()
    {
        if (!CanRemove || !await ConfirmAsync(Texts.T("bookmarks.deleteExplain"), Texts.T("bookmarks.remove"))) return;
        _busy = true; Refresh();
        try
        {
            int index = _snapshot!.Bookmarks.ToList().FindIndex(entry => entry.Key == _key);
            PostBookmarkSnapshot result = await _owner.RemovePostBookmarkAsync(_key!, _snapshot.Revision, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            _snapshot = result; _switching = true; List.ItemsSource = result.Bookmarks; _switching = false;
            SetEditor(result.Bookmarks.ElementAtOrDefault(Math.Min(index, result.Bookmarks.Count - 1))?.Key);
            SetStatus(result.Bookmarks.Count == 0 ? "bookmarks.empty" : null);
            if (_key != null) await ResolveAsync();
            if (_lifetime.IsCancellationRequested) return;
            if (List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem); List.Focus();
        }
        catch (PostBookmarkConflictException) { _conflicted = true; SetStatus("bookmarks.conflict"); }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!_lifetime.IsCancellationRequested) ShowError(error); }
        finally { _busy = false; if (!_lifetime.IsCancellationRequested) Refresh(); }
    }
    private async Task OpenAsync()
    {
        if (!CanOpen || !await CanDiscardAsync()) return;
        if (Dirty)
        {
            bool available = _targetAvailable;
            SetEditor(_key); _targetAvailable = available;
        }
        _busy = true; Refresh();
        try
        {
            _overviewOffset = List.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset;
            Hide();
            if (!await _open(_key!, _lifetime.Token)) { Show(); SetStatus("bookmarks.missing"); }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_lifetime.IsCancellationRequested) { Show(); ShowError(error); } }
        finally { _busy = false; if (!_lifetime.IsCancellationRequested) Refresh(); }
    }
    internal async void ReturnFromReader()
    {
        if (_lifetime.IsCancellationRequested) return;
        Show(); Activate();
        await ReloadAsync(preserveDraft: true);
        if (_lifetime.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_lifetime.IsCancellationRequested) return;
        if (_overviewOffset is { } offset && List.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
            scroll.Offset = offset;
        List.Focus();
    }
    internal async Task<bool> PrepareToCloseAsync() => !_busy && !_confirming && await CanDiscardAsync();

    internal void CloseForProfileEnd()
    {
        if (_forceClose) return;
        _lifetime.Cancel();
        _forceClose = true;
        Close();
    }
    private async Task<bool> CanDiscardAsync() => !Dirty || await ConfirmAsync(Texts.T("bookmarks.discard"), Texts.T("bookmarks.discardConfirm"));
    private async Task RequestCloseAsync()
    {
        if (_busy || _confirming || !await CanDiscardAsync()) return;
        _forceClose = true; Close();
    }
    private async Task<bool> ConfirmAsync(string explanation, string action)
    {
        if (_confirming) return false;
        _confirming = true;
        try
        {
            Window confirm = new() { Title = Title, Width = 460, SizeToContent = SizeToContent.Height,
                MinWidth = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            Button cancel = new() { Content = Texts.T("bookmarks.keepEditing") };
            Button accept = new() { Content = action };
            StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { cancel, accept } };
            confirm.Content = new StackPanel { Margin = new Thickness(16), Spacing = 12,
                Children = { new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap }, buttons } };
            cancel.Click += (_, _) => confirm.Close(false); accept.Click += (_, _) => confirm.Close(true);
            confirm.Opened += (_, _) => cancel.Focus();
            confirm.KeyDown += (_, e) => { if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; confirm.Close(false); } };
            return await confirm.ShowDialog<bool>(this);
        }
        finally { _confirming = false; }
    }
}
