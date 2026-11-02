using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Synthetic seam tests: real P06 bytes bind to the P05 request without a circular
// marker hash. These are not an active private import planner or consent proof.
public sealed class NativePrivateApplyStoreBindingTests
{
    private static readonly Guid Operation = Guid.Parse("390fa408-de3c-47af-8f7b-6b0a973f7c7e");
    private static readonly Guid Profile = Guid.Parse("9e76962b-475e-4291-b1ba-d52f37aa3b38");
    private static readonly ResourceId Thread = new("synthetic", "b", "100");
    private const string OldNote = "SYNTHETIC-TARGET-NOTE-MUST-NOT-BE-JOURNALED";
    private const string OldName = "SYNTHETIC-TARGET-FILENAME-MUST-NOT-BE-JOURNALED.jpg";
    private static string Hash(char value) => new(value, 64);

    [Theory]
    [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)]
    [InlineData(4, false)] [InlineData(5, false)] [InlineData(6, false)] [InlineData(7, false)]
    [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)]
    [InlineData(4, true)] [InlineData(5, true)] [InlineData(6, true)] [InlineData(7, true)]
    public void RealBatchAfterBytesBindToRequestWithoutCircularHashOrTargetData(int bits, bool existing)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var threads = new ThreadFavoriteStore(temp.Path, lease);
        var media = new FavoriteStore(temp.Path, lease);
        if (existing)
        {
            threads.Set(Thread with { Thread = "old" }, true);
            media.SetFavorite(new(new(Thread, "old", "original", Hash('e')), 17, OldName), true);
            File.WriteAllBytes(Path.Combine(temp.Path, "post-bookmarks.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, revision = 7,
                bookmarks = new[] { new { source = Thread.Source, board = Thread.Board,
                    thread = Thread.Thread, post = "old", note = OldNote } }
            }));
        }
        var notes = new PostBookmarkStore(temp.Path, lease);
        var selection = new NativePrivateMetadataSelection((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);
        FavoriteEntry incomingMedia = new(new(Thread, "new", "original", Hash('f')), 23, "synthetic-incoming.jpg");
        PostBookmark incomingNote = new(new(Thread, "101"), "selected incoming\n  exact Ω");
        var selected = new NativePrivateMetadataProjection([Thread], selection,
            selection.ThreadFavorites ? [Thread] : [], selection.MediaFavorites ? [incomingMedia.Key] : [],
            selection.PostBookmarksAndNotes ? [incomingNote] : []);
        var comparison = new NativePrivateMetadataComparisonResult(selection, selected,
            selection.ThreadFavorites ? [new(Thread, NativePrivateMetadataDisposition.Add)] : [],
            selection.MediaFavorites ? [new(incomingMedia.Key, NativePrivateMetadataDisposition.Add)] : [],
            selection.PostBookmarksAndNotes ? [new(incomingNote.Key, incomingNote.Note, NativePrivateMetadataDisposition.Add)] : []);
        var binding = new NativePrivateApplyRequestBinding(1024, Hash('1'), Hash('2'), 256, Hash('3'), Profile, Hash('4'));
        List<PrivateStoreBatch> Prepare(string plan)
        {
            List<PrivateStoreBatch> result = [];
            if (selection.ThreadFavorites) result.Add(threads.PrepareAdditiveBatch(Operation, plan, [Thread]));
            if (selection.MediaFavorites) result.Add(media.PrepareAdditiveBatch(Operation, plan, [incomingMedia]));
            if (selection.PostBookmarksAndNotes) result.Add(notes.PrepareAdditiveBatch(Operation, plan, [incomingNote]));
            return result;
        }
        // The first transient materialization samples the exact Before/Backup
        // fences. ContentPlan deliberately excludes the marker-bearing After bytes.
        var provisional = Prepare(Hash('a'));
        var seed = NativePrivateApplyRequest.Create(Operation, comparison, binding,
            provisional.Select(x => x.Fence).ToImmutableArray());
        var real = Prepare(seed.ContentPlanSha256);
        var request = NativePrivateApplyRequest.Create(Operation, comparison, binding,
            real.Select(x => x.Fence).ToImmutableArray());
        Assert.Equal(seed.ContentPlanSha256, request.ContentPlanSha256);
        Assert.NotEqual(seed.RequestSha256, request.RequestSha256);
        byte[] encoded = NativePrivateApplyRequestCodec.Serialize(request);
        var decoded = NativePrivateApplyRequestCodec.Deserialize(encoded);
        Assert.Equal(request.ContentPlanSha256, decoded.ContentPlanSha256);
        Assert.Equal(request.RequestSha256, decoded.RequestSha256);
        string json = Encoding.UTF8.GetString(encoded);
        Assert.DoesNotContain(OldNote, json);
        Assert.DoesNotContain(OldName, json);
        Assert.DoesNotContain(temp.Path, json);
        foreach (var batch in real)
        {
            Assert.True(batch.HasChanges);
            Assert.Equal(request.ContentPlanSha256, batch.Marker.ContentPlanSha256);
            Assert.Equal(Operation, batch.Marker.OperationId);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(batch.CopyAfterBytes())).ToLowerInvariant(),
                decoded.StoreFences.Single(x => x.Kind == batch.Fence.Kind).ExpectedAfter.Sha256);
            PrivateStoreBatchInspection result = batch.Fence.Kind switch
            {
                NativePrivateApplyStoreKind.ThreadFavorites => threads.CommitPreparedBatch(batch),
                NativePrivateApplyStoreKind.MediaFavorites => media.CommitPreparedBatch(batch),
                _ => notes.CommitPreparedBatch(batch)
            };
            Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, result);
        }
        // Only the selected stores were published. Old target notes never enter
        // the journal request, even though the transient After image retains them.
        if (existing && selection.PostBookmarksAndNotes)
            Assert.Equal(OldNote, notes.Read().Bookmarks.Single(x => x.Key.Post == "old").Note);
    }
}
