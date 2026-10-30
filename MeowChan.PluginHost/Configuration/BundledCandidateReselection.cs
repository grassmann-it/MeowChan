namespace MeowChan.PluginHost.Configuration;

/// <summary>A new user selection, never evidence that an old candidate was shipped.</summary>
public sealed record BundledCandidateReselectionPreview(long ProfileRevision,
    IReadOnlyList<BundledCandidateReselection> Rows);

/// <summary>Exact frozen choice. Confirmation rederives identity, configuration and restrictions.</summary>
public sealed record BundledCandidateReselection(string PluginId, string OldCandidateId,
    string? NewCandidateId, string? SlotId, string? OldPath, string? NewPath,
    string? OldExecutionRevision, string? NewExecutionRevision, string? ConfigurationJson,
    string? BlockedReason)
{
    public bool CanConfirm => BlockedReason is null && NewCandidateId is not null && ConfigurationJson is not null;
}
