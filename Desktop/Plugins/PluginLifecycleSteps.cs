using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.Plugins;

internal enum PluginStepState
{
    /// <summary>This step has happened.</summary>
    Done,
    /// <summary>The first step that has not happened yet; the next action belongs to it.</summary>
    Next,
    /// <summary>A step after the next one.</summary>
    Later,
    /// <summary>Something blocks this step; the plugin's problem text says what.</summary>
    Problem
}

internal enum PluginNextAction
{
    /// <summary>Nothing to do: the plugin is in use.</summary>
    None,
    /// <summary>The file is no longer in the plugin folder.</summary>
    FileMissing,
    /// <summary>The file or build has a problem the user has to fix first.</summary>
    FixProblem,
    /// <summary>A new file: it is built, described and registered at the next start only.</summary>
    RestartToRegister,
    /// <summary>The "Enable plugin" action; takes effect now.</summary>
    Enable,
    /// <summary>The "Use this version next start" action, then a restart.</summary>
    SelectBuild,
    /// <summary>A selected or saved build waits for the next start.</summary>
    RestartToUse,
    /// <summary>The explicit review of an automatic SDK transition.</summary>
    ApproveSdkTransition,
    /// <summary>The bundled build changed and waits for the "Review bundled repairs" confirmation; a restart alone never starts it.</summary>
    ReviewRepair,
    /// <summary>The plugin is in use and further saved settings apply at the next start.</summary>
    SavedChangesApplyAtRestart
}

internal sealed record PluginLifecycleStep(string Id, PluginStepState State);

/// <summary>The three stages every plugin file goes through, as shown on the plugin detail.</summary>
internal sealed record PluginLifecycleView(IReadOnlyList<PluginLifecycleStep> Steps, PluginNextAction Next, bool ActiveNow)
{
    internal const string Installed = "installed", Enabled = "enabled", Active = "active";
}

/// <summary>
/// Read-only projection of one catalog entry onto Installed → Enabled → Active (after restart), with the single next action.
/// It reads the same facts the status line and the action buttons read (<see cref="PluginEntryPresentation"/>, the profile's
/// activation and the inventory); it decides nothing and changes nothing, so no approval, consent or activation rule moves.
/// </summary>
internal static class PluginLifecycleSteps
{
    internal static PluginLifecycleView Create(PluginCatalogEntry entry, PluginEntryPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(presentation);
        Facts facts = new(entry, presentation);

        PluginStepState installed = facts.FileMissing || facts.FileProblem ? PluginStepState.Problem : PluginStepState.Done;
        PluginNextAction next;
        if (facts.FileMissing) next = PluginNextAction.FileMissing;
        else if (facts.FileProblem) next = PluginNextAction.FixProblem;
        else if (!facts.Registered) next = PluginNextAction.RestartToRegister;
        else if (presentation.HasAutomaticSdkTransition) next = PluginNextAction.ApproveSdkTransition;
        // Before "Enable": turning a plugin on does not start a build that waits for its repair review.
        else if (presentation.NeedsRepairReview) next = PluginNextAction.ReviewRepair;
        else if (!facts.Enabled) next = PluginNextAction.Enable;
        else if (presentation.CandidateChoiceRequired) next = PluginNextAction.SelectBuild;
        else if (presentation.IsActiveCandidate && presentation.HasWorkerProblem) next = PluginNextAction.FixProblem;
        else if (presentation.IsActiveCandidate)
            next = presentation.PendingKind != PluginPendingKind.None ? PluginNextAction.SavedChangesApplyAtRestart : PluginNextAction.None;
        else next = PluginNextAction.RestartToUse;

        bool activeNow = facts.Enabled && presentation.IsActiveCandidate && !presentation.HasWorkerProblem;
        PluginStepState enabled = installed != PluginStepState.Done ? PluginStepState.Later : facts.Enabled ? PluginStepState.Done : PluginStepState.Next;
        PluginStepState active = activeNow ? PluginStepState.Done
            : facts.Enabled && presentation.IsActiveCandidate && presentation.HasWorkerProblem ? PluginStepState.Problem
            : facts.Enabled && next == PluginNextAction.ReviewRepair ? PluginStepState.Problem
            : PluginStepState.Later;
        // The first step that has not happened is the one the next action belongs to (a blocked step keeps its Problem tone).
        if (installed == PluginStepState.Done && enabled == PluginStepState.Done && active == PluginStepState.Later) active = PluginStepState.Next;
        return new(new PluginLifecycleStep[]
        {
            new(PluginLifecycleView.Installed, installed),
            new(PluginLifecycleView.Enabled, enabled),
            new(PluginLifecycleView.Active, active)
        }, next, activeNow);
    }

    private readonly struct Facts
    {
        internal bool FileMissing { get; }
        internal bool FileProblem { get; }
        internal bool Registered { get; }
        internal bool Enabled { get; }

        internal Facts(PluginCatalogEntry entry, PluginEntryPresentation presentation)
        {
            // A retained profile record without any file of its own: the file left the folder, or never was in this one.
            bool fileKnown = entry.Candidate != null ? entry.Candidate.IsPresent : entry.InventoryPath != null || entry.SourcePath != null;
            FileMissing = !fileKnown;
            // Only blocking diagnostics count: the inventory's "warning" severity (for example a truncated folder scan) does not.
            FileProblem = fileKnown && (!string.IsNullOrEmpty(presentation.CandidateProblem)
                || entry.Diagnostics.Any(diagnostic => !string.Equals(diagnostic.Severity, "warning", StringComparison.OrdinalIgnoreCase)));
            Registered = entry.Activation != null;
            Enabled = entry.Activation?.Enabled == true;
        }
    }
}
