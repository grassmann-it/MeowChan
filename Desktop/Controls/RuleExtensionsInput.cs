using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Threading;
using MeowChan.Rules;

namespace MeowChan.Desktop.Controls;

/// <summary>The model owns both canonical tags and the exact unfinished/invalid text.</summary>
internal sealed class ExtensionTagDraft
{
    internal List<string> Tags { get; } = new();
    internal string Rest { get; set; } = string.Empty;
    internal ExtensionTagDraft Clone() { var copy = new ExtensionTagDraft { Rest = Rest }; copy.Tags.AddRange(Tags); return copy; }
    internal void Commit()
    {
        List<string> invalid = new();
        foreach (string token in Rest.Split(','))
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            try { string normalized = MediaTypeSelection.NormalizeExtension(token); if (!Tags.Contains(normalized, StringComparer.Ordinal)) Tags.Add(normalized); }
            catch (ArgumentException) { invalid.Add(token); }
        }
        Tags.Sort(StringComparer.Ordinal); Rest = string.Join(",", invalid);
    }
    internal MediaTypeSelection Build(MediaTypeSelectionMode mode) => new(mode, mode == MediaTypeSelectionMode.Selected
        ? Tags.Concat(Rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : Array.Empty<string>());
    internal string Fingerprint => string.Join(",", Tags) + "\0" + Rest;
}

/// <summary>Intercepts Ursa's destructive input commit; invalid tokens never enter its tag collection.</summary>
public sealed class RuleExtensionsInput : Ursa.Controls.TagInput
{
    protected override Type StyleKeyOverride => typeof(Ursa.Controls.TagInput);
    private TextBox? _input;
    private bool _sync;
    private bool _restoreOnAttach;
    private readonly ObservableCollection<string> _tags = new();
    internal ExtensionTagDraft State { get; private set; } = new();
    internal event Action? Edited;
    public RuleExtensionsInput()
    {
        Tags = _tags; AllowDuplicates = false; LostFocusBehavior = (Ursa.Controls.LostFocusBehavior)0;
        _tags.CollectionChanged += TagsChanged;
        AddHandler(KeyDownEvent, InputKeyDown, RoutingStrategies.Tunnel);
        Loaded += (_, _) => AttachInput();
        // The trailing TextBox can materialize after the outer Loaded event.
        // GotFocus is Bubble-only in Avalonia. Observe the actual owned editor
        // synchronously, even if its class handler has already handled focus.
        AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox input && input.GetVisualAncestors().Contains(this)) AttachInput(focusedInput: input);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        DetachInput();
        base.OnApplyTemplate(e);
        AttachInput();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachInput();
        base.OnDetachedFromVisualTree(e);
    }
    private void DetachInput()
    {
        if (_input == null) return;
        // Preserve pending raw text without rendering into an editor being removed.
        FlushPendingText();
        if (_input != null) _input.TextChanging -= InputChanged;
        _input = null;
        _restoreOnAttach = true;
    }
    private void AttachInput(bool render = true, TextBox? focusedInput = null)
    {
        TextBox? next = focusedInput ?? this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        if (ReferenceEquals(_input, next))
        {
            if (render && _input != null && _restoreOnAttach) Render();
            return;
        }
        if (_input != null) _input.TextChanging -= InputChanged;
        _input = next;
        if (_input == null) return;
        _input.TextChanging += InputChanged;
        AutomationProperties.SetName(_input, Localization.Texts.T("rules.extensions"));
        // A late attachment must not overwrite an edit made before Loaded. An
        // empty replacement editor, however, must display the model's retained rest.
        // A blank newly materialized editor has not received its owner's raw
        // rest. Keep that distinction until a normal render initializes it.
        _restoreOnAttach |= string.IsNullOrEmpty(_input.Text);
        if (render && !_restoreOnAttach && State.Rest != _input.Text)
        {
            State.Rest = _input.Text ?? string.Empty;
            Edited?.Invoke();
        }
        else if (render) Render();
    }
    internal void Bind(ExtensionTagDraft state) { State = state; Render(); }
    internal void FlushPendingText()
    {
        // Detached retained template children do not own the current draft.
        if (!this.IsAttachedToVisualTree()) return;
        // Resolve without rendering. A newly blank/replaced editor has no raw
        // edit to flush until normal Render/Loaded/focus initializes its owner.
        AttachInput(render: false);
        if (_sync || _input == null || _restoreOnAttach || State.Rest == (_input.Text ?? string.Empty)) return;
        State.Rest = _input.Text ?? string.Empty; Edited?.Invoke();
    }
    internal void FocusInput() { _input?.Focus(); this.BringIntoView(); }
    private void InputChanged(object? sender, TextChangingEventArgs args)
    {
        if (_sync || _input == null || !ReferenceEquals(sender, _input)) return;
        _restoreOnAttach = false; // A tracked edit, including explicit empty, now owns the raw value.
        State.Rest = _input.Text ?? string.Empty;
        if (State.Rest.Contains(',')) { State.Commit(); Render(); }
        Edited?.Invoke();
    }
    private void InputKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Delete || e.Key == Key.Back) && e.Source is Control source && source is not TextBox)
        {
            Ursa.Controls.ClosableTag? tag = source as Ursa.Controls.ClosableTag ?? source.GetVisualAncestors().OfType<Ursa.Controls.ClosableTag>().FirstOrDefault();
            if (tag?.Content is string value && _tags.Contains(value)) { e.Handled = true; _tags.Remove(value); }
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true; State.Commit(); Render(); Edited?.Invoke();
    }
    private void TagsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_sync) return;
        State.Tags.Clear(); State.Tags.AddRange(_tags); Edited?.Invoke();
        _input?.Focus();
    }
    private void Render()
    {
        _sync = true;
        try
        {
            _tags.Clear(); foreach (string tag in State.Tags) _tags.Add(tag);
            if (_input != null) { _input.Text = State.Rest; _restoreOnAttach = false; }
        }
        finally { _sync = false; }
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var tag in this.GetVisualDescendants().OfType<Ursa.Controls.ClosableTag>())
            {
                tag.Focusable = true;
                string label = Localization.Texts.T("rules.removeExtension", tag.Content);
                AutomationProperties.SetName(tag, label);
                foreach (var button in tag.GetVisualDescendants().OfType<Button>()) AutomationProperties.SetName(button, label);
            }
        }, DispatcherPriority.Loaded);
    }
}
