using System;
using System.Threading.Tasks;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    private readonly Guid _experimentalActivationSessionId = Guid.NewGuid();

    internal Task<ExperimentalSourceActivationProjection> ProjectExperimentalSourceActivationAsync(string url)
        => RunInteractiveAsync(nameof(ProjectExperimentalSourceActivationAsync), url, () => ProjectExperimentalSourceActivationAdmittedCoreAsync(url));

    private async Task<ExperimentalSourceActivationProjection> ProjectExperimentalSourceActivationAdmittedCoreAsync(string url)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            ExperimentalSourceActivationObservation observation = await ObserveExperimentalSourceActivationAsync(url).ConfigureAwait(false);
            if (!observation.IsReady || observation.Error != null)
                return new(ExperimentalSourceActivationProjectionStatus.Unavailable, Error: observation.Error ?? "The desktop session is not ready.");
            return observation.Target is { IsEnabled: false } target
                ? new(ExperimentalSourceActivationProjectionStatus.ConfirmationRequired, target)
                : new(ExperimentalSourceActivationProjectionStatus.NotRequired, observation.Target);
        }
        catch (Exception error) { return new(ExperimentalSourceActivationProjectionStatus.Unavailable, Error: error.Message); }
        finally { _commands.Release(); }
    }

    internal Task<ExperimentalSourceActivationResult> ActivateAndAllowExperimentalSourceAsync(ExperimentalSourceActivationTarget target)
        => RunInteractiveWithAdmissionAsync(nameof(ActivateAndAllowExperimentalSourceAsync), target.PluginOwnerId + "/" + target.SourceId, admission => ActivateAndAllowExperimentalSourceAdmittedCoreAsync(target, admission));

    private async Task<ExperimentalSourceActivationResult> ActivateAndAllowExperimentalSourceAdmittedCoreAsync(ExperimentalSourceActivationTarget target, ProfileAdmissionToken admission)
    {
        ArgumentNullException.ThrowIfNull(target);
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ExperimentalSourceActivationCoordinator.ExecuteAsync(target,
                () => ObserveExperimentalSourceActivationAsync(target.Url),
                async current =>
                {
                    EnsureReady();
                    // A family site first gets its own switch; while its plugin is off this changes nothing yet.
                    if (current.OwnerEnabled is not null && !Plugins!.SetChanSiteEnabled(current.SourceId, true))
                        throw new InvalidOperationException("The site switch could not be saved.");
                    if (current.OwnerEnabled == true) return;
                    // The existing lifecycle callback activates the adapter and increments policy revision.
                    // Its exception may occur after SetEnabledAsync has durably saved Enabled=true.
                    await Plugins!.ProfileStore.SetEnabledAsync(current.PluginOwnerId, true, current.ProfileRevision).ConfigureAwait(false);
                },
                async current =>
                {
                    EnsureReady();
                    if (!current.RequestsMismatchPermission || !ExperimentalSourceActivationPolicy.RequestsMismatchPermission(current.PluginOwnerId, current.SourceId))
                        throw new ExperimentalSourceActivationChangedException();
                    var verified = await ObserveExperimentalSourceActivationAsync(current.Url).ConfigureAwait(false);
                    if (!verified.IsReady || verified.Target != current) throw new ExperimentalSourceActivationChangedException();
                    // Do not reacquire _commands through the public permission entry point.
                    await SetSourceMediaIntegrityPermissionCoreAsync(current.PluginOwnerId, current.SourceId, true,
                        current.PolicyRevision, admission).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A read can fail before any mutation; an unknown actual state is never success.
            return new(ExperimentalSourceActivationStatus.StaleTarget, Error: error.Message);
        }
        finally { _commands.Release(); SignalChanged(); }
    }

    private async Task<ExperimentalSourceActivationObservation> ObserveExperimentalSourceActivationAsync(string url)
    {
        if (Plugins == null) return new(null, null, State == DesktopSessionState.Ready);
        var persisted = await Plugins.ProfileStore.ReadAsync().ConfigureAwait(false);
        return Plugins.ObserveExperimentalSourceActivation(_experimentalActivationSessionId, ProfileDirectory, url,
            persisted, State == DesktopSessionState.Ready);
    }
}
