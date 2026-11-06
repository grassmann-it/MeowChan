using MeowChan;
using MeowChan.Archive;

namespace MeowChan.DesktopTests;

// Pure existing session seam only: no Window, Root wiring, database or native input.
public sealed class FilenameSearchSelectionContractTests
{
    private const string Literal = "  %_ ' [claim] e\u0301 😀  ";

    private static ArchiveFilenameHit Claim() => new(
        new ResourceId("synthetic-source", "board-exact", "00101"), "00102", 3,
        "attachment-revision-exact", "preview", "  original claim 😀.png  ", true, false, 47);

    private static void AssertClaim(ArchiveFilenameHit hit)
    {
        Assert.Equal(new ResourceId("synthetic-source", "board-exact", "00101"), hit.Thread);
        Assert.Equal("00102", hit.Post);
        Assert.Equal(3, hit.Position);
        Assert.Equal("attachment-revision-exact", hit.AttachmentRevisionKey);
        Assert.Equal("preview", hit.Variant);
        Assert.Equal("  original claim 😀.png  ", hit.OriginalName);
        Assert.True(hit.PostIsDeleted);
        Assert.False(hit.AttachmentIsDeleted);
        Assert.Equal(47L, hit.CatalogThreadRevision);
    }

    [Fact]
    public async Task ReadySelectionKeepsEveryIdentityFieldAndExactLiteralWithoutReconstruction()
    {
        var hit = Claim();
        string? received = null;
        var session = new FilenameSearchSession((q, offset, limit, token) =>
        {
            received = q.Text;
            return Task.FromResult(new ArchiveFilenamePage(new[] { hit }, 1, offset));
        });
        try
        {
            session.Edit(Literal);
            await session.SearchAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Literal, received);
            Assert.Equal(Literal, session.Current.Text);
            ArchiveFilenameHit selected = Assert.Single(session.Current.Rows);
            Assert.Same(hit, selected);
            AssertClaim(selected);
            Assert.True(session.CanReturn(selected));
            Assert.False(session.CanReturn(null));
            // Neither value equality nor a partial/reconstructed identity authorizes return.
            foreach (var reconstructed in new[]
            {
                hit with { },
                hit with { Thread = new ResourceId("other-source", "board-exact", "00101") },
                hit with { Thread = new ResourceId("synthetic-source", "other-board", "00101") },
                hit with { Thread = new ResourceId("synthetic-source", "board-exact", "101") },
                hit with { Post = "102" }, hit with { Position = 4 },
                hit with { AttachmentRevisionKey = null }, hit with { Variant = "original" },
                hit with { OriginalName = "normalized.png" }, hit with { CatalogThreadRevision = 48 },
                hit with { PostIsDeleted = false }, hit with { AttachmentIsDeleted = true }
            }) Assert.False(session.CanReturn(reconstructed));
            AssertClaim(hit);
            Assert.Equal(Literal, session.Current.Text);
        }
        finally { await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrClosePreservesLiteralAndCapturedClaimButRevokesReturn(bool close)
    {
        var hit = Claim();
        int calls = 0;
        var session = new FilenameSearchSession((q, offset, limit, token) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal(Literal, q.Text);
            return Task.FromResult(new ArchiveFilenamePage(new[] { hit }, 1, offset));
        });
        try
        {
            session.Edit(Literal);
            await session.SearchAsync().WaitAsync(TimeSpan.FromSeconds(5));
            ArchiveFilenameHit captured = Assert.Single(session.Current.Rows);
            Assert.True(session.CanReturn(captured));
            if (close) await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
            else session.CancelSearch();
            Assert.Equal(Literal, session.Current.Text);
            Assert.Equal("filename.search.canceled", session.Current.StatusKey);
            Assert.Equal(close, session.Current.Closed);
            Assert.Empty(session.Current.Rows);
            Assert.False(session.CanReturn(captured));
            Assert.False(session.CanReturn(null));
            Assert.Equal(1, calls);
            Assert.Same(hit, captured);
            AssertClaim(captured); // immutable claim survives, not a live selection capability
        }
        finally { await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidLiteralIsRetainedExactlyWithoutHandoffOrRevivingOldIdentity(bool nul)
    {
        var hit = Claim();
        int calls = 0;
        var session = new FilenameSearchSession((q, offset, limit, token) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal(Literal, q.Text);
            return Task.FromResult(new ArchiveFilenamePage(new[] { hit }, 1, offset));
        });
        try
        {
            session.Edit(Literal);
            await session.SearchAsync().WaitAsync(TimeSpan.FromSeconds(5));
            ArchiveFilenameHit captured = Assert.Single(session.Current.Rows);
            string invalid = nul ? Literal + "\0 unchanged " : new string('a', 511) + "😀";
            session.Edit(invalid);
            await session.SearchAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(invalid, session.Current.Text);
            Assert.Equal("filename.search.invalid", session.Current.StatusKey);
            Assert.Empty(session.Current.Rows);
            Assert.False(session.CanReturn(captured));
            Assert.Equal(1, calls);
            AssertClaim(captured);
            session.CancelSearch();
            Assert.Equal(invalid, session.Current.Text);
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(invalid, session.Current.Text);
            Assert.False(session.CanReturn(captured));
        }
        finally { await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
