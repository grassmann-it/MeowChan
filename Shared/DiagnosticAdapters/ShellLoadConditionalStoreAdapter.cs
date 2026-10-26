// Candidate-only harness adapter. Link ONLY into DesktopSmoke after PF02 is present.
// Do not copy/link this optional file into a release-HEAD baseline: that version has no capability interface.
using System;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

internal sealed partial class HeldCommitStore : IConditionalWorkStateStore
{
    private IConditionalWorkStateStore Conditional => _inner as IConditionalWorkStateStore
        ?? throw new InvalidOperationException("The conditional smoke adapter requires the PF02 SQLite capability.");
    WorkResource? IConditionalWorkStateStore.FindResource(string url) => Conditional.FindResource(url);
    MediaWork? IConditionalWorkStateStore.FindMedia(MediaWorkKey key) => Conditional.FindMedia(key);
    bool IConditionalWorkStateStore.TryCommitMedia(MediaWork next, MediaWork? expected)
        => CommitMeasured(next, () => Conditional.TryCommitMedia(next, expected));
    WorkResource IConditionalWorkStateStore.CommitIntentUnderFence(WorkResource resource) => Conditional.CommitIntentUnderFence(resource);
    WorkResource? IConditionalWorkStateStore.CommitStateUnderFence(WorkResource resource) => Conditional.CommitStateUnderFence(resource);
    WorkResource? IConditionalWorkStateStore.CommitEndUnderFence(string url, long generation, ThreadSourceStateValue source,
        DateTimeOffset observedAt, string? finalSnapshotJson) => Conditional.CommitEndUnderFence(url, generation, source, observedAt, finalSnapshotJson);
}
