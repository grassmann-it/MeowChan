using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

/// <summary>
/// Arithmetic contract tests for the physical merge budget.  These deliberately do
/// not create 2-GiB files (and do not fabricate SQL rows or manifests); the planner
/// supplies the measured target-root total and measured staged payload descriptors.
/// </summary>
public sealed class NativeMediaMergePhysicalBudgetTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;
    private const long Cap = 2 * GiB;

    [Fact]
    public void TwoExisting768MiBRootsPlusOneNew768MiBPayloadAreRejected()
    {
        TransferBlobDescriptor[] safeRoot = [Blob(768 * MiB, 'a')];

        Assert.False(NativeMediaMergePhysicalBudget.WithinLimit(2 * 768 * MiB, safeRoot));
    }

    [Fact]
    public void ExactCapIsAcceptedAndOneByteOverCapIsRejected()
    {
        Assert.True(NativeMediaMergePhysicalBudget.WithinLimit(Cap - GiB, [Blob(GiB, 'a')]));
        Assert.False(NativeMediaMergePhysicalBudget.WithinLimit(Cap - GiB + 1, [Blob(GiB, 'a')]));
        Assert.True(NativeMediaMergePhysicalBudget.WithinLimit(Cap, Array.Empty<TransferBlobDescriptor>()));
        Assert.False(NativeMediaMergePhysicalBudget.WithinLimit(Cap + 1, Array.Empty<TransferBlobDescriptor>()));
    }

    [Fact]
    public void DuplicateShaWithinOneSafeRootCountsOnlyOnce()
    {
        TransferBlobDescriptor[] safeRoot =
        [
            Blob(768 * MiB, 'a'),
            Blob(768 * MiB, 'a'),
            Blob(512 * MiB, 'b')
        ];

        Assert.True(NativeMediaMergePhysicalBudget.WithinLimit(768 * MiB, safeRoot));
        Assert.False(NativeMediaMergePhysicalBudget.WithinLimit(768 * MiB + 512 * MiB, safeRoot));
    }

    [Fact]
    public void ExistingRootBytesAreNeverDeduplicatedAgainstNewRootPayloads()
    {
        // The same SHA already exists in an active target root.  A new safe root is
        // still a new physical copy, so its payload must be added again.
        Assert.False(NativeMediaMergePhysicalBudget.WithinLimit(Cap - 1, [Blob(2, 'a')]));
        Assert.True(NativeMediaMergePhysicalBudget.WithinLimit(Cap - 1, Array.Empty<TransferBlobDescriptor>()));
    }

    [Fact]
    public void ExactReuseHasNoAdditionalPhysicalBytes()
    {
        // Exact target-retained reuse creates no safe root.  The planner therefore
        // passes an empty descriptor set, regardless of the reused payload's SHA.
        Assert.True(NativeMediaMergePhysicalBudget.WithinLimit(Cap, Array.Empty<TransferBlobDescriptor>()));
    }

    [Fact]
    public void SameShaWithDifferentLengthsIsInvalidData()
    {
        TransferBlobDescriptor[] descriptors = [Blob(10, 'a'), Blob(11, 'a')];

        Assert.Throws<InvalidDataException>(() =>
            NativeMediaMergePhysicalBudget.WithinLimit(0, descriptors));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2147483649L)]
    public void InvalidExistingPhysicalBytesAreRejected(long existingBytes)
    {
        Assert.False(NativeMediaMergePhysicalBudget.WithinLimit(existingBytes,
            [Blob(1, 'a')]));
    }

    [Fact]
    public void IndividualPayloadsMayNotExceedOneGiB()
    {
        Assert.False(NativeMediaMergePhysicalBudget.WithinLimit(0, [Blob(GiB + 1, 'a')]));
        Assert.True(NativeMediaMergePhysicalBudget.WithinLimit(0, [Blob(GiB, 'a')]));
    }

    private static TransferBlobDescriptor Blob(long length, char shaMarker)
        => new(length, new string(shaMarker, 64));
}
