using System.Net;
using MeowChan.Admission;
using MeowChan.Boards.FourChan;
using MeowChan.Boards.EightKun;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class BoardAdmissionTests
{
    [Fact]
    public void LiteralIncludesAreAnyMatchExcludesWinAndIndividualChoicesOverride()
    {
        const string url = "https://stub.test/g/thread/123";
        BoardAdmissionPolicy policy = new([
            new(ThreadTextField.Title, ThreadTextAction.Include, "CAT"),
            new(ThreadTextField.OpeningPost, ThreadTextAction.Include, "a.b"),
            new(ThreadTextField.TitleAndOpeningPost, ThreadTextAction.Exclude, "spam")]);
        Assert.True(policy.Evaluate(new("123", url, "Cat pictures", "")).Included);
        Assert.True(policy.Evaluate(new("123", url, "", "literal a.b text")).Included);
        Assert.False(policy.Evaluate(new("123", url, "", "literal axb text")).Included);
        var spaced = new BoardAdmissionPolicy([new(ThreadTextField.Title, ThreadTextAction.Include, " cat ")]);
        Assert.True(spaced.Evaluate(new("123", url, "a cat photo", "")).Included);
        Assert.False(spaced.Evaluate(new("123", url, "cat", "")).Included);
        Assert.False(policy.Evaluate(new("123", url, "Cat pictures", "SPAM")).Included);
        policy = new(policy.Rules, new Dictionary<string, ThreadAdmissionException> { [url] = ThreadAdmissionException.Include });
        Assert.True(policy.Evaluate(new("123", url, "", "SPAM")).Included);
        Assert.False(policy.Evaluate(new("123", url, "", "", Suppressed: true)).Included);
        Assert.True(policy.Evaluate(new("123", url, "", "", Suppressed: true), honorSuppression: false).Included);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealCatalogPreviewUsesOnlyCatalogAndMetadataAndDecodesOpeningPost(bool eightKun)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        var requests = new List<string>();
        using HttpClient client = new(new DelegateHandler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath; requests.Add(path);
            string body = path == "/boards.json" ? eightKun ? "[]" : "{\"boards\":[]}" :
                "[{\"page\":1,\"threads\":[{\"no\":123,\"sub\":\"Cats &amp; dogs\",\"com\":\"Hello<br>world<script>bad()</script> &amp; friends\"}]}]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }));
        IImageBoard provider = eightKun ? new EightKunImageBoard(client) : new FourChanImageBoard(client);
        await using Registry registry = new([provider], new DownloadQueue(1, 1024), directory.Path, TimeSpan.FromMinutes(1), 60);
        registry.ConfigureBoardAdmission(new(directory.Path, lease));
        string url = eightKun ? "https://8kun.top/g/" : "https://boards.4chan.org/g/";
        var target = registry.ClassifyAdmission(url);
        Assert.True(target!.IsBoard); Assert.Empty(requests);
        var preview = await registry.PreviewBoardAdmissionAsync(url);
        var thread = Assert.Single(preview.Threads);
        Assert.Equal("Cats & dogs", thread.Title);
        Assert.Equal("Hello\nworld & friends", thread.OpeningPost);
        Assert.Equal(new[] { "/boards.json", "/g/catalog.json" }, requests);
        Assert.Empty(registry.Threads); Assert.Empty(registry.Boards);
        Assert.False(File.Exists(Path.Combine(directory.Path, "board_admission.json")));
    }

    [Fact]
    public async Task CommitFiltersFirstDiscoveryAndLaterCatalogsWithoutRemovingManualWatches()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using StubImageBoard provider = new();
        provider.Board.OnCheck = () => Task.FromResult<IEnumerable<IThread>>([
            new ProbeThread(provider.Board, new("https://stub.test/g/thread/1")) { Title = "cat" },
            new ProbeThread(provider.Board, new("https://stub.test/g/thread/2")) { Title = "dog" }]);
        await using Registry registry = new([provider], new DownloadQueue(1, 1024), directory.Path, TimeSpan.FromMinutes(1), 60, persistSession: false);
        registry.ConfigureBoardAdmission(new(directory.Path, lease));
        await registry.AddAsync("https://stub.test/g/thread/2");
        BoardAdmissionPolicy policy = new([new(ThreadTextField.Title, ThreadTextAction.Include, "cat")]);
        await registry.ApplyBoardAdmissionAsync("https://stub.test/g/", policy);
        await TestWait.UntilAsync(() => registry.Threads.Count == 2 && provider.Board.Clock.Waits == 1);
        Assert.Contains(registry.Threads, thread => thread.ThreadId == "2");
        Assert.Single(new BoardAdmissionStore(directory.Path, lease).Get("https://stub.test/g/").Rules);
        provider.Board.Clock.Advance(); await TestWait.UntilAsync(() => provider.Board.Clock.Waits == 2);
        Assert.Equal(2, registry.Threads.Count);
        Assert.False(File.Exists(Path.Combine(directory.Path, "saved_urls.json")));
    }

    [Fact]
    public async Task SavedIncludeDoesNotReactivateLaterRemovalWithoutNewExplicitChoice()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using StubImageBoard provider = new();
        const string url = "https://stub.test/g/thread/123";
        provider.Board.OnCheck = () => Task.FromResult<IEnumerable<IThread>>([provider.GetThread(new(url))]);
        await using Registry registry = new([provider], new DownloadQueue(1, 1024), directory.Path, TimeSpan.FromMinutes(1), 60);
        registry.ConfigureBoardAdmission(new(directory.Path, lease));
        BoardAdmissionPolicy policy = new([], new Dictionary<string, ThreadAdmissionException> { [url] = ThreadAdmissionException.Include });
        await registry.ApplyBoardAdmissionAsync("https://stub.test/g/", policy);
        await TestWait.UntilAsync(() => registry.Threads.Count == 1 && provider.Board.Clock.Waits == 1);
        await registry.RemoveThreadAsync(Assert.Single(registry.Threads));
        await registry.ApplyBoardAdmissionAsync("https://stub.test/g/", policy);
        provider.Board.Clock.Advance(); await TestWait.UntilAsync(() => provider.Board.Clock.Waits == 2);
        Assert.Empty(registry.Threads);
        var preview = await registry.PreviewBoardAdmissionAsync("https://stub.test/g/");
        Assert.True(Assert.Single(preview.Threads).Suppressed);
        await registry.ApplyBoardAdmissionAsync("https://stub.test/g/", policy, new[] { url });
        Assert.Single(registry.Threads);
    }

    [Fact]
    public void CorruptPolicyDataBlocksInsteadOfSilentlyMonitoringEverythingAndBackupRecovers()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, "board_admission.json");
        File.WriteAllText(path, "invalid");
        Assert.Throws<InvalidDataException>(() => new BoardAdmissionStore(directory.Path, lease));
        File.Delete(path);
        var store = new BoardAdmissionStore(directory.Path, lease);
        store.Save("https://stub.test/g/", new([new(ThreadTextField.Title, ThreadTextAction.Exclude, "spam")]));
        store.Save("https://stub.test/g/", BoardAdmissionPolicy.All);
        File.WriteAllText(path, "invalid");
        var recovered = new BoardAdmissionStore(directory.Path, lease);
        Assert.NotNull(recovered.LoadWarning);
        Assert.Single(recovered.Get("https://stub.test/g/").Rules);
        recovered.Save("https://stub.test/g/", BoardAdmissionPolicy.All);
        Assert.Contains("spam", File.ReadAllText(path + ".bak"));
    }
}
