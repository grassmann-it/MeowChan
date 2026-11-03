using MeowChan.Storage;

namespace MeowChan.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StorageRelocationRootCollection
{
    public const string Name = "Storage relocation roots";
}

[Collection(StorageRelocationRootCollection.Name)]
public sealed class StorageRelocationPreflightTests
{
    [Fact]
    public void EqualDestinationIsUnsafeAndDoesNotCreateAnything()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        Directory.CreateDirectory(source);
        string[] before = Directory.GetFileSystemEntries(workspace.Path);

        StorageRelocationPreflightResult result = Inspect(source, source);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.EqualSourceAndDestination);
        Assert.False(result.HasAllWritersProof);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.AllWritersProofUnavailable);
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path));
    }

    [Fact]
    public void DestinationNestedUnderSourceIsUnsafe()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        Directory.CreateDirectory(source);
        string destination = Path.Combine(source, "nested", "target");

        StorageRelocationPreflightResult result = Inspect(source, destination);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationNestedUnderSource);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void SourceNestedUnderDestinationIsUnsafe()
    {
        using TempDirectory workspace = new();
        string destination = Path.Combine(workspace.Path, "destination");
        Directory.CreateDirectory(destination);
        string source = Path.Combine(destination, "nested", "source");

        StorageRelocationPreflightResult result = Inspect(source, destination);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.SourceNestedUnderDestination);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.SourceMissing);
        Assert.False(Directory.Exists(source));
    }

    [Fact]
    public void ExistingDestinationFileIsUnsafeWithoutReplacingIt()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        string destination = Path.Combine(workspace.Path, "destination");
        Directory.CreateDirectory(source);
        File.WriteAllText(destination, "must remain");
        string[] before = Directory.GetFileSystemEntries(workspace.Path);
        string contentBefore = File.ReadAllText(destination);

        StorageRelocationPreflightResult result = Inspect(source, destination);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationNotDirectory);
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path));
        Assert.Equal(contentBefore, File.ReadAllText(destination));
    }

    [Fact]
    public void LinkDestinationAncestorIsUnsafe()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        string target = Path.Combine(workspace.Path, "physical-target");
        string link = Path.Combine(workspace.Path, "destination-link");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }

        StorageRelocationPreflightResult result = Inspect(source, Path.Combine(link, "new-root"));

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationLinkComponent);
        Assert.True(Directory.Exists(target));
        Assert.False(Directory.Exists(Path.Combine(target, "new-root")));
    }

    [Fact]
    public void SourceLinkIsUnsafeEvenWhenDestinationIsLexicallySeparate()
    {
        using TempDirectory workspace = new();
        string sourceTarget = Path.Combine(workspace.Path, "source-target");
        string sourceLink = Path.Combine(workspace.Path, "source-link");
        string destination = Path.Combine(workspace.Path, "destination");
        Directory.CreateDirectory(sourceTarget);
        Directory.CreateDirectory(destination);
        try { Directory.CreateSymbolicLink(sourceLink, sourceTarget); }
        catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }

        StorageRelocationPreflightResult result = Inspect(sourceLink, destination);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.SourceLinkComponent);
    }

    [Fact]
    public void MissingDestinationLeafIsObservedButNeverCreated()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        string destination = Path.Combine(workspace.Path, "new-destination");
        Directory.CreateDirectory(source);
        string[] before = Directory.GetFileSystemEntries(workspace.Path);

        StorageRelocationPreflightResult result = Inspect(source, destination);

        Assert.Equal(StorageRelocationPathSafety.NoLexicalOrLinkHazardObserved, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationLeafMissing);
        Assert.DoesNotContain(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationParentMissing);
        Assert.False(Directory.Exists(destination));
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path));
    }

    [Fact]
    public void CooperativeProducerObservationIsNeverAllWriterAuthority()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        Directory.CreateDirectory(source);
        StorageRelocationActiveStateEvidence evidence = new(
            StorageRelocationActiveStateEvidenceKind.CooperativeProducersQuiescent,
            "synthetic cooperative producer observation");

        StorageRelocationPreflightResult result = Inspect(source, Path.Combine(workspace.Path, "destination"), evidence);

        Assert.Equal(StorageRelocationPathSafety.NoLexicalOrLinkHazardObserved, result.PathSafety);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.False(result.HasAllWritersProof);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.OnlyCooperativeProducersObserved);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.AllWritersProofUnavailable);
    }

    [Fact]
    public void ActiveWriterObservationRemainsBlocked()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        Directory.CreateDirectory(source);
        StorageRelocationActiveStateEvidence evidence = new(
            StorageRelocationActiveStateEvidenceKind.ActiveWritersObserved,
            "synthetic active writer observation");

        StorageRelocationPreflightResult result = Inspect(source, Path.Combine(workspace.Path, "destination"), evidence);

        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.ActiveWritersObserved);
        Assert.False(result.HasAllWritersProof);
    }

    [Fact]
    public void MissingActiveStateEvidenceIsExplicitAndFailClosed()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        Directory.CreateDirectory(source);

        StorageRelocationPreflightResult result = Inspect(source, Path.Combine(workspace.Path, "destination"));

        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.ActiveStateEvidenceUnknown);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.AllWritersProofUnavailable);
        Assert.False(result.HasAllWritersProof);
    }

    [Fact]
    public void TrailingSeparatorDoesNotHideEqualRoot()
    {
        using TempDirectory workspace = new();
        StorageRelocationPreflightResult result = Inspect(workspace.Path,
            workspace.Path + Path.DirectorySeparatorChar);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.EqualSourceAndDestination);
    }

    [Fact]
    public void SharedTextPrefixIsNotNestedButIsStillNotAnApplyApproval()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "media");
        string destination = Path.Combine(workspace.Path, "media-other");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);

        StorageRelocationPreflightResult result = Inspect(source, destination);

        Assert.Equal(StorageRelocationPathSafety.NoLexicalOrLinkHazardObserved, result.PathSafety);
        Assert.DoesNotContain(result.Findings, finding => finding.Code is
            StorageRelocationFindingCode.DestinationNestedUnderSource or
            StorageRelocationFindingCode.SourceNestedUnderDestination);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.PhysicalPathIdentityUnknown);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.ReferenceInventoryUnavailable);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.CapacityAndAccessUnknown);
        Assert.False(result.HasAllWritersProof);
    }

    [Fact]
    public void FilesystemRootRetainsItsSeparatorAndContainsTheSource()
    {
        using TempDirectory workspace = new();
        string root = Path.GetPathRoot(workspace.Path)!;

        StorageRelocationPreflightResult result = Inspect(workspace.Path, root);

        Assert.Equal(root, result.DestinationRoot);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.SourceNestedUnderDestination);
        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
    }

    [Fact]
    public void MissingDestinationAncestorIsNotMistakenForAnAvailableLeaf()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        string destination = Path.Combine(workspace.Path, "absent-parent", "target");
        Directory.CreateDirectory(source);
        string[] before = Directory.GetFileSystemEntries(workspace.Path);

        StorageRelocationPreflightResult result = Inspect(source, destination);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationParentMissing);
        Assert.DoesNotContain(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationLeafMissing);
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path));
    }

    [Fact]
    public void ExistingSourceFileAndDestinationFileAncestorAreUnsafe()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source-file");
        string ancestor = Path.Combine(workspace.Path, "target-file");
        File.WriteAllText(source, "source stays");
        File.WriteAllText(ancestor, "target stays");

        StorageRelocationPreflightResult result = Inspect(source, Path.Combine(ancestor, "root"));

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.SourceNotDirectory);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationNotDirectory);
        Assert.Equal("source stays", File.ReadAllText(source));
        Assert.Equal("target stays", File.ReadAllText(ancestor));
    }

    [Fact]
    public void DanglingDestinationLinkIsUnsafeAndNeverTreatedAsAnAbsentLeaf()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        string target = Path.Combine(workspace.Path, "absent-target");
        string link = Path.Combine(workspace.Path, "dangling-link");
        Directory.CreateDirectory(source);
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return; // Native symlink support/privileges must be supplied by root validation.
        }

        StorageRelocationPreflightResult result = Inspect(source, link);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationLinkComponent);
        Assert.DoesNotContain(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationLeafMissing);
        Assert.False(Directory.Exists(target));
        Assert.Equal(target, new DirectoryInfo(link).LinkTarget);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void DotSegmentsAreRejectedBeforeNormalizationCanHideAnAncestor(string segment)
    {
        using TempDirectory workspace = new();
        string[] before = Directory.GetFileSystemEntries(workspace.Path);
        string destination = workspace.Path + Path.DirectorySeparatorChar + "possible-link"
            + Path.DirectorySeparatorChar + segment + Path.DirectorySeparatorChar + "target";

        StorageRelocationPreflightResult result = Inspect(workspace.Path, destination);

        Assert.Equal(StorageRelocationPathSafety.Unknown, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.InvalidPath);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative-root")]
    [InlineData("bad\0path")]
    public void InvalidPathIsUnknownAndFailClosedWithoutCreatingTheSource(string? destination)
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "not-created");

        StorageRelocationPreflightResult result = Inspect(source, destination!);

        Assert.Equal(StorageRelocationPathSafety.Unknown, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.InvalidPath);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.ActiveStateEvidenceUnknown);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Path));
    }

    [Fact]
    public void OverlongInputIsRejectedWithBoundedDiagnosticPaths()
    {
        using TempDirectory workspace = new();
        string destination = workspace.Path + Path.DirectorySeparatorChar
            + new string('x', StorageRelocationPreflight.MaximumPathCharacters);

        StorageRelocationPreflightResult result = Inspect(workspace.Path, destination);

        Assert.Equal(StorageRelocationPathSafety.Unknown, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.InvalidPath);
        Assert.True(result.DestinationRoot.Length <= StorageRelocationPreflight.MaximumPathCharacters);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Path));
    }

    [Fact]
    public void ComponentBudgetStopsBeforeFilesystemTraversalOrCreation()
    {
        using TempDirectory workspace = new();
        string destination = workspace.Path;
        for (int index = 0; index < StorageRelocationPreflight.MaximumPathComponents; index++)
            destination = Path.Combine(destination, "d");

        StorageRelocationPreflightResult result = Inspect(workspace.Path, destination);

        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.PathComponentLimitExceeded);
        Assert.DoesNotContain(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationParentMissing);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public void UnsupportedOrUnknownActiveStateIsNotCooperativeQuiescence(int kind)
    {
        using TempDirectory workspace = new();

        StorageRelocationPreflightResult result = Inspect(workspace.Path, Path.Combine(workspace.Path, "unused"), new((StorageRelocationActiveStateEvidenceKind)kind));

        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.ActiveStateEvidenceUnknown);
        Assert.DoesNotContain(result.Findings, finding => finding.Code == StorageRelocationFindingCode.OnlyCooperativeProducersObserved);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.False(result.HasAllWritersProof);
    }

    [Fact]
    public void CancellationDoesNotAcquireOrCreateAnything()
    {
        using TempDirectory workspace = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        StorageRelocationPreflightRequest request = new(Path.Combine(workspace.Path, "source"),
            Path.Combine(workspace.Path, "target"));

        Assert.ThrowsAny<OperationCanceledException>(() => StorageRelocationPreflight.Inspect(request, cancellation.Token));

        Assert.Empty(Directory.GetFileSystemEntries(workspace.Path));
    }

    [Fact]
    public void ExistingProfileAndMediaSentinelsAreNotOpenedForWriteOrChanged()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        string destination = Path.Combine(workspace.Path, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        string[] names = [".meowchan-manifest.json", ".ochan-manifest.json", ".meowchan-store.lock",
            ".ochan-store.lock", ".meowchan-profile.lock", ".meowchan-profile.protocol", "settings.json",
            "session.json", "work-state.sqlite", "archive.sqlite", "media.bin"];
        foreach (string name in names) File.WriteAllText(Path.Combine(source, name), "synthetic:" + name);
        string[] before = Directory.GetFileSystemEntries(source).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        string[] bytes = before.Select(File.ReadAllText).ToArray();
        // Held lock demonstrates the inspector is not attempting ProfileLease/store acquisition.
        using (FileStream held = new(Path.Combine(source, ".meowchan-profile.lock"), FileMode.Open,
                   FileAccess.Read, FileShare.None))
        {
            StorageRelocationPreflightResult result = Inspect(source, destination);
            Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
            Assert.False(result.HasAllWritersProof);
        }

        Assert.Equal(before, Directory.GetFileSystemEntries(source).OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.Equal(bytes, before.Select(File.ReadAllText).ToArray());
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    private static StorageRelocationPreflightResult Inspect(
        string source, string destination, StorageRelocationActiveStateEvidence? evidence = null)
        => StorageRelocationPreflight.Inspect(new StorageRelocationPreflightRequest(source, destination, evidence));
}
