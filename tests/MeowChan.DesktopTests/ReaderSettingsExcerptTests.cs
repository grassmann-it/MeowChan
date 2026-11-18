using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderSettingsExcerptTests
{
    private static readonly ResourceId Id = new("4chan", "g", "700");
    private static readonly DateTimeOffset OpAt = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData((int)ReaderDateStyle.System)]
    [InlineData((int)ReaderDateStyle.Long)]
    [InlineData((int)ReaderDateStyle.SinceOp)]
    [InlineData((int)ReaderDateStyle.RelativeToNow)]
    public void ExcerptUsesSelectedStyleAndAFullDateRemainsIndependent(int value)
    {
        ReaderDateStyle style = (ReaderDateStyle)value;
        CapturedPost reply = new("701", 1, PostFieldPresence.All, PostedAt: OpAt.AddHours(2), Html: "<p>reply</p>");
        ArchivedThreadView view = View(new("700", 0, PostFieldPresence.All, PostedAt: OpAt), reply);
        DateTimeOffset now = OpAt.AddDays(3);
        DateTimeOffset? op = ArchivePage.ExcerptOpeningPostAt(view, Id);
        Assert.Equal(OpAt, op);
        var excerpt = ArchivePresentation.PostExcerpt(reply, 5, 100, style, op, now);
        Assert.EndsWith(ReaderDateText.Posted(reply.PostedAt, style, OpAt, now), excerpt.Header);
        Assert.Equal("reply", excerpt.Body);
        Assert.Equal(reply.PostedAt!.Value.ToLocalTime().ToString("F", Texts.Culture), ReaderDateText.PostedFull(reply.PostedAt));
        // The same fixed now is deterministic: no timer or wall-clock refresh is required.
        Assert.Equal(excerpt, ArchivePresentation.PostExcerpt(reply, 5, 100, style, op, now));
    }

    [Fact]
    public void ForeignMissingOrUndatedOpUsesSystemFallbackNotAnUnrelatedFirstPost()
    {
        CapturedPost reply = new("701", 1, PostFieldPresence.All, PostedAt: OpAt.AddHours(2));
        ArchivedThreadView view = View(new("700", 0, PostFieldPresence.All, PostedAt: OpAt), reply);
        DateTimeOffset?[] unknown =
        [
            ArchivePage.ExcerptOpeningPostAt(view, new ResourceId("4chan", "g", "999")),
            ArchivePage.ExcerptOpeningPostAt(view, null),
            ArchivePage.ExcerptOpeningPostAt(null, Id),
            ArchivePage.ExcerptOpeningPostAt(View(reply), Id),
            ArchivePage.ExcerptOpeningPostAt(View(new("700", 0, PostFieldPresence.All), reply), Id)
        ];
        foreach (DateTimeOffset? op in unknown)
        {
            Assert.Null(op);
            Assert.EndsWith(ReaderDateText.Posted(reply.PostedAt, ReaderDateStyle.System),
                ArchivePresentation.PostExcerpt(reply, 5, 100, ReaderDateStyle.SinceOp, op).Header);
        }
    }

    private static ArchivedThreadView View(params CapturedPost[] posts) => new(
        new ArchiveThreadSummary(Id, "https://offline.invalid/700", "fixture", "", ThreadSourceState.Archived,
            true, OpAt, OpAt, posts.Length, 1), posts);
}
