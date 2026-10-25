using System;
using System.Threading.Tasks;
using MeowChan.PluginHost.Configuration;

namespace MeowChan.Desktop;

internal enum ExperimentalSourceActivationProjectionStatus { NotRequired, ConfirmationRequired, Unavailable }
internal enum ExperimentalSourceActivationStatus { Succeeded, StaleTarget, ActivationFailed, GrantFailed }

// A dialog captures runtime authority, not a candidate's declaration or a mutable UI row.
internal sealed record ExperimentalSourceActivationTarget(Guid SessionId, string ProfileId,
    string PluginOwnerId, string SourceId, string SourceName, string Url, string CandidateId,
    string ExecutionRevision, string CompilationFingerprint, string AssemblySha256,
    long ProfileRevision, long LifecycleRevision, long PolicyRevision, bool IsEnabled, bool IsGranted);
internal sealed record ExperimentalSourceActivationProjection(ExperimentalSourceActivationProjectionStatus Status,
    ExperimentalSourceActivationTarget? Target = null, string? Error = null);
internal sealed record ExperimentalSourceActivationActualState(bool IsEnabled, bool IsActive, bool IsGranted,
    bool IsEffective, long ProfileRevision, long LifecycleRevision, long PolicyRevision);
internal sealed record ExperimentalSourceActivationResult(ExperimentalSourceActivationStatus Status,
    ExperimentalSourceActivationActualState? ActualState = null,
    ExperimentalSourceActivationTarget? RetryTarget = null, string? Error = null);
internal sealed record ExperimentalSourceActivationObservation(ExperimentalSourceActivationTarget? Target,
    ExperimentalSourceActivationActualState? ActualState, bool IsReady, string? Error = null);

internal sealed class ExperimentalSourceActivationChangedException() : InvalidOperationException("The source activation target changed before saving.");

/// <summary>
/// Caller owns the host command gate throughout. Enable and grant are independent durable writes;
/// exceptions are followed by an observation, never an inferred rollback or an automatic retry.
/// </summary>
internal static class ExperimentalSourceActivationCoordinator
{
    internal static async Task<ExperimentalSourceActivationResult> ExecuteAsync(ExperimentalSourceActivationTarget target,
        Func<Task<ExperimentalSourceActivationObservation>> observe,
        Func<ExperimentalSourceActivationTarget, Task> enable,
        Func<ExperimentalSourceActivationTarget, Task> grant)
    {
        ExperimentalSourceActivationObservation current = await observe().ConfigureAwait(false);
        if (!Matches(current, target)) return Stale(current);
        if (!target.IsEnabled)
        {
            try { await enable(target).ConfigureAwait(false); }
            catch (Exception error)
            {
                return await FailedAsync(ExperimentalSourceActivationStatus.ActivationFailed, target, error, observe).ConfigureAwait(false);
            }
            current = await observe().ConfigureAwait(false);
            // Enable is the only permitted intervening mutation. Reading a new revision must not
            // silently accept a concurrent revoke, disable, owner choice or configuration change.
            target = target with { IsEnabled = true, ProfileRevision = checked(target.ProfileRevision + 1),
                LifecycleRevision = checked(target.LifecycleRevision + 1), PolicyRevision = checked(target.PolicyRevision + 1) };
            if (!Matches(current, target)) return Stale(current);
        }
        if (current.ActualState is not { IsActive: true }) return Stale(current);
        if (!target.IsGranted)
        {
            try { await grant(target).ConfigureAwait(false); }
            catch (Exception error)
            {
                return await FailedAsync(ExperimentalSourceActivationStatus.GrantFailed, target, error, observe).ConfigureAwait(false);
            }
            current = await observe().ConfigureAwait(false);
            target = target with { IsGranted = true, PolicyRevision = checked(target.PolicyRevision + 1) };
        }
        if (!Matches(current, target) || current.ActualState is not { IsActive: true, IsEffective: true }) return Stale(current);
        return new(ExperimentalSourceActivationStatus.Succeeded, current.ActualState);
    }

    private static bool Matches(ExperimentalSourceActivationObservation observation, ExperimentalSourceActivationTarget target) =>
        observation.IsReady && observation.Target == target;

    private static ExperimentalSourceActivationResult Stale(ExperimentalSourceActivationObservation observation) =>
        new(ExperimentalSourceActivationStatus.StaleTarget, observation.ActualState, Error: observation.Error ?? "The source activation target changed. Submit the URL again.");

    private static async Task<ExperimentalSourceActivationResult> FailedAsync(ExperimentalSourceActivationStatus status,
        ExperimentalSourceActivationTarget original, Exception error, Func<Task<ExperimentalSourceActivationObservation>> observe)
    {
        ExperimentalSourceActivationObservation current;
        try { current = await observe().ConfigureAwait(false); }
        catch (Exception readError) { return new(status, Error: error.Message + " " + readError.Message); }
        if (error is ExperimentalSourceActivationChangedException or PluginProfileConflictException
            || status == ExperimentalSourceActivationStatus.GrantFailed && !Matches(current, original))
            return Stale(current);
        ExperimentalSourceActivationTarget? retry = current.IsReady && current.Target is { } next
            && SameAuthority(original, next) && current.ActualState is { } actual
            && actual.IsEnabled == actual.IsActive ? next : null;
        return new(status, current.ActualState, retry, error.Message);
    }

    private static bool SameAuthority(ExperimentalSourceActivationTarget a, ExperimentalSourceActivationTarget b) =>
        a.SessionId == b.SessionId && a.ProfileId == b.ProfileId && a.PluginOwnerId == b.PluginOwnerId
        && a.SourceId == b.SourceId && a.Url == b.Url && a.CandidateId == b.CandidateId
        && a.ExecutionRevision == b.ExecutionRevision && a.CompilationFingerprint == b.CompilationFingerprint
        && a.AssemblySha256 == b.AssemblySha256;
}
