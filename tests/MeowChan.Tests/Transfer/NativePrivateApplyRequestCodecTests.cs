using System;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using MeowChan.Persistence;
using MeowChan.Favorites;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativePrivateApplyRequestCodecTests
{
    private static string Hash(char c) => new(c,64);
    private static NativePrivateApplyRequest Request(bool selected=true,string note="incoming\n  exact Ω")
    {
        var id=new ResourceId("synthetic","b","100");
        var selection=new NativePrivateMetadataSelection(selected,false,selected);
        var payload=new NativePrivateMetadataProjection([id],selection,[],[],selected?[new PostBookmark(new(id,"101"),note)]:[]);
        var rows=new NativePrivateMetadataComparisonResult(selection,payload,[],[],selected?[new(new(id,"101"),note,NativePrivateMetadataDisposition.Conflict)]:[]);
        var missing=new NativePrivateApplyStoreImage(false,0,null,0,0,null);
        ImmutableArray<NativePrivateApplyStoreFence> fences=selected?[new(NativePrivateApplyStoreKind.ThreadFavorites,missing,missing,missing),new(NativePrivateApplyStoreKind.PostBookmarksAndNotes,missing,missing)]:[];
        return NativePrivateApplyRequest.Create(Guid.Parse("11111111-1111-1111-1111-111111111111"),rows,new(1024,Hash('a'),Hash('b'),100,Hash('c'),Guid.Parse("22222222-2222-2222-2222-222222222222"),Hash('d')),fences);
    }
    [Theory]
    [InlineData(true,"")]
    [InlineData(true,"incoming\n  exact Ω")]
    [InlineData(false,"")]
    public void ExactCanonicalRoundtripPreservesSelectionAndConflict(bool selected,string note)
    {
        var request=Request(selected,note);byte[] bytes=NativePrivateApplyRequestCodec.Serialize(request);
        var decoded=NativePrivateApplyRequestCodec.Deserialize(bytes);
        Assert.Equal(bytes,NativePrivateApplyRequestCodec.Serialize(decoded));
        Assert.Equal(request.Selection,decoded.Selection);Assert.Equal(request.ContentPlanSha256,decoded.ContentPlanSha256);Assert.Equal(request.RequestSha256,decoded.RequestSha256);
        if(selected){Assert.Equal(note,decoded.PostBookmarksAndNotes[0].IncomingNote);Assert.Equal(NativePrivateMetadataDisposition.Conflict,decoded.PostBookmarksAndNotes[0].Disposition);}
        string text=Encoding.UTF8.GetString(bytes);Assert.DoesNotContain("oldTargetNote",text);Assert.DoesNotContain("path",text);Assert.Contains("\"mediaFavorites\":null",text);
    }
    [Theory]
    [InlineData("schema")][InlineData("binding")][InlineData("payload")][InlineData("threadDispositions")][InlineData("mediaFavoriteEntries")][InlineData("contentPlanSha256")][InlineData("requestSha256")]
    public void MissingFieldsRejected(string name){var node=JsonNode.Parse(NativePrivateApplyRequestCodec.Serialize(Request()))!.AsObject();node.Remove(name);Reject(node.ToJsonString());}
    [Theory]
    [InlineData("schema", "3")][InlineData("bookmarkDispositions","[99]")][InlineData("threadDispositions","[0]")][InlineData("contentPlanSha256","\"bad\"")][InlineData("requestSha256","\"bad\"")]
    public void TamperedFieldsRejected(string field,string value){var node=JsonNode.Parse(NativePrivateApplyRequestCodec.Serialize(Request()))!;node[field]=JsonNode.Parse(value);Reject(node.ToJsonString());}
    [Fact]
    public void WrapperDuplicatesAndUnknownFieldsRejected()
    {
        string text=Encoding.UTF8.GetString(NativePrivateApplyRequestCodec.Serialize(Request()));
        Reject(text.Replace("\"schema\":2,","\"schema\":2,\"schema\":2,"));
        Reject(text.Replace("\"binding\":{","\"binding\":{\"extra\":0,"));
        Reject(text.Replace("\"exists\":false,","\"exists\":false,\"exists\":false,"));
        Reject(text.Replace("\"kind\":","\"extra\":0,\"kind\":"));
        Reject(text.Replace("\"note\":","\"extra\":0,\"note\":"));
    }
    [Fact]
    public void StrictEncodingTrailingGarbageAndBudget()
    {
        byte[] bytes=NativePrivateApplyRequestCodec.Serialize(Request());
        Assert.Throws<InvalidDataException>(()=>NativePrivateApplyRequestCodec.Deserialize(new byte[]{0xff}));
        byte[] bom=new byte[bytes.Length+3];bom[0]=0xef;bom[1]=0xbb;bom[2]=0xbf;bytes.CopyTo(bom,3);
        Assert.Throws<InvalidDataException>(()=>NativePrivateApplyRequestCodec.Deserialize(bom));
        Reject(Encoding.UTF8.GetString(bytes)+"{}");
        Assert.Throws<InvalidDataException>(()=>NativePrivateApplyRequestCodec.Deserialize(new byte[NativePrivateApplyRequestCodec.MaximumDocumentBytes+1]));
    }
    [Fact]
    public void AllIdentityArraysRoundtripAndReorderedPayloadIsRejected()
    {
        var a=new ResourceId("synthetic","b","100");var b=a with { Thread="200" };
        var media=new MeowChan.Favorites.FavoriteAssetKey(a,"image","original",Hash('e'));
        var selection=new NativePrivateMetadataSelection(true,true,false);
        var payload=new NativePrivateMetadataProjection([a,b],selection,[a,b],[media],[]);
        var rows=new NativePrivateMetadataComparisonResult(selection,payload,[new(a,NativePrivateMetadataDisposition.Identical),new(b,NativePrivateMetadataDisposition.MissingTarget)],[new(media,NativePrivateMetadataDisposition.Add)],[]);
        var missing=new NativePrivateApplyStoreImage(false,0,null,0,0,null);
        var request=NativePrivateApplyRequest.Create(Guid.NewGuid(),rows,new(1024,Hash('a'),Hash('b'),100,Hash('c'),Guid.NewGuid(),Hash('d')),[new(NativePrivateApplyStoreKind.ThreadFavorites,missing,missing,missing),new(NativePrivateApplyStoreKind.MediaFavorites,missing,missing,missing)]);
        byte[] bytes=NativePrivateApplyRequestCodec.Serialize(request);
        Assert.Equal(bytes,NativePrivateApplyRequestCodec.Serialize(NativePrivateApplyRequestCodec.Deserialize(bytes)));
        var node=JsonNode.Parse(bytes)!;var array=node["payload"]!["threads"]!.AsArray();
        var first=array[0]!.DeepClone();array[0]=array[1]!.DeepClone();array[1]=first;Reject(node.ToJsonString());
    }
    [Fact]
    public void MediaFavoriteLengthAndOriginalNameRoundtripAndBindTheRequestDigest()
    {
        var id = new ResourceId("synthetic", "b", "100");
        var key = new FavoriteAssetKey(id, "image", "original", Hash('e'));
        var entry = new FavoriteEntry(key, 123456789, "Original Ä image.webm");
        var selection = new NativePrivateMetadataSelection(MediaFavorites: true);
        var payload = new NativePrivateMetadataProjection([id], selection, [], [key], [], [entry]);
        var rows = new NativePrivateMetadataComparisonResult(selection, payload, [],
            [new(key, NativePrivateMetadataDisposition.Add, entry)], []);
        var missing = new NativePrivateApplyStoreImage(false, 0, null, 0, 0, null);
        var request = NativePrivateApplyRequest.Create(Guid.NewGuid(), rows,
            new(1024, Hash('a'), Hash('b'), 100, Hash('c'), Guid.NewGuid(), Hash('d')),
            [new(NativePrivateApplyStoreKind.MediaFavorites, missing, missing, missing)]);

        byte[] bytes = NativePrivateApplyRequestCodec.Serialize(request);
        var decoded = NativePrivateApplyRequestCodec.Deserialize(bytes);
        Assert.Equal(entry, Assert.Single(decoded.MediaFavoriteEntries));
        Assert.Equal(entry, Assert.Single(decoded.MediaFavorites).IncomingEntry);
        Assert.Contains("\"length\":123456789", Encoding.UTF8.GetString(bytes));
        JsonObject document = JsonNode.Parse(bytes)!.AsObject();
        Assert.Equal("Original Ä image.webm",
            document["mediaFavoriteEntries"]![0]!["originalFileName"]!.GetValue<string>());

        var changed = new NativePrivateMetadataProjection([id], selection, [], [key], [],
            [entry with { Length = entry.Length + 1 }]);
        var changedRows = new NativePrivateMetadataComparisonResult(selection, changed, [],
            [new(key, NativePrivateMetadataDisposition.Add, changed.MediaFavoriteEntries[0])], []);
        var changedRequest = NativePrivateApplyRequest.Create(request.OperationId, changedRows,
            request.Binding, request.StoreFences);
        Assert.NotEqual(request.ContentPlanSha256, changedRequest.ContentPlanSha256);
        Assert.NotEqual(request.RequestSha256, changedRequest.RequestSha256);
    }

    [Fact]
    public void IdentityOnlyMediaProjectionNeverInventsLengthOrOriginalName()
    {
        var id = new ResourceId("synthetic", "b", "100");
        var key = new FavoriteAssetKey(id, "image", "original", Hash('e'));
        var selection = new NativePrivateMetadataSelection(MediaFavorites: true);
        var payload = new NativePrivateMetadataProjection([id], selection, [], [key], []);
        var rows = new NativePrivateMetadataComparisonResult(selection, payload, [],
            [new(key, NativePrivateMetadataDisposition.Add)], []);
        var missing = new NativePrivateApplyStoreImage(false, 0, null, 0, 0, null);
        var request = NativePrivateApplyRequest.Create(Guid.NewGuid(), rows,
            new(1024, Hash('a'), Hash('b'), 100, Hash('c'), Guid.NewGuid(), Hash('d')),
            [new(NativePrivateApplyStoreKind.MediaFavorites, missing, missing, missing)]);
        var decoded = NativePrivateApplyRequestCodec.Deserialize(NativePrivateApplyRequestCodec.Serialize(request));
        Assert.Empty(decoded.MediaFavoriteEntries);
        Assert.Null(Assert.Single(decoded.MediaFavorites).IncomingEntry);
        Assert.Contains("\"mediaFavoriteEntries\":null", Encoding.UTF8.GetString(
            NativePrivateApplyRequestCodec.Serialize(decoded)));
    }

    [Fact]
    public void CancellationPrecedesParsingAndSerialization()
    {var token=new CancellationToken(true);Assert.Throws<OperationCanceledException>(()=>NativePrivateApplyRequestCodec.Serialize(Request(),token));Assert.Throws<OperationCanceledException>(()=>NativePrivateApplyRequestCodec.Deserialize(ReadOnlyMemory<byte>.Empty,token));}
    private static void Reject(string text)=>Assert.Throws<InvalidDataException>(()=>NativePrivateApplyRequestCodec.Deserialize(Encoding.UTF8.GetBytes(text)));
}
