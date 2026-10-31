using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MeowChan.Desktop;

/// <summary>
/// Transfers gallery-owned chrome, never native drawables. Avalonia 12 retains queued
/// descendants in their old presentation's layout manager after detach. Drain that
/// presentation's real layout with the subtree detached before attaching elsewhere.
/// </summary>
internal static class GalleryControlTransfer
{
    private static readonly ConditionalWeakTable<Control, Transfer> Transfers = new();
    // Source layout callbacks may request placement of OTHER gallery controls. Serialize
    // their physical moves too, not just recursive requests for the current control.
    private static int _activeTransactions;

    internal static void Track(Control control, Func<bool>? canAttach = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        Transfer transfer = Transfers.GetValue(control, static value => new Transfer(value));
        if (canAttach != null) transfer.OwnerAvailable = canAttach;
        transfer.RememberCurrentRoot();
    }

    internal static void Move(Control control, Panel target, Func<bool>? canAttach = null, Action? moved = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        Track(control);
        Transfers.GetValue(control, static value => new Transfer(value)).Request(target, canAttach, moved);
    }

    // Terminal only. Temporary hides/detachments must remain reusable.
    internal static void Cancel(Control control)
    {
        Dispatcher.UIThread.VerifyAccess();
        Track(control);
        Transfers.GetValue(control, static value => new Transfer(value)).Cancel();
    }

    private static Layoutable? CurrentRoot(Control control) => !control.IsAttachedToVisualTree() ? null
        : (control.GetVisualAncestors().LastOrDefault() ?? control) as Layoutable;

    private static bool HasPendingAncestor(Control control)
    {
        for (Visual? visual = control; visual != null; visual = visual.GetVisualParent())
            if (visual is Control ancestor && Transfers.TryGetValue(ancestor, out Transfer? transfer)
                && (transfer.IsPending || transfer._cancelled)) return true;
        return false;
    }

    private sealed class Transfer
    {
        private readonly Control _control;
        private readonly HashSet<Layoutable> _roots = new();
        private Panel? _target;
        private Func<bool>? _canAttach;
        private Action? _moved;
        private bool _queued, _running;
        internal bool _cancelled;
        private long _revision;
        internal bool IsPending => _queued || _running;
        internal Func<bool> OwnerAvailable = static () => true;

        internal Transfer(Control control)
        {
            _control = control;
            control.AttachedToVisualTree += (_, e) =>
            {
                Remember(e.RootVisual);
                // Retain presentation history even when VideoView.Content, rather than
                // the tracked control itself, was detached/reinstalled by its owner.
                if (!_cancelled && !IsPending && _roots.Count > 1 && control.Parent is Panel parent)
                    Request(parent, null, null);
            };
            control.DetachedFromVisualTree += (_, e) => Remember(e.RootVisual);
        }

        private void Remember(Visual root)
        {
            if (root is Layoutable layout) _roots.Add(layout);
        }

        internal void RememberCurrentRoot()
        {
            if (CurrentRoot(_control) is { } root) _roots.Add(root);
        }

        internal void Request(Panel target, Func<bool>? canAttach, Action? moved)
        {
            if (_cancelled) return;
            RememberCurrentRoot();
            bool unchanged = ReferenceEquals(_target, target);
            // A completed same-target placement is a no-op, but a queued A->B->A
            // must overwrite B even though the physical parent is still A.
            if (ReferenceEquals(_control.Parent, target) && _roots.Count <= 1
                && (!_queued && !_running || _running && unchanged)) return;
            _target = target;
            _canAttach = canAttach;
            // Layout callbacks without captured focus must not erase the original
            // same-target focus restoration; a different destination replaces it.
            if (!unchanged || moved != null) _moved = moved;
            _revision++;
            if (IsPending) return;
            Layoutable? current = CurrentRoot(_control);
            bool sameRoot = current != null && ReferenceEquals(current, CurrentRoot(target)) && _roots.Count == 1;
            if (_activeTransactions == 0 && !HasPendingAncestor(target) && (_roots.Count == 0 || sameRoot))
                Execute(drain: false);
            else Queue();
        }

        internal void Cancel()
        {
            if (_cancelled) return;
            RememberCurrentRoot();
            _cancelled = true;
            _target = null; _canAttach = null; _moved = null; _revision++;
            // Never restore terminal controls to an attached main window. Removing
            // them is safe during a drain and prevents a pending attach immediately.
            if (_control.Parent is Panel parent) parent.Children.Remove(_control);
        }

        private void Queue()
        {
            if (_queued || _cancelled) return;
            _queued = true;
            // Leave Arrange/SizeChanged before cross-presentation work. Scheduling
            // alone isn't the fix: Execute actively drains retained source roots.
            Dispatcher.UIThread.Post(Complete, DispatcherPriority.Normal);
        }

        private void Complete()
        {
            _queued = false;
            if (_cancelled) return;
            if (_activeTransactions != 0) { Queue(); return; }
            Execute(drain: true);
        }

        private void Execute(bool drain)
        {
            _running = true;
            _activeTransactions++;
            bool completed = false, needsDeferredDrain = false;
            long appliedRevision = _revision;
            try
            {
                RememberCurrentRoot();
                if (_control.Parent is Panel parent) parent.Children.Remove(_control);
                if (drain)
                {
                    Layoutable[] roots = _roots.ToArray();
                    foreach (Layoutable root in roots) root.UpdateLayout();
                    // Keep the debt if a real layout throws; never suppress a failure
                    // or claim a presentation has drained before it actually did.
                    _roots.ExceptWith(roots);
                }
                // Draining can retarget/cancel this transfer. Use the latest request,
                // then check lifetime again immediately before attachment.
                appliedRevision = _revision;
                Panel? target = _target;
                needsDeferredDrain = !drain && target != null && _roots.Count != 0
                    && (_roots.Count != 1 || CurrentRoot(target) is not { } targetRoot || !_roots.Contains(targetRoot));
                if (!needsDeferredDrain && !_cancelled && target != null && OwnerAvailable() && (_canAttach?.Invoke() ?? true))
                {
                    if (!_cancelled && appliedRevision == _revision)
                    {
                        target.Children.Add(_control);
                        if (!_cancelled && appliedRevision == _revision) _moved?.Invoke();
                    }
                }
                completed = true;
            }
            finally
            {
                _activeTransactions--;
                _running = false;
                if (completed)
                {
                    if (!_cancelled && (needsDeferredDrain || appliedRevision != _revision)) Queue();
                    else { _target = null; _canAttach = null; _moved = null; }
                }
            }
        }
    }
}
