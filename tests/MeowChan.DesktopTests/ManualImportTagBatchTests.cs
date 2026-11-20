using MeowChan.Desktop;
using MeowChan.Import;
using MeowChan.Interfaces;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

/// <summary>C03: the tag phase of a manual import verifies each imported file once and writes the tag store once, for any number of tags.</summary>
public sealed class ManualImportTagBatchTests
{
    private static byte[] Png(byte seed) => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, seed, 7 };

    private static async Task<(DesktopSessionHost Host, ManualImportPlan Plan)> StartAsync(TempDirectory temp, int files, int? tagLimit = null)
    {
        string source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        for (int index = 0; index < files; index++) File.WriteAllBytes(Path.Combine(source, "f" + index + ".png"), Png((byte)(index + 1)));
        string profile = Path.Combine(temp.Path, "profile");
        Config config = new() { DownloadPath = Path.Combine(temp.Path, "downloads"), DownloadsPaused = true };
        if (tagLimit is { } limit) config.PrivateTagLimit = limit;
        config.SaveConfig(profile);
        DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        host.ManualImportFreeSpaceForTests = _ => null;
        ManualImportPlan plan = await host.PlanManualImportAsync(new ManualImportRequest(new[] { source }),
            _ => Task.FromResult(ManualImportKnownMedia.Empty), null, default);
        Assert.Equal(files, plan.Count(ManualImportDisposition.New));
        return (host, plan);
    }

    private static string[] Names(int count) => Enumerable.Range(0, count).Select(index => "tag" + index).ToArray();

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task VerificationCountDependsOnlyOnTheMediaSetNotOnTheNumberOfTags(int tagCount)
    {
        using TempDirectory temp = new();
        (DesktopSessionHost host, ManualImportPlan plan) = await StartAsync(temp, files: 3);
        await using DesktopSessionHost _ = host;
        int calls = 0, verified = 0;
        host.LocalMediaChecksForTests = (observations, _) =>
        {
            Interlocked.Increment(ref calls);
            Interlocked.Add(ref verified, observations.Count);
            return Task.FromResult<IReadOnlyList<LocalMediaCheck>>(observations.Select(_ => LocalMediaCheck.Verified).ToArray());
        };
        long revisionBefore = host.GetPrivateTagSnapshot().Snapshot!.Revision;

        ManualImportOutcome outcome = await host.RunManualImportAsync(plan, new ManualImportOptions("B", Names(tagCount)), null, default);

        Assert.True(outcome.TagsApplied, outcome.TagError?.ToString());
        Assert.Equal(1, calls);
        Assert.Equal(3, verified);
        PrivateTagStoreSnapshot snapshot = host.GetPrivateTagSnapshot().Snapshot!;
        Assert.Equal(tagCount, snapshot.Tags.Count);
        Assert.Equal(3 * tagCount, snapshot.LocalAssignments.Count);
        // ONE store write for the whole tag phase, however many tags.
        Assert.Equal(revisionBefore + 1, snapshot.Revision);
    }

    [Fact]
    public async Task TagLimitRejectsTheWholeBatchAtomicallyAndKeepsTheCopiedFiles()
    {
        using TempDirectory temp = new();
        (DesktopSessionHost host, ManualImportPlan plan) = await StartAsync(temp, files: 2, tagLimit: PrivateTagLimits.MinimumTagLimit);
        await using DesktopSessionHost _ = host;
        host.LocalMediaChecksForTests = (observations, _) =>
            Task.FromResult<IReadOnlyList<LocalMediaCheck>>(observations.Select(_ => LocalMediaCheck.Verified).ToArray());
        // Leave exactly one free slot: two new tags cannot both fit.
        for (int index = 0; index < PrivateTagLimits.MinimumTagLimit - 1; index++) await host.DefinePrivateTagAsync("old" + index);
        PrivateTagStoreSnapshot before = host.GetPrivateTagSnapshot().Snapshot!;

        ManualImportOutcome outcome = await host.RunManualImportAsync(plan, new ManualImportOptions("B", new[] { "new1", "new2" }), null, default);

        Assert.True(outcome.TagsRequested);
        Assert.False(outcome.TagsApplied);
        Assert.Equal(PrivateTagStoreError.TagLimitReached, outcome.TagError);
        PrivateTagStoreSnapshot after = host.GetPrivateTagSnapshot().Snapshot!;
        Assert.Equal(before.Revision, after.Revision);               // nothing written
        Assert.Equal(before.Tags.Count, after.Tags.Count);           // no stray "new1"
        Assert.Empty(after.LocalAssignments);
        Assert.False(host.ReadManualImportCatalog()!.Batches.Single().TagsApplied);
        Assert.Equal(2, Directory.GetFiles(outcome.Batch.Directory).Count(path => !Path.GetFileName(path).StartsWith('.')));
    }

    [Fact]
    public async Task CancellationStopsAfterTheCurrentFileAndTagsExactlyTheCopiedFilesOnce()
    {
        using TempDirectory temp = new();
        (DesktopSessionHost host, ManualImportPlan plan) = await StartAsync(temp, files: 4);
        await using DesktopSessionHost _ = host;
        int verified = 0, calls = 0;
        host.LocalMediaChecksForTests = (observations, _) =>
        {
            calls++; verified += observations.Count;
            return Task.FromResult<IReadOnlyList<LocalMediaCheck>>(observations.Select(_ => LocalMediaCheck.Verified).ToArray());
        };
        using CancellationTokenSource cancel = new();
        Progress progress = new(report => { if (report.FilesDone >= 1) cancel.Cancel(); });

        ManualImportOutcome outcome = await host.RunManualImportAsync(plan, new ManualImportOptions("B", Names(3)), progress, cancel.Token);

        Assert.True(outcome.Batch.Cancelled);
        int copied = outcome.Batch.Items.Count(item => item.State == ManualImportItemState.Committed);
        Assert.InRange(copied, 1, 3);
        Assert.Equal(copied, Directory.GetFiles(outcome.Batch.Directory).Count(path => !Path.GetFileName(path).StartsWith('.')));
        Assert.True(outcome.TagsApplied, outcome.TagError?.ToString());
        Assert.Equal(1, calls);
        Assert.Equal(copied, verified);
        Assert.Equal(copied * 3, host.GetPrivateTagSnapshot().Snapshot!.LocalAssignments.Count);
    }

    private sealed class Progress(Action<ManualImportCopyProgress> report) : IProgress<ManualImportCopyProgress>
    {
        public void Report(ManualImportCopyProgress value) => report(value);
    }
}
