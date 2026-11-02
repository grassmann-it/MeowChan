using System.Security.Cryptography;
using System.Text;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

/// <summary>Boundary-only P02 coverage. Synthetic data; no build or store hooks.</summary>
public sealed class NativePrivateMetadataBoundaryTests
{
    private static readonly ResourceId Thread = new("synthetic", "b", "1");
    private const string Sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void AcceptsMaximumThreadAndThreadFavoriteCounts()
    {
        string[] ids = Enumerable.Range(0, 1_000)
            .Select(i => $"{{\"source\":\"synthetic\",\"board\":\"b\",\"thread\":\"{i}\"}}")
            .ToArray();
        string json = Document($"[{string.Join(',', ids)}]", $"[{string.Join(',', ids)}]", "null", "null");

        NativePrivateMetadataInspection result = NativePrivateMetadataCodec.Inspect(Utf8(json));
        Assert.True(result.IsValid, result.Error.ToString());
        Assert.Equal(1_000, result.Projection!.Threads.Length);
        Assert.Equal(1_000, result.Projection.ThreadFavorites.Length);
        NativePrivateMetadataInspection roundtrip = NativePrivateMetadataCodec.Inspect(
            NativePrivateMetadataCodec.Serialize(result.Projection!));
        Assert.True(roundtrip.IsValid, roundtrip.Error.ToString());
        Assert.Equal(1_000, roundtrip.Projection!.ThreadFavorites.Length);
    }

    [Fact]
    public void AcceptsMaximumMediaFavoriteCount()
    {
        string scope = Identity("1");
        string[] entries = Enumerable.Range(0, 50_000)
            .Select(i => $"{{\"thread\":{scope},\"mediaId\":\"m{i}\",\"variant\":\"original\",\"sha256\":\"{Sha256}\"}}")
            .ToArray();
        string json = Document($"[{scope}]", "null", $"[{string.Join(',', entries)}]", "null");

        NativePrivateMetadataInspection result = NativePrivateMetadataCodec.Inspect(Utf8(json));
        Assert.True(result.IsValid, result.Error.ToString());
        Assert.Equal(50_000, result.Projection!.MediaFavorites.Length);
        NativePrivateMetadataInspection roundtrip = NativePrivateMetadataCodec.Inspect(
            NativePrivateMetadataCodec.Serialize(result.Projection!));
        Assert.True(roundtrip.IsValid, roundtrip.Error.ToString());
        Assert.Equal(50_000, roundtrip.Projection!.MediaFavorites.Length);
    }

    [Fact]
    public void AcceptsMaximumBookmarkCount()
    {
        string scope = Identity("1");
        string[] entries = Enumerable.Range(0, 10_000)
            .Select(i => $"{{\"thread\":{scope},\"post\":\"p{i}\",\"note\":\"\"}}")
            .ToArray();
        string json = Document($"[{scope}]", "null", "null", $"[{string.Join(',', entries)}]");

        NativePrivateMetadataInspection result = NativePrivateMetadataCodec.Inspect(Utf8(json));
        Assert.True(result.IsValid, result.Error.ToString());
        Assert.Equal(10_000, result.Projection!.PostBookmarksAndNotes.Length);
        NativePrivateMetadataInspection roundtrip = NativePrivateMetadataCodec.Inspect(
            NativePrivateMetadataCodec.Serialize(result.Projection!));
        Assert.True(roundtrip.IsValid, roundtrip.Error.ToString());
        Assert.Equal(10_000, roundtrip.Projection!.PostBookmarksAndNotes.Length);
    }

    [Fact]
    public void AcceptsMaximumIdentifierCodeUnitsAcrossIdentityAndPrivateKeys()
    {
        string x = new('x', 256);
        ResourceId id = new(x, x, x);
        NativePrivateMetadataProjection projection = new(
            [id], new(true, true, true), [id],
            [new FavoriteAssetKey(id, x, x, Sha256)],
            [new PostBookmark(new(id, x), "")]);

        NativePrivateMetadataInspection result = NativePrivateMetadataCodec.Inspect(
            NativePrivateMetadataCodec.Serialize(projection));
        Assert.True(result.IsValid, result.Error.ToString());
        Assert.Equal(projection.Selection, result.Projection!.Selection);
        Assert.Equal(projection.Threads.ToArray(), result.Projection.Threads.ToArray());
        Assert.Equal(projection.ThreadFavorites.ToArray(), result.Projection.ThreadFavorites.ToArray());
        Assert.Equal(projection.MediaFavorites.ToArray(), result.Projection.MediaFavorites.ToArray());
        Assert.Equal(projection.PostBookmarksAndNotes.ToArray(), result.Projection.PostBookmarksAndNotes.ToArray());
    }

    [Fact]
    public void AcceptsExactDocumentByteCapButRejectsOneByteMore()
    {
        byte[] valid = NativePrivateMetadataCodec.Serialize(
            new NativePrivateMetadataProjection([Thread], default, [], [], []));
        byte[] exact = PadWithWhitespace(valid, 16 * 1024 * 1024);
        byte[] over = PadWithWhitespace(valid, 16 * 1024 * 1024 + 1);

        NativePrivateMetadataInspection accepted = NativePrivateMetadataCodec.Inspect(exact);
        Assert.True(accepted.IsValid, accepted.Error.ToString());
        NativePrivateMetadataInspection rejected = NativePrivateMetadataCodec.Inspect(over);
        Assert.False(rejected.IsValid);
        Assert.Equal(NativePrivateMetadataCodecError.BudgetExceeded, rejected.Error);
    }

    [Fact]
    public void RejectsDefaultImmutableArraysDuringSerializeAndSelect()
    {
        NativePrivateMetadataProjection[] invalid =
        [
            new(default, default, [], [], []),
            new([Thread], default, default, [], []),
            new([Thread], default, [], default, []),
            new([Thread], default, [], [], default)
        ];

        foreach (NativePrivateMetadataProjection projection in invalid)
        {
            Assert.Throws<InvalidDataException>(() => NativePrivateMetadataCodec.Serialize(projection));
            Assert.Throws<InvalidDataException>(() => projection.Select());
        }
    }

    [Fact]
    public void RejectsPopulatedUnselectedCategoriesDuringSerializeAndSelect()
    {
        NativePrivateMetadataProjection[] invalid =
        [
            new([Thread], default, [Thread], [], []),
            new([Thread], default, [], [new FavoriteAssetKey(Thread, "m", "original", Sha256)], []),
            new([Thread], default, [], [], [new PostBookmark(new(Thread, "p"), "")])
        ];

        foreach (NativePrivateMetadataProjection projection in invalid)
        {
            Assert.Throws<InvalidDataException>(() => NativePrivateMetadataCodec.Serialize(projection));
            Assert.Throws<InvalidDataException>(() => projection.Select());
        }
    }

    [Fact]
    public void RejectsNullPrivateValuesAndScopeWithOpaqueErrors()
    {
        PostBookmark nullBookmark = null!;
        PostBookmark nullKey = new(null!, "SYNTHETIC-PRIVATE-NOTE");
        PostBookmark nullNote = new(new(Thread, "SYNTHETIC-PRIVATE-POST"), null!);
        FavoriteAssetKey nullFavorite = null!;
        FavoriteAssetKey nullFavoriteThread = new(null!, "SYNTHETIC-PRIVATE-MEDIA", "SYNTHETIC-PRIVATE-VARIANT", Sha256);
        ResourceId nullScope = null!;

        NativePrivateMetadataProjection[] invalid =
        [
            new([Thread], new(PostBookmarksAndNotes: true), [], [], [nullBookmark]),
            new([Thread], new(PostBookmarksAndNotes: true), [], [], [nullKey]),
            new([Thread], new(PostBookmarksAndNotes: true), [], [], [nullNote]),
            new([Thread], new(MediaFavorites: true), [], [nullFavorite], []),
            new([Thread], new(MediaFavorites: true), [], [nullFavoriteThread], []),
            new([nullScope], default, [], [], [])
        ];

        foreach (NativePrivateMetadataProjection projection in invalid)
        {
            AssertClosed(() => NativePrivateMetadataCodec.Serialize(projection),
                "SYNTHETIC-PRIVATE-NOTE", "SYNTHETIC-PRIVATE-MEDIA", "SYNTHETIC-PRIVATE-VARIANT", "SYNTHETIC-PRIVATE-POST");
            AssertClosed(() => projection.Select(),
                "SYNTHETIC-PRIVATE-NOTE", "SYNTHETIC-PRIVATE-MEDIA", "SYNTHETIC-PRIVATE-VARIANT", "SYNTHETIC-PRIVATE-POST");
        }
    }

    [Fact]
    public void RejectsNullNestedValuesFromWireWithoutLeakingInputText()
    {
        const string secret = "synthetic-secret-value";
        string[] wires =
        [
            Document($"[{Identity("1")}]", "null", "null",
                $"[{{\"thread\":{Identity("1")},\"post\":\"post\",\"note\":null}}]"),
            Document($"[{Identity("1")}]", "null", "null",
                $"[{{\"thread\":null,\"post\":\"post\",\"note\":\"{secret}\"}}]"),
            Document($"[{Identity("1")}]", "null",
                $"[{{\"thread\":null,\"mediaId\":\"media\",\"variant\":\"original\",\"sha256\":\"{Sha256}\"}}]", "null")
        ];

        foreach (string wire in wires)
        {
            NativePrivateMetadataInspection result = NativePrivateMetadataCodec.Inspect(Utf8(wire));
            Assert.False(result.IsValid);
            Assert.Null(result.Projection);
            Assert.NotEqual(NativePrivateMetadataCodecError.None, result.Error);
            Assert.DoesNotContain(secret, result.Error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SerializeAndSelectFailuresHaveNoInnerExceptionOrRawValues()
    {
        const string secret = "synthetic-private-secret";
        NativePrivateMetadataProjection invalid = new(
            [Thread], new(PostBookmarksAndNotes: true), [], [],
            [new PostBookmark(new(Thread, secret), null!)]);

        AssertClosed(() => NativePrivateMetadataCodec.Serialize(invalid), secret);
        AssertClosed(() => invalid.Select(), secret);
    }

    [Fact]
    public void PreCancelledSelectAndFromSnapshotAreCancelledBeforeWork()
    {
        NativePrivateMetadataProjection projection = new([Thread], default, [], [], []);
        using CancellationTokenSource selectCancellation = new();
        selectCancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => projection.Select(default, selectCancellation.Token));

        using TempDirectory directory = new();
        using ProfileLease owner = ProfileLease.Acquire(directory.Path);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(owner, [Thread]);
        using CancellationTokenSource snapshotCancellation = new();
        snapshotCancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            NativePrivateMetadataProjection.FromSnapshot(snapshot, owner, snapshotCancellation.Token));
    }

    [Theory]
    [InlineData("chan-v1")]
    [InlineData("chan-v2")]
    [InlineData("chan-v3")]
    public void PublicCatalogCodecRejectsPrivateWireBytes(string publicSchema)
    {
        byte[] bytes = NativePrivateMetadataCodec.Serialize(
            new NativePrivateMetadataProjection([Thread], default, [], [], []));
        NativeTransferEnvelope envelope = new(
            "synthetic-package", publicSchema, "synthetic-fingerprint",
            new(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()), []);

        TransferCatalogInspection result = NativeTransferCatalogValidator.Inspect(bytes, envelope);
        Assert.False(result.IsCatalogValid);
        Assert.Null(result.Catalog);
    }

    private static void AssertClosed(Action action, params string[] forbidden)
    {
        // Select exposes shared validation's argument errors as well as data errors;
        // both fail closed. NullReferenceException (or any unexpected type) must fail.
        Exception? error = Record.Exception(action);
        Assert.NotNull(error);
        Assert.True(error is InvalidDataException or ArgumentException, error.GetType().Name);
        Assert.Null(error.InnerException);
        foreach (string value in forbidden)
            Assert.DoesNotContain(value, error.ToString(), StringComparison.Ordinal);
    }

    private static string Document(string threads, string threadFavorites, string mediaFavorites, string bookmarks) =>
        $"{{\"schema\":\"meowchan-private-v1\",\"threads\":{threads},\"threadFavorites\":{threadFavorites},\"mediaFavorites\":{mediaFavorites},\"postBookmarksAndNotes\":{bookmarks}}}";

    private static string Identity(string thread) =>
        $"{{\"source\":\"synthetic\",\"board\":\"b\",\"thread\":\"{thread}\"}}";

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[] PadWithWhitespace(byte[] value, int length)
    {
        Assert.True(value.Length < length);
        byte[] result = new byte[length];
        value.CopyTo(result, 0);
        Array.Fill(result, (byte)' ', value.Length, length - value.Length);
        return result;
    }
}
