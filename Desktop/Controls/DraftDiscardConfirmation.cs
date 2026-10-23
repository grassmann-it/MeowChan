using System;
using System.Linq;
using System.Windows.Input;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan.Desktop.Controls;

/// <summary>Explicit destructive-action surface, bound to a captured draft identity by the caller.</summary>
internal sealed class DraftDiscardConfirmation
{
    private readonly Button _trigger;
    private readonly DraftPopConfirm _surface;
    private Func<Task>? _confirm;
    private Func<bool>? _context;
    private Func<Control?>? _completedFocus;
    private long _generation;
    internal bool IsOpen => _surface.IsDropdownOpen;
    internal DraftDiscardConfirmation(Button trigger)
    {
        _trigger = trigger;
        _surface = new DraftPopConfirm { TriggerMode = (PopConfirmTriggerMode)0, HandleAsyncCommand = false,
            Placement = PlacementMode.Top, MaxWidth = 440 };
        if (trigger.Parent is Panel parent)
        {
            int index = parent.Children.IndexOf(trigger); parent.Children.RemoveAt(index);
            _surface.Content = trigger; parent.Children.Insert(index, _surface);
        }
        _surface.ConfirmCommand = new CallbackCommand(ConfirmAsync);
        _surface.CancelCommand = new CallbackCommand(() => { Close(true); return Task.CompletedTask; });
        _surface.PropertyChanged += (_, e) =>
        {
            if (e.Property != PopConfirm.IsDropdownOpenProperty) return;
            if (!IsOpen)
            {
                if (_confirm != null) { _confirm = null; ++_generation; ReturnFocus(_trigger, _context, _generation); }
                return;
            }
            Dispatcher.UIThread.Post(PrepareButtons, DispatcherPriority.Loaded);
        };
        _surface.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!IsOpen) return;
            if (e.Key == Key.Escape) { Close(true); e.Handled = true; }
            // Enter belongs to the focused native button. Do not bubble to page commands.
            else if (e.Key == Key.Enter) e.Handled = true;
        }, RoutingStrategies.Bubble);
    }
    internal void Open(string target, Action confirm, string? actionLabel = null, Func<bool>? context = null, Func<Control?>? completedFocus = null)
        => OpenAsync(target, () => { confirm(); return Task.CompletedTask; }, actionLabel, context, completedFocus);
    internal void OpenAsync(string target, Func<Task> confirm, string? actionLabel = null, Func<bool>? context = null, Func<Control?>? completedFocus = null)
    {
        if (IsOpen) return;
        ++_generation; _confirm = confirm; _context = context; _completedFocus = completedFocus;
        _surface.PopupHeader = Texts.T("draft.discardTitle", target);
        _surface.PopupContent = new TextBlock { Text = Texts.T("draft.discardBody"), TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 380 };
        _actionLabel = actionLabel ?? Texts.T("rules.discard");
        _surface.IsDropdownOpen = true;
    }
    private string _actionLabel = string.Empty;
    private void PrepareButtons()
    {
        if (!IsOpen) return;
        Button? cancel = _surface.CancelButton;
        Button? confirm = _surface.ConfirmButton;
        if (cancel != null) { cancel.Content = Texts.T("draft.keepEditing"); AutomationProperties.SetName(cancel, Texts.T("draft.keepEditing")); cancel.Focus(); }
        if (confirm != null) { confirm.Content = _actionLabel; AutomationProperties.SetName(confirm, _actionLabel); }
    }
    private async Task ConfirmAsync()
    {
        Func<Task>? action = _confirm; if (action == null) return;
        Func<bool>? context = _context; Func<Control?>? completedFocus = _completedFocus; long generation = _generation;
        _confirm = null; _surface.IsDropdownOpen = false;
        // The callback owns error display. Wait for its entire operation and final UI publication.
        await action();
        if (generation == _generation && context?.Invoke() != false)
            ReturnFocus(completedFocus?.Invoke() ?? _trigger, context, generation);
    }
    internal void Close(bool focus = false)
    {
        if (_confirm != null || IsOpen) ++_generation;
        _confirm = null; _surface.IsDropdownOpen = false;
        if (focus) ReturnFocus(_trigger, _context, _generation);
    }
    private void ReturnFocus(Control? target, Func<bool>? context, long generation)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _generation || context?.Invoke() == false || target == null || !target.IsAttachedToVisualTree()
                || !target.IsEffectivelyVisible || !target.IsEnabled) return;
            target.BringIntoView(); target.Focus();
        }, DispatcherPriority.Loaded);
    }
    private sealed class DraftPopConfirm : PopConfirm
    {
        protected override Type StyleKeyOverride => typeof(PopConfirm);
        internal Button? CancelButton, ConfirmButton;
        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            CancelButton = e.NameScope.Find<Button>(PART_CancelButton); ConfirmButton = e.NameScope.Find<Button>(PART_ConfirmButton);
            if (e.NameScope.Find<Popup>(PART_Popup) is { } popup)
                popup.Closed += (_, _) => { if (IsDropdownOpen) IsDropdownOpen = false; };
        }
    }
    private sealed class CallbackCommand(Func<Task> execute) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public async void Execute(object? parameter) => await execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
