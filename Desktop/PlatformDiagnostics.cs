using System;
using System.Collections.Generic;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;

namespace MeowChan.Desktop;

/// <summary>
/// Facts about the machine and the platform layer that a tester can read in Diagnostics and paste into a report. The entries
/// are always shown, after any request or problem; they are information, never a problem, and carry no retry or watch action. Texts are English on
/// purpose: they are pasted into reports, and nothing here contains a user name or a path.
/// </summary>
internal static class PlatformDiagnostics
{
    internal const string MotionKey = "platform:motion-signal";
    internal const string SummaryKey = "platform:summary";

    internal static IReadOnlyList<DiagnosticEntry> Entries()
    {
        OsMotionStatus motion = OsMotionSignal.Status;
        string summary = "OS reduce-motion signal: " + MotionSummary(motion);
        string details = summary + Environment.NewLine
            + "Effective: animations " + (MotionPolicy.IsOff ? "OFF" : "ON") + " (setting " + MotionPolicy.Setting
            + ", environment " + (MotionPolicy.FromEnvironment(Environment.GetEnvironmentVariable) is { } env ? (env ? "off" : "on") : "unset") + ")"
            + Environment.NewLine + "Detail: " + motion;
        List<DiagnosticEntry> entries = new();
        if (PlatformSummary.Line is { } line)
            entries.Add(new DiagnosticEntry(SummaryKey, Texts.T("diagnostics.platform"), "MeowChan", "Platform summary (paste into a report)", line, null)
            { Kind = DiagnosticKind.Information });
        entries.Add(
            new DiagnosticEntry(MotionKey, Texts.T("diagnostics.platform"), "MeowChan", summary, details, null)
            { Kind = DiagnosticKind.Information });
        return entries;
    }

    /// <summary>Short state word: tells "signal not read" apart from "signal says off".</summary>
    internal static string MotionSummary(OsMotionStatus status) => status.State switch
    {
        OsMotionState.DetectedOn => "detected ON",
        OsMotionState.DetectedOff => "detected OFF",
        OsMotionState.Unavailable => "UNAVAILABLE (" + status.Reason + ")",
        _ => "not read"
    };
}
