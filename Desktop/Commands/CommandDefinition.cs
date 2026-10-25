using System;
using System.Collections.Generic;

namespace MeowChan.Desktop.Commands;

/// <summary>Stable, presentation-independent identity for a desktop action.</summary>
public sealed record CommandDefinition(
    string Id,
    string LabelKey,
    string LabelEn,
    string LabelDe,
    string Icon,
    string? Shortcut,
    CommandScope Scope,
    IReadOnlyList<string> Contexts,
    IReadOnlyList<string>? ActiveContexts = null)
{
    public bool IsImplemented => Id is not ("post.copy-link" or "post.open-source" or "viewer.focus-mode"
        or "table.reset-layout" or "admission.monitor-all");
    public IReadOnlyList<ShortcutGesture> DefaultGestures => EffectiveShortcutBindings.Defaults(this);
    public IReadOnlyList<string> ExecutionContexts => EffectiveShortcutBindings.ExecutionContexts(Id);
    public string Label => CommandCatalog.Label(Id);
    public string? ShortcutDisplay => CommandCatalog.ShortcutDisplay(Id);
    /// <summary>Dynamic availability is resolved by each binding against its captured target.</summary>
    public CommandAvailability Availability => !IsImplemented ? CommandAvailability.Unavailable : Scope == CommandScope.Application
        ? CommandAvailability.Always : CommandAvailability.Contextual;
    public string IconForState(string? state) => CommandCatalog.IconForState(Id, state);
}

public enum CommandAvailability
{
    Always,
    Contextual,
    Unavailable
}

[Flags]
public enum CommandScope
{
    None = 0,
    Application = 1,
    Downloads = 2,
    Archive = 4,
    Reader = 8,
    Gallery = 16,
    Media = 32,
    Settings = 64,
    Dialog = 128,
    About = 256,
    Sources = 512,
    Diagnostics = 1024,
}
