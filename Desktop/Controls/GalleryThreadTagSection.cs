using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The "Thread tags" section of the gallery tag panel (Ctrl+T): the tags of the thread the shown medium belongs to, each a removable
/// chip, and a field that adds one (own tags suggested, Enter or comma adds, a new name creates the tag). It is the content of
/// <see cref="QuickTagSurface.ExtraContent"/>; the panel switches its <see cref="Model"/> when the gallery moves to another thread.
/// A thread tag marks the thread, not its media files; the heading says so.
/// </summary>
internal sealed class GalleryThreadTagSection : StackPanel, IDisposable
{
    private readonly string _name;
    private readonly TextBlock _heading = new() { Classes = { "workspace-meta" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { Classes = { "workspace-help" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly WrapPanel _chips = new() { ItemSpacing = 8, LineSpacing = 8 };
    private readonly IdentitySuggestionBox _input = new();
    private readonly TextBlock _error = new() { Classes = { "workspace-error" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
    private readonly List<(Button Chip, TagId Tag)> _built = new();
    private GalleryThreadTagModel? _model;
    private bool _disposed;

    internal GalleryThreadTagSection(string name = "GalleryThreadTags")
    {
        _name = name;
        Name = name;
        Spacing = 8;
        _chips.Name = name + "Chips"; _input.Name = name + "Input"; _error.Name = name + "Error"; _hint.Name = name + "Hint"; _heading.Name = name + "Heading";
        Children.Add(_heading);
        Children.Add(_chips);
        Children.Add(_hint);
        Children.Add(_input);
        Children.Add(_error);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        TagSuggestionInput.Attach(_input, () => _model is { Busy: false } model ? model.Snapshot : null,
            () => _model?.AssignedIds() ?? new HashSet<TagId>(), () => _ = AddTypedAsync());
        _input.AddHandler(InputElement.KeyDownEvent, OnInputKey, RoutingStrategies.Tunnel);
        Texts.Changed += OnLanguageChanged;
        Localize();
        Refresh();
    }

    internal IdentitySuggestionBox Input => _input;
    internal IReadOnlyList<Button> ChipButtons => _built.Select(item => item.Chip).ToArray();
    internal TextBlock Heading => _heading;
    internal TextBlock Error => _error;

    /// <summary>The thread shown now; null clears the section.</summary>
    internal GalleryThreadTagModel? Model
    {
        get => _model;
        set
        {
            if (ReferenceEquals(_model, value)) return;
            if (_model != null) _model.Updated -= OnModelUpdated;
            _model = value;
            if (_model != null) _model.Updated += OnModelUpdated;
            Refresh();
        }
    }

    internal void FocusInput() => _input.Focus();

    private void OnModelUpdated()
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() => { if (!_disposed) Refresh(); });
    }

    internal void Refresh()
    {
        if (_disposed) return;
        foreach ((Button chip, _) in _built) chip.Click -= OnChipClick;
        _built.Clear();
        _chips.Children.Clear();
        IReadOnlyList<PrivateTagDefinition> tags = _model?.Assigned ?? Array.Empty<PrivateTagDefinition>();
        foreach (PrivateTagDefinition tag in tags)
        {
            Button chip = new() { Name = _name + "Chip" + (_built.Count + 1), Content = WorkspaceChips.RemovableContent(tag.Name.Display) };
            WorkspaceChips.MarkTagPill(WorkspaceChips.MarkOwn(chip), WorkspaceChips.TagPillAssigned);
            string remove = Texts.T("tags.quick.thread.remove", tag.Name.Display);
            AutomationProperties.SetName(chip, remove);
            ToolTip.SetTip(chip, remove);
            chip.Click += OnChipClick;
            _built.Add((chip, tag.Id));
            _chips.Children.Add(chip);
        }
        _heading.Text = Texts.T("tags.quick.thread.heading", tags.Count);
        _hint.Text = tags.Count == 0 ? Texts.T("tags.quick.thread.empty") : Texts.T("tags.quick.thread.hint");
        _input.IsEnabled = _model?.Snapshot != null;
        _error.Text = _model?.MessageKey is { } key ? Texts.T(key) : _model != null && _model.Snapshot == null ? Texts.T("tags.unavailable") : string.Empty;
        _error.IsVisible = _error.Text.Length > 0;
    }

    private void Localize()
    {
        TagSuggestionInput.Localize(_input);
        string name = Texts.T("tags.quick.thread.field");
        AutomationProperties.SetName(_input, name);
        _input.PlaceholderText = name;
        AutomationProperties.SetName(_chips, Texts.T("tags.quick.thread.chips"));
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        Localize();
        Refresh();
    });

    private async void OnChipClick(object? sender, RoutedEventArgs e)
    {
        if (_model == null || _built.FirstOrDefault(item => ReferenceEquals(item.Chip, sender)) is not { Chip: not null } found) return;
        await Guard(() => _model.RemoveAsync(found.Tag), "tags.quick.thread.remove");
    }

    private async Task AddTypedAsync()
    {
        string name = _input.Text ?? string.Empty;
        if (_model == null || string.IsNullOrWhiteSpace(name)) return;
        if (_model.Busy) return; // keep the draft
        await Guard(() => _model.AddAsync(name), "tags.quick.thread.add");
        if (!_disposed && _model.MessageKey == null) _input.Text = string.Empty;
    }

    // Enter adds the draft, unless the field takes it (TagSuggestionInput.FieldTakesEnter).
    private async void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        // Added after the field's own handler, so it runs before it: a highlighted suggestion is the field's to accept.
        if (e.Key == Key.Enter && !TagSuggestionInput.FieldTakesEnter(_input, e))
        {
            e.Handled = true;
            _input.Dismiss();
            await AddTypedAsync();
        }
    }

    private static async Task Guard(Func<Task> action, string context)
    {
        try { await action(); }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, context)) { /* Logged and shown. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Texts.Changed -= OnLanguageChanged;
        if (_model != null) _model.Updated -= OnModelUpdated;
        _model = null;
        _input.Dispose();
        foreach ((Button chip, _) in _built) chip.Click -= OnChipClick;
        _input.RemoveHandler(InputElement.KeyDownEvent, OnInputKey);
    }
}
