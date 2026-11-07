namespace MeowChan.Persistence;

// Zero/default is deliberately uncertain. Only the adapter that reconciled the
// real operation/receipt may attest SafeToResume; a successful Task is not proof.
internal enum ProfileExclusiveDisposition
{
    RecoveryRequired = 0,
    SafeToResume = 1
}

/// <summary>
/// Result of the complete exclusive operation, including reconciliation and
/// descendant/disposal completion. No status-name or cancellation inference.
/// </summary>
internal readonly record struct ProfileExclusiveOutcome<T>
{
    private ProfileExclusiveOutcome(T result, ProfileExclusiveDisposition disposition)
    {
        Result = result;
        Disposition = disposition;
    }

    public T Result { get; }
    public ProfileExclusiveDisposition Disposition { get; }

    internal static ProfileExclusiveOutcome<T> SafeToResume(T result) =>
        new(result, ProfileExclusiveDisposition.SafeToResume);

    internal static ProfileExclusiveOutcome<T> RecoveryRequired(T result) =>
        new(result, ProfileExclusiveDisposition.RecoveryRequired);
}
