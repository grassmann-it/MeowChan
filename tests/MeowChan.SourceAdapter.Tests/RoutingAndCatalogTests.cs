using MeowChan.Interfaces;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using MeowChan.OfficialSources.FourChan;

namespace MeowChan.SourceAdapter.Tests;

public sealed class RoutingAndCatalogTests
{
    [Fact]
    public void ThreadOnlySourceHasPassiveBoardContainer()
    {
        PluginDescription basis = new FourChanPlugin().Describe();
        SourceDescription source = basis.Sources[0] with { Capabilities = SourceCapabilities.ThreadRead,
            Routes = basis.Sources[0].Routes.Where(route => route.Kind == SourceResourceKind.Thread).ToArray() };
        using PluginSourceImageBoard adapter = new(basis with { Sources = new[] { source } }, "4chan", _ => throw new Exception("No IPC"));
        IThread thread = adapter.GetThread(new Uri("https://boards.4chan.org/g/thread/123"));
        Assert.Equal("123", thread.ThreadId);
        Assert.Equal("g", thread.Board.BoardCode);
    }

    [Theory]
    [InlineData("http://source.test:8080/b/thread/001/", true)]
    [InlineData("http://source.test:8080/b/thread/001", false)]
    [InlineData("http://source.test:8081/b/thread/001/", false)]
    public void RequiredSlashAndCanonicalSourceAuthorityArePreserved(string input, bool valid)
    {
        SourceDescription source = new FourChanPlugin().Describe().Sources[0] with
        {
            BaseUri = new Uri("http://source.test:8080/"),
            Routes = new[] { new UrlRoute(SourceResourceKind.Thread, "source.test", "/{board}/%74hread/{thread}/", true, AllowTrailingSlash: false)
                { BoardIdentifierKind = RouteIdentifierKind.AsciiWord, ThreadIdentifierKind = RouteIdentifierKind.Decimal } }
        };
        PluginSourceRouter router = new(source);
        Assert.Equal(valid, router.TryResolve(new Uri(input), out _, out _, out _, out Uri? canonical));
        if (valid) Assert.Equal("http://source.test:8080/b/thread/001/", canonical!.AbsoluteUri);
    }

    [Theory]
    [InlineData("file://boards.4chan.org/g/thread/123", false)]
    [InlineData("content://boards.4chan.org/g/thread/123", false)]
    [InlineData("ftp://boards.4chan.org/g/thread/123", false)]
    [InlineData("https://user:pass@boards.4chan.org/g/thread/123", false)]
    [InlineData("https://boards.4chan.org/g%2Fbad/thread/123", false)]
    [InlineData("https://boards.4chan.org/caf%C3%A9/thread/123", false)]
    [InlineData("https://boards.4chan.org/g/thread/%2531", false)]
    [InlineData("https://boards.4chan.org/g/thread/%D9%A1%D9%A2%D9%A3", false)]
    [InlineData("https://boards.4chan.org/%67/thread/%31%32%33", true)]
    [InlineData("https://boards.4channel.org/g/thread/123/title%20%C3%BC#p456", true)]
    public void SourceRoutingDistinguishesFilesystemUrisAndDecodesIdentitiesOnce(string input, bool accepted)
    {
        PluginDescription description = new FourChanPlugin().Describe();
        int acquisitions = 0;
        using PluginSourceImageBoard adapter = new(description, "4chan", _ =>
        {
            acquisitions++;
            throw new InvalidOperationException("Routing must not acquire a worker.");
        });
        var uri = new Uri(input, UriKind.Absolute);
        Assert.Equal(accepted, adapter.CanHandle(uri));
        if (accepted)
        {
            IThread thread = adapter.GetThread(uri);
            Assert.Equal("g", thread.Board.BoardCode);
            Assert.Equal("123", thread.ThreadId);
            Assert.Equal("https://boards.4chan.org/g/thread/123", thread.ThreadUri.AbsoluteUri);
        }
        else Assert.Throws<ArgumentException>(() => adapter.GetThread(uri));
        Assert.Equal(0, acquisitions);
    }

    [Fact]
    public async Task CatalogConsumesBoundedContinuationWithoutHoldingAWorkerBetweenPages()
    {
        PluginDescription description = new FourChanPlugin().Describe();
        List<int?> requested = new();
        SourceAdapterTests.TestWorker worker = new((_, _, _) => throw new NotSupportedException(), description,
            (request, _, _) =>
            {
                requested.Add(request.Page);
                string id = request.Page == null ? "1" : "2";
                return Task.FromResult<CatalogReadResult>(new CatalogSnapshot(new SourceCatalogSnapshot(request.Board,
                    new[] { new SourceThreadReference(new SourceThreadId("4chan", "g", id), new Uri("https://boards.4chan.org/g/thread/" + id)) },
                    request.Page != null, NextPage: request.Page == null ? 2 : null)));
            });
        using PluginSourceImageBoard adapter = new(description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker));
        IBoard board = adapter.GetBoard(new Uri("https://boards.4chan.org/g/"));
        IThread[] threads = (await board.GetThreadsAsync()).ToArray();
        Assert.Equal(new int?[] { null, 2 }, requested);
        Assert.Equal(new[] { "1", "2" }, threads.Select(thread => thread.ThreadId));
    }

    [Fact]
    public async Task PaginationLoopFailsWithoutPublishingPartialCatalog()
    {
        PluginDescription description = new FourChanPlugin().Describe();
        SourceAdapterTests.TestWorker worker = new((_, _, _) => throw new NotSupportedException(), description,
            (request, _, _) => Task.FromResult<CatalogReadResult>(new CatalogSnapshot(new SourceCatalogSnapshot(request.Board,
                Array.Empty<SourceThreadReference>(), false, NextCursor: "same"))));
        using PluginSourceImageBoard adapter = new(description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker));
        PluginWorkerException failure = await Assert.ThrowsAsync<PluginWorkerException>(() => adapter.GetBoard(new Uri("https://boards.4chan.org/g/")).GetThreadsAsync());
        Assert.Equal("catalog_pagination_loop", failure.Code);
    }
}
