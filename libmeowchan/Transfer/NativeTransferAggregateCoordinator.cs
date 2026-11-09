
namespace MeowChan.Transfer;

/// <summary>
/// Result of handing a validated aggregate plan to the media consumer boundary.
/// A validated plan is not an execution capability; the existing v2 media contracts
/// still materialize exactly one resource and must remain the only single-thread path.
/// </summary>
internal enum NativeTransferAggregateMediaMaterializationError
{
    None,
    InvalidPlan,
    SingleThreadRequiresNativeMediaPipeline,
    MultiThreadMaterializationUnsupported
}

internal sealed record NativeTransferAggregateMediaMaterializationResult(
    NativeTransferAggregateMergePlan? ValidatedPlan,
    NativeTransferAggregateMediaMaterializationError Error)
{
    internal bool IsValidated => ValidatedPlan != null
        && Error != NativeTransferAggregateMediaMaterializationError.InvalidPlan;

    internal bool IsMaterializable => ValidatedPlan != null
        && Error == NativeTransferAggregateMediaMaterializationError.None;
}

/// <summary>
/// Coordinates the bounded multi-thread admission slice with the existing media
/// import/merge boundary. It performs no target reads, filesystem work, leases, or
/// SQL writes. Until a batch materializer exists, multi-thread plans stop here with
/// an explicit typed rejection rather than being routed through the single-thread v2
/// contracts or silently dropping threads.
/// </summary>
internal static class NativeTransferAggregateMediaMaterialization
{
    internal static NativeTransferAggregateMediaMaterializationResult Prepare(
        NativeTransferAggregateMergePlan? plan)
    {
        if (!NativeTransferAggregateMergePlanner.IsWellFormed(plan))
            return new(null, NativeTransferAggregateMediaMaterializationError.InvalidPlan);

        if (plan!.Threads.Length == 1)
        {
            // Keep the established v2 import/merge entry points authoritative for
            // single-thread materialization; this coordinator must not become a
            // parallel bypass around their owner-issued plan and recovery guards.
            return new(plan, NativeTransferAggregateMediaMaterializationError.SingleThreadRequiresNativeMediaPipeline);
        }

        return new(plan, NativeTransferAggregateMediaMaterializationError.MultiThreadMaterializationUnsupported);
    }
}
