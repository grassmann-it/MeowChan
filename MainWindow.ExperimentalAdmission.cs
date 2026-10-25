using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private ExperimentalAdmissionFocusReturn? _experimentalAdmissionFocusReturn;

    // A canceled owned modal may leave Avalonia's owner FocusManager empty.
    // This scope belongs only to a topbar submission; Sources keeps its own
    // independent busy/input/lifetime guards.
    private sealed class ExperimentalAdmissionFocusReturn : IDisposable
    {
        private readonly MainWindow _owner;
        private readonly TextBox _editor;
        private readonly string _text;
        private readonly WorkspacePage _page;
        private readonly List<Action> _stopModalObservers = new();
        private bool _eligible, _sawModal;
        private ExperimentalActivationDialog? _modal;

        internal ExperimentalAdmissionFocusReturn(MainWindow owner, TextBox editor, Button add)
        {
            _owner = owner; _editor = editor; _text = editor.Text ?? string.Empty; _page = owner._selectedPage;
            _eligible = editor.IsKeyboardFocusWithin || add.IsKeyboardFocusWithin;
            owner.AddHandler(GotFocusEvent, OnNewFocus, RoutingStrategies.Bubble, handledEventsToo: true);
            owner.AddHandler(PointerPressedEvent, OnNewPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
            owner.AddHandler(KeyDownEvent, OnNewKey, RoutingStrategies.Tunnel, handledEventsToo: true);
            owner.Deactivated += OnOwnerDeactivated;
            owner.Closing += OnOwnerClosing;
            owner.PropertyChanged += OnOwnerPropertyChanged;
            editor.PropertyChanged += OnEditorPropertyChanged;
            editor.DetachedFromVisualTree += OnEditorDetached;
        }

        internal void Observe(ExperimentalActivationDialog modal)
        {
            _sawModal = true; _modal = modal;
            WindowClosingEventArgs? closing = null;
            void OnClosing(object? sender, WindowClosingEventArgs args) => closing = args;
            void OnDeactivated(object? sender, EventArgs args)
            {
                // Read the original args object after all Closing handlers: a
                // canceled close must not hide a later external deactivation.
                if (closing is not { Cancel: false }) _eligible = false;
            }
            void OnClosed(object? sender, EventArgs args) { if (ReferenceEquals(_modal, modal)) _modal = null; }
            modal.Closing += OnClosing;
            modal.Deactivated += OnDeactivated;
            modal.Closed += OnClosed;
            _stopModalObservers.Add(() =>
            {
                modal.Closing -= OnClosing;
                modal.Deactivated -= OnDeactivated;
                modal.Closed -= OnClosed;
            });
        }

        private void OnNewFocus(object? sender, FocusChangedEventArgs args)
        {
            if (!ReferenceEquals(_owner.FocusManager?.GetFocusedElement(), _editor)) _eligible = false;
        }
        private void OnNewPointer(object? sender, PointerPressedEventArgs args) => _eligible = false;
        private void OnNewKey(object? sender, KeyEventArgs args) => _eligible = false;
        private void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            // TextChanged is queued; an earlier initial edit must not invalidate
            // a scope created later during the same routed click.
            if (args.Property == TextBox.TextProperty) _eligible = false;
        }
        private void OnEditorDetached(object? sender, VisualTreeAttachmentEventArgs args) => _eligible = false;
        private void OnOwnerClosing(object? sender, WindowClosingEventArgs args) => _eligible = false;
        private void OnOwnerDeactivated(object? sender, EventArgs args)
        {
            // Opening our modal is expected. Switching away from that modal is
            // observed on the modal itself, including an external Alt-Tab.
            if (_modal == null) _eligible = false;
        }
        private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == IsVisibleProperty && !_owner.IsVisible
                || args.Property == WindowStateProperty && _owner.WindowState == WindowState.Minimized)
                _eligible = false;
        }

        internal void Restore()
        {
            if (!_eligible || !_sawModal || !ReferenceEquals(_owner._experimentalAdmissionFocusReturn, this)
                || _owner._isClosing || _owner._importing || _owner._applyingPreferences || !_owner._restoreCompleted
                || _owner._session.State != DesktopSessionState.Ready || !_owner.IsVisible || !_owner.IsActive || !_owner.IsEnabled
                || _owner._selectedPage != _page || _owner.HasVisibleShellDialog()
                || !_editor.IsEffectivelyVisible || !_editor.IsEffectivelyEnabled || _editor.Text != _text
                || !ReferenceEquals(TopLevel.GetTopLevel(_editor), _owner)) return;
            // The topbar editor stayed enabled during admission. No delayed
            // callback is needed after restoring the owner's interaction state.
            if (_owner.FocusManager?.GetFocusedElement() != null) return;
            _editor.BringIntoView();
            _editor.Focus();
        }

        public void Dispose()
        {
            _eligible = false;
            _owner.RemoveHandler(GotFocusEvent, OnNewFocus);
            _owner.RemoveHandler(PointerPressedEvent, OnNewPointer);
            _owner.RemoveHandler(KeyDownEvent, OnNewKey);
            _owner.Deactivated -= OnOwnerDeactivated;
            _owner.Closing -= OnOwnerClosing;
            _owner.PropertyChanged -= OnOwnerPropertyChanged;
            _editor.PropertyChanged -= OnEditorPropertyChanged;
            _editor.DetachedFromVisualTree -= OnEditorDetached;
            foreach (Action stop in _stopModalObservers) stop();
        }
    }

    internal static bool IsSameExperimentalAdmissionTarget(ExperimentalSourceActivationTarget frozen,
        ExperimentalSourceActivationTarget? current) => current != null
        && current.SessionId == frozen.SessionId && current.ProfileId == frozen.ProfileId
        && current.PluginOwnerId == frozen.PluginOwnerId && current.SourceId == frozen.SourceId
        && current.Url == frozen.Url && current.CandidateId == frozen.CandidateId
        && current.ExecutionRevision == frozen.ExecutionRevision
        && current.CompilationFingerprint == frozen.CompilationFingerprint
        && current.AssemblySha256 == frozen.AssemblySha256;

    private async Task<ExperimentalSourceActivationProjection> ReadExperimentalProjectionAsync(string url)
    {
        try { return await _session.ProjectExperimentalSourceActivationAsync(url); }
        catch (OperationCanceledException) when (_isClosing) { throw; }
        catch (Exception error)
        {
            // Keep a local source/configuration failure out of the clipboard-read error path.
            return new(ExperimentalSourceActivationProjectionStatus.Unavailable, Error: error.Message);
        }
    }

    private async Task<IReadOnlyDictionary<int, string>> FilterExperimentalAdmissionsAsync(IReadOnlyList<string> entries,
        Action<Window>? observeExperimentalModal = null)
    {
        var skipped = new Dictionary<int, string>();
        var groups = new Dictionary<(string Owner, string Source), (ExperimentalSourceActivationTarget Target, List<int> Entries)>();
        // Freeze the original URL grouping before any Enable can change projection results.
        // Invalid/unsupported URLs and existing watches keep normal per-entry feedback.
        for (int index = 0; index < entries.Count; index++)
        {
            if (_isClosing) throw new OperationCanceledException();
            if (_session.ClassifyAdmission(entries[index]) is { AlreadyWatching: true }) continue;
            ExperimentalSourceActivationProjection projection = await ReadExperimentalProjectionAsync(entries[index]);
            if (projection.Status == ExperimentalSourceActivationProjectionStatus.NotRequired) continue;
            if (projection.Status != ExperimentalSourceActivationProjectionStatus.ConfirmationRequired || projection.Target == null)
            {
                skipped[index] = Texts.T("activation.unavailable", projection.Error ?? "");
                continue;
            }
            ExperimentalSourceActivationTarget target = projection.Target;
            var key = (target.PluginOwnerId, target.SourceId);
            if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = (target, new List<int>()));
            group.Entries.Add(index);
        }
        // Dictionary insertion order is made explicit: first submitted URL determines prompt order.
        foreach (var group in groups.Values.OrderBy(group => group.Entries[0]))
        {
            if (_isClosing) throw new OperationCanceledException();
            // Earlier confirmed sources may change the global profile revision. Capture a fresh
            // target before displaying the next warning, never after its confirmation.
            // A newly active target may proceed strictly only if it is the same captured
            // authority and build. NotRequired alone must not silently retarget this group.
            ExperimentalSourceActivationProjection current = await ReadExperimentalProjectionAsync(entries[group.Entries[0]]);
            bool sameTarget = IsSameExperimentalAdmissionTarget(group.Target, current.Target);
            if (current.Status == ExperimentalSourceActivationProjectionStatus.NotRequired && sameTarget) continue;
            string? error = null;
            if (current.Status != ExperimentalSourceActivationProjectionStatus.ConfirmationRequired || current.Target == null || !sameTarget)
                error = Texts.T("activation.unavailable", current.Error ?? Texts.T("activation.targetChanged"));
            else
            {
                var dialog = new ExperimentalActivationDialog(current.Target, _session.ActivateAndAllowExperimentalSourceAsync);
                _experimentalAdmissionFocusReturn?.Observe(dialog);
                observeExperimentalModal?.Invoke(dialog);
                ExperimentalActivationDecision? decision = await dialog.ShowDialog<ExperimentalActivationDecision?>(this);
                RefreshSourcesPage();
                if (_isClosing) throw new OperationCanceledException();
                if (decision?.ContinueAdmission == true) continue;
                error = decision?.Error ?? dialog.AdmissionError ?? Texts.T("activation.skipped", group.Target.SourceName);
            }
            foreach (int index in group.Entries) skipped[index] = error;
        }
        return skipped;
    }
}
