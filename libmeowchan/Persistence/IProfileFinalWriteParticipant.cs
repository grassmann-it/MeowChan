using System;
using System.Threading.Tasks;

namespace MeowChan.Persistence;

// The existing Registry lane owns both families. No public capability/token role,
// callback I/O, full Stop wait, or additional Exclusive reservation is introduced.
internal interface IProfileFinalWriteParticipant
{
    void CloseTransitionCommandIngress();
    Task CutAndDrainAsync();
    void ReleaseDrainedEpoch();
    void InstallReadyEpoch();
    void OpenAfterProducerActivation();
    void FailTransition(Exception error);
    void AbortBeforeCut();
    void ReleaseStoppedEpoch();
}
