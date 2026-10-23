using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Commands;

/// <summary>Resolve the target at invocation time, never when the draft/menu was created.</summary>
public sealed record CommandRegistration(string CommandId, Control Owner, Func<bool> IsActive,
    Func<bool> CanExecute, Action Execute, Func<string?>? DisabledReason = null, int Priority = 0,
    bool AllowInTextInput = false);

public sealed class CommandRouter : IDisposable
{
    private static readonly List<WeakReference<CommandRouter>> Routers = new();
    private readonly Control _root;
    private readonly List<CommandRegistration> _registrations = new();
    private bool _disposed;
    public CommandRouter(Control root)
    {
        _root = root;
        Routers.RemoveAll(w => !w.TryGetTarget(out _));
        Routers.Add(new(this));
    }
    internal static bool HasExplicitRegistration(string id, Control context)
    {
        var top = TopLevel.GetTopLevel(context);
        return Routers.Any(w => w.TryGetTarget(out var router) && !router._disposed &&
            router._registrations.Any(r => r.CommandId == id && TopLevel.GetTopLevel(r.Owner) == top));
    }
    public IDisposable Register(CommandRegistration registration)
    {
        CommandCatalog.Get(registration.CommandId);
        _registrations.Add(registration);
        return new RegistrationLease(this, registration);
    }
    public bool Route(KeyEventArgs e)
    {
        if (_disposed || e.Handled || ShortcutService.IsCapturing || e.Key is Key.Escape or Key.Tab) return false;
        if (!IsRoutingContextActive(_root, e.Source)) return false;
        var top = TopLevel.GetTopLevel(_root);
        var matches = _registrations.Where(r => r.Owner.IsEffectivelyVisible && TopLevel.GetTopLevel(r.Owner) == top
            && r.IsActive() && (!IsNativeInput(e.Source, e.Key) || r.AllowInTextInput)
            && ShortcutService.Current.Matches(e, r.CommandId)).OrderByDescending(r => r.Priority).ToArray();
        if (matches.Length == 0) return false;
        // Matching a disabled high-priority action deliberately consumes the event.
        e.Handled = true;
        var chosen = matches[0];
        if (matches.Any(r => r.Priority == chosen.Priority && r.CommandId != chosen.CommandId)) return true;
        if (chosen.CanExecute()) chosen.Execute();
        return true;
    }
    internal static bool IsRoutingContextActive(Control root, object? source)
    {
        if (!root.IsEffectivelyVisible || !root.IsEffectivelyEnabled) return false;
        var top = TopLevel.GetTopLevel(root);
        if (top == null) return false;
        if (top is Window window && (!window.IsActive || !window.IsEnabled)) return false;
        // Owned dialogs and native video windows have their own router. Ownership
        // alone never authorizes a background page to execute their input.
        return source is not Visual visual || ReferenceEquals(TopLevel.GetTopLevel(visual), top);
    }
    public static bool IsNativeInput(object? source, Key key)
    {
        if (source is not Visual visual) return false;
        foreach (var v in visual.GetSelfAndVisualAncestors())
        {
            if (v is TextBox or ComboBox or NumericUpDown or Slider or ScrollBar or MenuItem or Ursa.Controls.AutoCompleteBox) return true;
            if (v is SelectableTextBlock text && text.SelectionStart != text.SelectionEnd) return true;
            if ((key is Key.Enter or Key.Space) && (v is Button or Ursa.Controls.ClosableTag or Ursa.Controls.Rating or Expander)) return true;
            if ((key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown) && (v is SelectingItemsControl or DataGrid)) return true;
        }
        return false;
    }

    public void Dispose() { _disposed = true; _registrations.Clear(); }
    private sealed class RegistrationLease(CommandRouter router, CommandRegistration registration) : IDisposable
    { public void Dispose() => router._registrations.Remove(registration); }
}
