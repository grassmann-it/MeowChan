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
