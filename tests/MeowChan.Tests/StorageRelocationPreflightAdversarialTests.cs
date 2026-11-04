using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using MeowChan.Storage;

namespace MeowChan.Tests;

// Reuses the existing root collection so these cases do not overlap other
// StorageRelocationPreflight root fixtures. Root owns execution; this slice runs none.
[Collection(StorageRelocationRootCollection.Name)]
public sealed class StorageRelocationPreflightAdversarialTests
{
    [Fact]
    public void MaximumPathLengthIsBoundedAndDoesNotCreateTheDestination()
    {
        using TempDirectory workspace = new();
        string destination = Path.Combine(workspace.Path,
            new string('x', StorageRelocationPreflight.MaximumPathCharacters));
        string[] before = Directory.GetFileSystemEntries(workspace.Path);

        StorageRelocationPreflightResult result = Inspect(workspace.Path, destination);

        Assert.Equal(StorageRelocationPathSafety.Unknown, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.InvalidPath);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.True(result.DestinationRoot.Length <= StorageRelocationPreflight.MaximumPathCharacters);
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path));
    }

    [Fact]
    public void MaximumComponentBudgetIsBoundedBeforeMissingAncestorsCanLookCreatable()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        Directory.CreateDirectory(source);
        string destination = Path.Combine(workspace.Path, "destination");
        for (int index = 0; index < StorageRelocationPreflight.MaximumPathComponents; index++)
            destination = Path.Combine(destination, "segment");
        string[] before = Directory.GetFileSystemEntries(workspace.Path);

        StorageRelocationPreflightResult result = Inspect(source, destination);

        Assert.Equal(StorageRelocationPathSafety.Unknown, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.PathComponentLimitExceeded);
        Assert.DoesNotContain(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationParentMissing);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void DotSegmentsAreRejectedBeforeFullPathCanHideAComponent(string segment)
    {
        using TempDirectory workspace = new();
        string destination = workspace.Path + Path.DirectorySeparatorChar + "link-or-root"
            + Path.DirectorySeparatorChar + segment + Path.DirectorySeparatorChar + "target";
        string[] before = Directory.GetFileSystemEntries(workspace.Path);

        StorageRelocationPreflightResult result = Inspect(workspace.Path, destination);

        Assert.Equal(StorageRelocationPathSafety.Unknown, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.InvalidPath);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path));
    }

    [Fact]
    public void EqualRootsWithDifferentTrailingSeparatorsRemainUnsafe()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        Directory.CreateDirectory(source);
        string[] before = Directory.GetFileSystemEntries(workspace.Path);

        StorageRelocationPreflightResult result = Inspect(source,
            source + Path.DirectorySeparatorChar + Path.AltDirectorySeparatorChar);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.EqualSourceAndDestination);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path));
    }

    [Fact]
    public void DestinationNestedUnderSourceIsBlockedWithoutCreatingNestedDirectories()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        string destination = Path.Combine(source, "nested", "destination");
        Directory.CreateDirectory(source);
        string[] before = Directory.GetFileSystemEntries(source);

        StorageRelocationPreflightResult result = Inspect(source, destination);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationNestedUnderSource);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.False(Directory.Exists(destination));
        Assert.Equal(before, Directory.GetFileSystemEntries(source));
    }

    [Fact]
    public void SourceNestedUnderDestinationIsBlockedWithoutCreatingTheSource()
    {
        using TempDirectory workspace = new();
        string destination = Path.Combine(workspace.Path, "destination");
        string source = Path.Combine(destination, "nested", "source");
        Directory.CreateDirectory(destination);
        string[] before = Directory.GetFileSystemEntries(workspace.Path);

        StorageRelocationPreflightResult result = Inspect(source, destination);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.SourceNestedUnderDestination);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.SourceMissing);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.False(Directory.Exists(source));
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path));
    }

    [Fact]
    public void DanglingDestinationLinkIsNotAValidMissingLeaf()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        string missingTarget = Path.Combine(workspace.Path, "missing-target");
        string destinationLink = Path.Combine(workspace.Path, "destination-link");
        Directory.CreateDirectory(source);
        // Missing platform capability must fail fixture setup, not silently pass.
        Directory.CreateSymbolicLink(destinationLink, missingTarget);

        string[] before = Directory.GetFileSystemEntries(workspace.Path)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        StorageRelocationPreflightResult result = Inspect(source, destinationLink);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationLinkComponent);
        Assert.DoesNotContain(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationLeafMissing);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.False(Directory.Exists(missingTarget));
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void SourceLinkIsBlockedEvenWhenTheDestinationIsOrdinary()
    {
        using TempDirectory workspace = new();
        string sourceTarget = Path.Combine(workspace.Path, "source-target");
        string sourceLink = Path.Combine(workspace.Path, "source-link");
        string destination = Path.Combine(workspace.Path, "destination");
        Directory.CreateDirectory(sourceTarget);
        Directory.CreateDirectory(destination);
        Directory.CreateSymbolicLink(sourceLink, sourceTarget);

        StorageRelocationPreflightResult result = Inspect(sourceLink, destination);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.SourceLinkComponent);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.True(Directory.Exists(sourceTarget));
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public void SourceAndDestinationFilesAreNotTreatedAsDirectories()
    {
        using TempDirectory workspace = new();
        string sourceFile = Path.Combine(workspace.Path, "source-file");
        string destinationFile = Path.Combine(workspace.Path, "destination-file");
        File.WriteAllText(sourceFile, "source sentinel");
        File.WriteAllText(destinationFile, "destination sentinel");
        string[] before = Directory.GetFileSystemEntries(workspace.Path)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();

        StorageRelocationPreflightResult result = Inspect(sourceFile, destinationFile);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.SourceNotDirectory);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationNotDirectory);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.Equal("source sentinel", File.ReadAllText(sourceFile));
        Assert.Equal("destination sentinel", File.ReadAllText(destinationFile));
    }

    [Fact]
    public void ExistingDestinationAncestorFileIsFailClosedWithoutReplacingIt()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        string destinationAncestor = Path.Combine(workspace.Path, "destination-file");
        Directory.CreateDirectory(source);
        File.WriteAllText(destinationAncestor, "do not replace");
        string[] before = Directory.GetFileSystemEntries(workspace.Path)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();

        StorageRelocationPreflightResult result = Inspect(source,
            Path.Combine(destinationAncestor, "new-root"));

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationNotDirectory);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.Equal("do not replace", File.ReadAllText(destinationAncestor));
    }

    [Fact]
    public void PreCancelledInspectionThrowsWithoutCreatingRoots()
    {
        using TempDirectory workspace = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        StorageRelocationPreflightRequest request = new(
            Path.Combine(workspace.Path, "not-created-source"),
            Path.Combine(workspace.Path, "not-created-destination"));
        string[] before = Directory.GetFileSystemEntries(workspace.Path);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            StorageRelocationPreflight.Inspect(request, cancellation.Token));

        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path));
    }

    [Fact]
    public void MissingDestinationLeafIsObservedWithoutCreationOrWriteProbe()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        string destination = Path.Combine(workspace.Path, "new-destination");
        Directory.CreateDirectory(source);
        string[] before = Directory.GetFileSystemEntries(workspace.Path);

        StorageRelocationPreflightResult result = Inspect(source, destination);

        Assert.Equal(StorageRelocationPathSafety.NoLexicalOrLinkHazardObserved, result.PathSafety);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.DestinationLeafMissing);
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.False(Directory.Exists(destination));
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.Path));
    }

    [Fact]
    public void CooperativeEvidenceStillCannotManufactureAnAllWriterProof()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        Directory.CreateDirectory(source);
        StorageRelocationActiveStateEvidence evidence = new(
            StorageRelocationActiveStateEvidenceKind.CooperativeProducersQuiescent,
            "synthetic cooperative-only evidence");

        StorageRelocationPreflightResult result = Inspect(source,
            Path.Combine(workspace.Path, "destination"), evidence);

        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.False(result.HasAllWritersProof);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.OnlyCooperativeProducersObserved);
        Assert.Contains(result.Findings, finding => finding.Code == StorageRelocationFindingCode.AllWritersProofUnavailable);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void CharacterLimitPlusOneAndPlusTwoRejectEitherRoleWithExactBoundedEcho(bool oversizedSource, int excess)
    {
        using TempDirectory workspace = new();
        string prefix = workspace.Path + Path.DirectorySeparatorChar;
        string overlong = prefix + new string('a',
            StorageRelocationPreflight.MaximumPathCharacters + excess - prefix.Length);
        Assert.Equal(StorageRelocationPreflight.MaximumPathCharacters + excess, overlong.Length);
        string[] before = SnapshotTree(workspace.Path);

        StorageRelocationPreflightResult result = oversizedSource
            ? Inspect(overlong, workspace.Path)
            : Inspect(workspace.Path, overlong);

        Assert.Equal(StorageRelocationPathSafety.Unknown, result.PathSafety);
        AssertFinding(result, StorageRelocationFindingCode.InvalidPath);
        Assert.Equal(overlong[..StorageRelocationPreflight.MaximumPathCharacters],
            oversizedSource ? result.SourceRoot : result.DestinationRoot);
        Assert.DoesNotContain(result.Findings, finding => finding.Code == StorageRelocationFindingCode.PathComponentLimitExceeded);
        AssertClosed(result);
        Assert.Equal(before, SnapshotTree(workspace.Path));
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void ExactComponentBoundaryIsInclusiveAndCountsFilesystemRoot(bool deepSource, int offset)
    {
        using TempDirectory workspace = new();
        string ordinaryRoot = Path.Combine(workspace.Path, "ordinary");
        Directory.CreateDirectory(ordinaryRoot);
        int componentCount = 0;
        for (DirectoryInfo? current = new(workspace.Path); current != null; current = current.Parent)
            componentCount++;
        int wanted = StorageRelocationPreflight.MaximumPathComponents + offset;
        Assert.True(componentCount < wanted);
        string deepPath = workspace.Path;
        for (int count = componentCount; count < wanted; count++)
            deepPath = Path.Combine(deepPath, "d");
        string[] before = SnapshotTree(workspace.Path);

        StorageRelocationPreflightResult result = deepSource
            ? Inspect(deepPath, ordinaryRoot) : Inspect(ordinaryRoot, deepPath);

        if (offset > 0)
        {
            Assert.Equal(StorageRelocationPathSafety.Unknown, result.PathSafety);
            AssertFinding(result, StorageRelocationFindingCode.PathComponentLimitExceeded);
            Assert.DoesNotContain(result.Findings, finding => finding.Code is
                StorageRelocationFindingCode.SourceMissing or StorageRelocationFindingCode.DestinationParentMissing);
        }
        else
        {
            // No deep tree is created. At/below the limit the first absent ancestor
            // is observed; above it the component budget wins before metadata I/O.
            Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
            Assert.DoesNotContain(result.Findings, finding => finding.Code == StorageRelocationFindingCode.PathComponentLimitExceeded);
            AssertFinding(result, deepSource ? StorageRelocationFindingCode.SourceMissing
                : StorageRelocationFindingCode.DestinationParentMissing);
        }
        AssertClosed(result);
        Assert.Equal(before, SnapshotTree(workspace.Path));
    }

    [Theory]
    [InlineData(false, ".")]
    [InlineData(false, "..")]
    [InlineData(true, ".")]
    [InlineData(true, "..")]
    public void DotSegmentAfterRealLinkIsRejectedRatherThanNormalizedAway(bool sourceRole, string dot)
    {
        using TempDirectory workspace = new();
        string target = Path.Combine(workspace.Path, "target");
        string link = Path.Combine(workspace.Path, "link");
        string ordinary = Path.Combine(workspace.Path, "ordinary");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(ordinary);
        Directory.CreateSymbolicLink(link, target);
        string suspect = link + Path.DirectorySeparatorChar + dot + Path.DirectorySeparatorChar + "missing";
        string[] before = SnapshotTree(workspace.Path);

        StorageRelocationPreflightResult result = sourceRole
            ? Inspect(suspect, ordinary) : Inspect(ordinary, suspect);

        Assert.Equal(StorageRelocationPathSafety.Unknown, result.PathSafety);
        AssertFinding(result, StorageRelocationFindingCode.InvalidPath);
        AssertClosed(result);
        Assert.Equal(before, SnapshotTree(workspace.Path));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DanglingLinksAtLeafOrAncestorAreHazardsInEitherRole(bool sourceRole, bool ancestor)
    {
        using TempDirectory workspace = new();
        string ordinary = Path.Combine(workspace.Path, "ordinary");
        string link = Path.Combine(workspace.Path, "dangling");
        string absentTarget = Path.Combine(workspace.Path, "absent");
        Directory.CreateDirectory(ordinary);
        Directory.CreateSymbolicLink(link, absentTarget);
        string suspect = ancestor ? Path.Combine(link, "child") : link;
        string[] before = SnapshotTree(workspace.Path);

        StorageRelocationPreflightResult result = sourceRole
            ? Inspect(suspect, ordinary) : Inspect(ordinary, suspect);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        AssertFinding(result, sourceRole ? StorageRelocationFindingCode.SourceLinkComponent
            : StorageRelocationFindingCode.DestinationLinkComponent, link);
        Assert.DoesNotContain(result.Findings, finding => finding.Code is
            StorageRelocationFindingCode.SourceMissing or StorageRelocationFindingCode.DestinationParentMissing
            or StorageRelocationFindingCode.DestinationLeafMissing);
        AssertClosed(result);
        Assert.Equal(before, SnapshotTree(workspace.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingLinkAncestorPointsAtOtherRootButDoesNotBecomeAnAuthorizedAlias(bool sourceRole)
    {
        using TempDirectory workspace = new();
        string ordinary = Path.Combine(workspace.Path, "ordinary");
        string link = Path.Combine(workspace.Path, "alias");
        Directory.CreateDirectory(ordinary);
        File.WriteAllText(Path.Combine(ordinary, "keep.bin"), "synthetic source bytes");
        Directory.CreateSymbolicLink(link, ordinary);
        string suspect = Path.Combine(link, "not-created");
        string[] before = SnapshotTree(workspace.Path);

        StorageRelocationPreflightResult result = sourceRole
            ? Inspect(suspect, ordinary) : Inspect(ordinary, suspect);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        AssertFinding(result, sourceRole ? StorageRelocationFindingCode.SourceLinkComponent
            : StorageRelocationFindingCode.DestinationLinkComponent, link);
        AssertClosed(result);
        Assert.Equal(before, SnapshotTree(workspace.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedInspectionWithHeldLocksKeepsWholeSyntheticTreeAndWriteTimes(bool cancel)
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(workspace.Path, "source");
        string destination = Path.Combine(workspace.Path, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        foreach (string root in new[] { source, destination })
        {
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            foreach (string name in new[] { "settings.json", "session.json", "work.sqlite", "archive.sqlite",
                         ".meowchan-profile.protocol", ".meowchan-profile.lock", ".meowchan-store.lock",
                         ".ochan-store.lock", ".meowchan-manifest.json", ".ochan-manifest.json", "partial.part" })
                File.WriteAllText(Path.Combine(root, name), "synthetic:" + name);
            File.WriteAllBytes(Path.Combine(root, "nested", "media.bin"), new byte[] { 0, 255, 1, 0, 128 });
        }
        string[] before = SnapshotTree(workspace.Path);
        using CancellationTokenSource cancellation = new();
        if (cancel) cancellation.Cancel();
        using (FileStream profileLock = new(Path.Combine(source, ".meowchan-profile.lock"), FileMode.Open,
                   FileAccess.Read, FileShare.None))
        using (FileStream mediaLock = new(Path.Combine(destination, ".meowchan-store.lock"), FileMode.Open,
                   FileAccess.Read, FileShare.None))
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                StorageRelocationPreflightRequest request = new(source, destination);
                if (cancel)
                    Assert.ThrowsAny<OperationCanceledException>(() => StorageRelocationPreflight.Inspect(request, cancellation.Token));
                else
                    AssertClosed(StorageRelocationPreflight.Inspect(request, cancellation.Token));
            }
        }
        Assert.Equal(before, SnapshotTree(workspace.Path));
    }

    [Fact]
    public void PreCancellationWinsEvenOverInvalidPathWithoutReturningAnUnknownPlan()
    {
        using TempDirectory workspace = new();
        string[] before = SnapshotTree(workspace.Path);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        StorageRelocationPreflightRequest request = new("relative/../source", "bad\0destination");

        Assert.ThrowsAny<OperationCanceledException>(() => StorageRelocationPreflight.Inspect(request, cancellation.Token));

        Assert.Equal(before, SnapshotTree(workspace.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileAncestorInEitherRoleIsReportedAtTheFileNotAsAMissingChild(bool sourceRole)
    {
        using TempDirectory workspace = new();
        string ordinary = Path.Combine(workspace.Path, "ordinary");
        string file = Path.Combine(workspace.Path, "file-ancestor");
        Directory.CreateDirectory(ordinary);
        File.WriteAllBytes(file, new byte[] { 0, 1, 2, 255 });
        string suspect = Path.Combine(file, "missing", "root");
        string[] before = SnapshotTree(workspace.Path);

        StorageRelocationPreflightResult result = sourceRole
            ? Inspect(suspect, ordinary) : Inspect(ordinary, suspect);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        AssertFinding(result, sourceRole ? StorageRelocationFindingCode.SourceNotDirectory
            : StorageRelocationFindingCode.DestinationNotDirectory, file);
        Assert.DoesNotContain(result.Findings, finding => finding.Code is
            StorageRelocationFindingCode.SourceMissing or StorageRelocationFindingCode.DestinationParentMissing
            or StorageRelocationFindingCode.DestinationLeafMissing);
        AssertClosed(result);
        Assert.Equal(before, SnapshotTree(workspace.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelfReferentialLinkIsReportedWithoutResolvingItsTarget(bool sourceRole)
    {
        using TempDirectory workspace = new();
        string ordinary = Path.Combine(workspace.Path, "ordinary");
        string link = Path.Combine(workspace.Path, "loop");
        Directory.CreateDirectory(ordinary);
        Directory.CreateSymbolicLink(link, link);
        string[] before = SnapshotTree(workspace.Path);

        StorageRelocationPreflightResult result = sourceRole
            ? Inspect(link, ordinary) : Inspect(ordinary, link);

        Assert.Equal(StorageRelocationPathSafety.Unsafe, result.PathSafety);
        AssertFinding(result, sourceRole ? StorageRelocationFindingCode.SourceLinkComponent
            : StorageRelocationFindingCode.DestinationLinkComponent, link);
        AssertClosed(result);
        Assert.Equal(before, SnapshotTree(workspace.Path));
    }

    private static void AssertFinding(StorageRelocationPreflightResult result,
        StorageRelocationFindingCode code, string? path = null)
        => Assert.Contains(result.Findings, finding => finding.Code == code && (path == null || finding.Path == path));

    private static void AssertClosed(StorageRelocationPreflightResult result)
    {
        Assert.Equal(StorageRelocationPreflightDecision.Blocked, result.Decision);
        Assert.False(result.HasAllWritersProof);
        AssertFinding(result, StorageRelocationFindingCode.ActiveStateEvidenceUnknown);
        AssertFinding(result, StorageRelocationFindingCode.AllWritersProofUnavailable);
        AssertFinding(result, StorageRelocationFindingCode.PhysicalPathIdentityUnknown);
        AssertFinding(result, StorageRelocationFindingCode.ReferenceInventoryUnavailable);
        AssertFinding(result, StorageRelocationFindingCode.CapacityAndAccessUnknown);
    }

    // Snapshot only our synthetic fixture. Do not recurse into symlinks (including
    // dangling targets), and do not compare access times changed by our own reads.
    private static string[] SnapshotTree(string root)
    {
        List<string> entries = new();
        Visit(root);
        return entries.OrderBy(entry => entry, StringComparer.Ordinal).ToArray();

        void Visit(string path)
        {
            string relative = Path.GetRelativePath(root, path);
            string? target = new DirectoryInfo(path).LinkTarget;
            if (target != null)
            {
                entries.Add("L:" + relative + ":" + target);
                return;
            }
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                entries.Add("D:" + relative + ":" + Directory.GetLastWriteTimeUtc(path).Ticks);
                foreach (string child in Directory.GetFileSystemEntries(path)) Visit(child);
            }
            else
            {
                entries.Add("F:" + relative + ":" + File.GetLastWriteTimeUtc(path).Ticks
                    + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            }
        }
    }

    private static StorageRelocationPreflightResult Inspect(string source, string destination)
        => StorageRelocationPreflight.Inspect(new StorageRelocationPreflightRequest(source, destination));

    private static StorageRelocationPreflightResult Inspect(
        string source, string destination, StorageRelocationActiveStateEvidence evidence)
        => StorageRelocationPreflight.Inspect(new StorageRelocationPreflightRequest(source, destination, evidence));
}
