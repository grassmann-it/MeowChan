using MeowChan.Archive;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

public sealed class BooruClaimMapperTests
{
    [Fact]
    public void MapsAllProviderFieldsWithExplicitPresenceAndEnumConversions()
    {
        string md5 = Convert.ToBase64String(new byte[16]);
        DateTimeOffset created = new(2026, 11, 12, 8, 30, 0, TimeSpan.Zero);
        BooruPost source = new(
            new SourcePostId("danbooru", "42"),
            BooruFieldPresence.All,
            new Uri("https://danbooru.donmai.us/posts/42"),
            created,
            BooruRating.Questionable,
            "q",
            17,
            3,
            [
                new BooruTag("cat", BooruTagCategory.General),
                new BooruTag("artist_name", BooruTagCategory.Artist)
            ],
            [
                new BooruMediaVariant(BooruMediaRole.Original,
                    new Uri("https://cdn.test/original/42.png"), ".png", 1234, 640, 480, md5),
                new BooruMediaVariant(BooruMediaRole.Sample,
                    new Uri("https://cdn.test/sample/42.jpg"), ".jpg", 456, 320, 240),
                new BooruMediaVariant(BooruMediaRole.Preview,
                    new Uri("https://cdn.test/preview/42.jpg"))
            ],
            "https://source.example/42",
            IsDeleted: true,
            IsBanned: true);

        BooruPostClaim claim = BooruClaimMapper.Map(source);

        Assert.Equal("danbooru", claim.SourceId);
        Assert.Equal("42", claim.PostId);
        Assert.Equal(BooruPostClaimPresence.All, claim.Fields);
        Assert.Equal("https://danbooru.donmai.us/posts/42", claim.PostUrl);
        Assert.Equal(created, claim.CreatedAtUtc);
        Assert.Equal(BooruPostClaimRating.Questionable, claim.Rating);
        Assert.Equal("q", claim.RawRating);
        Assert.Equal(17, claim.Score);
        Assert.Equal(3, claim.FavoriteCount);
        Assert.Equal("https://source.example/42", claim.SourceClaim);
        Assert.True(claim.IsDeleted);
        Assert.True(claim.IsBanned);
        Assert.Equal(
            [BooruPostClaimTagCategory.General, BooruPostClaimTagCategory.Artist],
            claim.Tags.Select(tag => tag.Category));
        Assert.Equal(
            [BooruPostClaimMediaRole.Original, BooruPostClaimMediaRole.Sample, BooruPostClaimMediaRole.Preview],
            claim.Media.Select(media => media.Role));
        Assert.Equal(md5, claim.Media[0].ExpectedMd5);
    }

    [Fact]
    public void AbsentFieldsBecomeUnsetCoreValuesAndDoNotInventPrivateMetadata()
    {
        BooruPostClaim claim = BooruClaimMapper.Map(new BooruPost(
            new SourcePostId("danbooru", "7"), BooruFieldPresence.None));

        Assert.Equal(BooruPostClaimPresence.None, claim.Fields);
        Assert.Null(claim.PostUrl);
        Assert.Null(claim.CreatedAtUtc);
        Assert.Equal(BooruPostClaimRating.Unknown, claim.Rating);
        Assert.Null(claim.RawRating);
        Assert.Null(claim.Score);
        Assert.Null(claim.FavoriteCount);
        Assert.Empty(claim.Tags);
        Assert.Empty(claim.Media);
        Assert.Null(claim.SourceClaim);
        Assert.False(claim.IsDeleted);
        Assert.False(claim.IsBanned);
    }

    [Fact]
    public void UnknownPresenceAndEnumValuesAreRejectedInsteadOfBlindCast()
    {
        Assert.Throws<InvalidDataException>(() => BooruClaimMapper.Map(new BooruPost(
            new SourcePostId("danbooru", "1"), (BooruFieldPresence)(1 << 12))));
        Assert.Throws<InvalidDataException>(() => BooruClaimMapper.Map(new BooruPost(
            new SourcePostId("danbooru", "1"), BooruFieldPresence.Rating,
            Rating: (BooruRating)99)));
        Assert.Throws<InvalidDataException>(() => BooruClaimMapper.Map(new BooruPost(
            new SourcePostId("danbooru", "1"), BooruFieldPresence.Tags,
            Tags: [new BooruTag("tag", (BooruTagCategory)99)])));
        Assert.Throws<InvalidDataException>(() => BooruClaimMapper.Map(new BooruPost(
            new SourcePostId("danbooru", "1"), BooruFieldPresence.Media,
            Media: [new BooruMediaVariant((BooruMediaRole)99, null)])));
    }

    [Fact]
    public void PresenceMismatchAndForeignOrMismatchedUrlsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => BooruClaimMapper.Map(new BooruPost(
            new SourcePostId("danbooru", "1"), BooruFieldPresence.None,
            Tags: [])));
        Assert.Throws<InvalidDataException>(() => BooruClaimMapper.Map(new BooruPost(
            new SourcePostId("other", "1"), BooruFieldPresence.None)));
        Assert.Throws<InvalidDataException>(() => BooruClaimMapper.Map(new BooruPost(
            new SourcePostId("danbooru", "1"), BooruFieldPresence.None,
            PostUrl: new Uri("https://danbooru.donmai.us/posts/2"))));
    }
}
