using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    // One startup lifetime per host/lease, acquired before constructing profile owners.
    // Access is serialized by _lifecycle (or by the not-yet-published constructor).
    private ProfileAdmissionToken? _profileStartupAdmission;

    private void ReleaseProfileStartupAdmission()
    {
        ProfileAdmissionToken? admission = _profileStartupAdmission;
        _profileStartupAdmission = null;
        admission?.Dispose();
    }

    private void FinishProfileStartup(bool succeeded)
    {
        // Called at the restoration boundary, before publishing host Ready/RecoveryFailed.
        // Failure disables new writers before releasing the startup work's ownership.
        if (succeeded)
        {
            ReleaseProfileStartupAdmission();
            _profileLease.Admission.CompleteStartup();
        }
        else
        {
            try { _profileLease.Admission.FailStartup(); }
            finally { ReleaseProfileStartupAdmission(); }
        }
    }

    private void BeginProfileShutdown()
    {
        // Caller holds _lifecycle and has closed host command admission. Startup
        // cannot publish Ready after this point, even if restoration later succeeds.
        if (_profileLease.Admission.Phase == ProfileAdmissionPhase.Starting)
            _profileLease.Admission.FailStartup();
        // With StartAsync scheduled, its worker owns the token until restoration
        // returns. Stop-before-Start has no worker and must release it here instead.
        if (_startTask == null) ReleaseProfileStartupAdmission();
        _profileLease.Admission.BeginShutdown();
    }
}
