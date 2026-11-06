using System.Globalization;
using MeowChan;
using MeowChan.Desktop.Localization;
using MeowChan.Storage;
using Op = MeowChan.MediaHashSearchOperation;

namespace MeowChan.DesktopTests;

// Synthetic immutable snapshots; no paths are opened. No native window/renderer PASS claim.
[Collection("Localization")]
public sealed class MediaHashSearchDialogTests
{
    private static readonly string Hash = new('a', 64);
    private static MediaAssetReference Asset(string id, string? hash = null, string variant = "original", bool current = true)
        => new(id, "media-" + id, variant, "PRIVATE-file.bin", "/PRIVATE/never-opened/" + id,
            123, hash ?? Hash, "https://PRIVATE.invalid/?token=secret", "PRIVATE-original.bin", current);
    private static MediaAssetSnapshot Snapshot(MediaAssetReference[] assets, MediaManifestStatus status = MediaManifestStatus.Available,
        string? generation = "captured-generation") => new("/PRIVATE/never-opened", status,
            status == MediaManifestStatus.Available ? 4 : null, generation, assets, "PRIVATE warning");
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Op.WorkResult Work(MediaSnapshotHashQuery query) => new(query.Search(), true);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(" aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaＡ")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaag")]
    public async Task InvalidInputDoesNotDispatchOrNormalize(string? value)
    {
        int calls = 0;
        var operation = new Op(Snapshot([Asset("one")]), executeForTests: (query, token) =>
        { calls++; return Task.FromResult(Work(query)); });
        Assert.Equal(Op.Status.Ready, operation.Current.Status);
        Assert.Equal(0, calls);
        await operation.SearchAsync(value);
        Assert.Equal(0, calls);
        Assert.Equal(Op.Status.Invalid, operation.Current.Status);
        Assert.Empty(operation.Current.Page);
        Assert.Null(operation.Select(operation.Current.Request, 0, 0));
        await operation.TerminateAsync();
    }

    [Fact]
    public async Task ExactUppercaseAndLeadingZeroUseTheSameCapturedSnapshotOnlyOnExplicitStart()
    {
        string digest = "0" + new string('b', 63);
        var asset = Asset("zero", digest);
        var snapshot = Snapshot([asset], generation: "captured-old");
        var operation = new Op(snapshot);
        operation.Invalidate(); // Editing does not search.
        Assert.Equal(Op.Status.Ready, operation.Current.Status);
        Assert.Empty(operation.Current.Page);
        await operation.SearchAsync(digest.ToUpperInvariant());
        Assert.Equal(Op.Status.Available, operation.Current.Status);
        Assert.Equal("captured-old", operation.Current.Generation);
        Assert.Same(asset, Assert.Single(operation.Current.Page));
        Assert.Same(asset, operation.Select(operation.Current.Request, 0, 0));
        Assert.Equal(digest, snapshot.GetPage()[0].Sha256);
        await operation.TerminateAsync();
    }

    [Fact]
    public async Task AllIdsVariantsAndCapturedCurrentFlagsRemainOnBoundedPages()
    {
        var assets = Enumerable.Range(0, 205).Select(i => Asset(i.ToString("D3", CultureInfo.InvariantCulture),
            variant: i % 2 == 0 ? "original" : "preview", current: i % 3 == 0)).ToArray();
        var operation = new Op(Snapshot(assets));
        await operation.SearchAsync(Hash);
        Assert.Equal(205, operation.Current.Count);
        Assert.Equal(100, operation.Current.Page.Length);
        var first = operation.Current;
        var selected = new List<MediaAssetReference>();
        selected.AddRange(first.Page);
        operation.MovePage(-1);
        Assert.Equal(0, operation.Current.Offset);
        operation.MovePage(1);
        Assert.Equal(100, operation.Current.Offset);
        Assert.Null(operation.Select(first.Request, first.Offset, 0)); // stale page button
        selected.AddRange(operation.Current.Page);
        operation.MovePage(1);
        Assert.Equal(200, operation.Current.Offset);
        Assert.Equal(5, operation.Current.Page.Length);
        selected.AddRange(operation.Current.Page);
        operation.MovePage(1);
        Assert.Equal(200, operation.Current.Offset);
        Assert.Equal(assets.Length, selected.Count);
        Assert.Equal(assets.Select(x => x.AssetId), selected.Select(x => x.AssetId));
        for (int i = 0; i < assets.Length; i++) Assert.Same(assets[i], selected[i]);
        Assert.Contains(selected, x => x.Variant == "preview" && !x.IsCurrent);
        Assert.Contains(selected, x => x.Variant == "original" && x.IsCurrent);
        await operation.TerminateAsync();
    }

    [Theory]
    [InlineData(MediaManifestStatus.Missing, "Missing")]
    [InlineData(MediaManifestStatus.Corrupt, "Corrupt")]
    public async Task MissingAndCorruptAreNotAnEmptySuccessfulInventory(MediaManifestStatus manifest, string expected)
    {
        var operation = new Op(Snapshot([], manifest, null));
        await operation.SearchAsync(Hash);
        Assert.Equal(expected, operation.Current.Status.ToString());
        Assert.Null(operation.Current.Generation);
        Assert.Empty(operation.Current.Page);
        Assert.Null(operation.Select(operation.Current.Request, 0, 0));
        await operation.TerminateAsync();
    }

    [Fact]
    public async Task MissingMeasurementAndNoMatchAreDifferentWithoutRehash()
    {
        var none = new Op(Snapshot([Asset("no-measurement", "")]));
        await none.SearchAsync(Hash);
        Assert.Equal(Op.Status.NoMeasurements, none.Current.Status);
        Assert.Empty(none.Current.Page);
        var measured = new Op(Snapshot([Asset("other", new string('c', 64))]));
        await measured.SearchAsync(Hash);
        Assert.Equal(Op.Status.Available, measured.Current.Status);
        Assert.Equal(0, measured.Current.Count);
        Assert.Empty(measured.Current.Page);
        await none.TerminateAsync(); await measured.TerminateAsync();
    }

    [Fact]
    public async Task EmptyCapturedInventoryHasNoRecordedMeasurementAndCannotSelect()
    {
        var operation = new Op(Snapshot([]));
        await operation.SearchAsync(Hash);
        Assert.Equal(Op.Status.NoMeasurements, operation.Current.Status);
        Assert.Equal("captured-generation", operation.Current.Generation);
        Assert.Null(operation.Select(operation.Current.Request, 0, 0));
        await operation.TerminateAsync();
        Assert.True(Op.IsExactSha256(new string('0', 64)));
        Assert.True(Op.IsExactSha256(new string('F', 64)));
        Assert.False(Op.IsExactSha256(new string('a', 63)));
        Assert.False(Op.IsExactSha256(new string('a', 65)));
        Assert.False(Op.IsExactSha256(Hash + "\n"));
    }

    [Fact]
    public async Task LateOldRequestCannotReplaceNewRequestOrAuthorizeOldButton()
    {
        var delayed = Gate<Op.WorkResult>(); int calls = 0;
        MediaSnapshotHashQuery? old = null;
        var operation = new Op(Snapshot([Asset("first"), Asset("second", new string('b', 64))]),
            executeForTests: (query, token) =>
            {
                calls++;
                if (calls == 1) { old = query; return delayed.Task; } // Ignores cancellation deliberately.
                return Task.FromResult(Work(query));
            });
        Task first = operation.SearchAsync(Hash);
        long firstRequest = operation.Current.Request;
        await operation.SearchAsync(new string('b', 64));
        var current = operation.Current;
        Assert.Equal("second", Assert.Single(current.Page).AssetId);
        delayed.SetResult(Work(old!)); await first;
        Assert.Equal(current, operation.Current);
        Assert.Null(operation.Select(firstRequest, 0, 0));
        Assert.Equal("second", operation.Select(current.Request, 0, 0)!.AssetId);
        await operation.TerminateAsync();
    }

    [Fact]
    public async Task InputEditRevokesPendingAndCompletedSelectionsWithoutStartingAnotherSearch()
    {
        var delayed = Gate<Op.WorkResult>(); MediaSnapshotHashQuery? captured = null; int calls = 0;
        var operation = new Op(Snapshot([Asset("one")]), executeForTests: (query, token) =>
        { calls++; captured = query; return delayed.Task; });
        Task run = operation.SearchAsync(Hash);
        long request = operation.Current.Request;
        operation.Invalidate();
        Assert.Equal(Op.Status.Ready, operation.Current.Status);
        delayed.SetResult(Work(captured!)); await run;
        Assert.Equal(1, calls);
        Assert.Empty(operation.Current.Page);
        Assert.Null(operation.Select(request, 0, 0));
        await operation.TerminateAsync();

        var completed = new Op(Snapshot([Asset("one")]));
        await completed.SearchAsync(Hash);
        var view = completed.Current;
        Assert.NotNull(completed.Select(view.Request, 0, 0));
        completed.Invalidate();
        Assert.Null(completed.Select(view.Request, 0, 0));
        await completed.TerminateAsync();
    }

    [Fact]
    public async Task ClosingDrainsUncooperativeWorkWithoutLateNotificationsOrSelection()
    {
        var delayed = Gate<Op.WorkResult>(); MediaSnapshotHashQuery? query = null;
        var operation = new Op(Snapshot([Asset("one")]), executeForTests: (q, token) =>
        { query = q; return delayed.Task; });
        int changes = 0; operation.Changed += () => changes++;
        Task run = operation.SearchAsync(Hash); long request = operation.Current.Request;
        Task drain = operation.TerminateAsync(); int before = changes;
        Assert.Same(drain, operation.TerminateAsync());
        Assert.False(drain.IsCompleted);
        delayed.SetResult(Work(query!)); await drain; await run;
        Assert.Equal(before, changes);
        Assert.Equal(Op.Status.Closed, operation.Current.Status);
        Assert.Empty(operation.Current.Page);
        Assert.Null(operation.Select(request, 0, 0));
        await operation.SearchAsync(Hash);
        Assert.Equal(Op.Status.Closed, operation.Current.Status);
    }

    [Fact]
    public async Task ParentCancellationRevokesAlreadyPublishedResultsAndLateWork()
    {
        using var parent = new CancellationTokenSource();
        var operation = new Op(Snapshot([Asset("one")]), parent.Token);
        await operation.SearchAsync(Hash); var view = operation.Current;
        parent.Cancel();
        Assert.Equal(Op.Status.Canceled, operation.Current.Status);
        Assert.Empty(operation.Current.Page);
        Assert.Null(operation.Select(view.Request, 0, 0));
        await operation.TerminateAsync();

        using var pendingParent = new CancellationTokenSource();
        var delayed = Gate<Op.WorkResult>(); MediaSnapshotHashQuery? captured = null;
        var pending = new Op(Snapshot([Asset("one")]), pendingParent.Token, (query, token) =>
        { captured = query; return delayed.Task; });
        Task run = pending.SearchAsync(Hash);
        pendingParent.Cancel(); delayed.SetResult(Work(captured!)); await run;
        Assert.Equal(Op.Status.Canceled, pending.Current.Status);
        Assert.Empty(pending.Current.Page);
        await pending.TerminateAsync();
    }

    [Fact]
    public async Task AlreadyCanceledLifetimeNeverDispatchesEvenAfterAnInputEdit()
    {
        using var parent = new CancellationTokenSource(); parent.Cancel(); int calls = 0;
        var operation = new Op(Snapshot([Asset("one")]), parent.Token, (query, token) =>
        { calls++; return Task.FromResult(Work(query)); });
        operation.Invalidate();
        await operation.SearchAsync(Hash);
        Assert.Equal(0, calls);
        Assert.Equal(Op.Status.Canceled, operation.Current.Status);
        Assert.Empty(operation.Current.Page);
        await operation.TerminateAsync();
    }

    [Fact]
    public async Task FailureDoesNotSurfacePrivateExceptionOrKeepPreviousSelection()
    {
        int calls = 0;
        var operation = new Op(Snapshot([Asset("one")]), executeForTests: (query, token) =>
            ++calls == 1 ? Task.FromResult(Work(query)) : Task.FromException<Op.WorkResult>(new Exception("PRIVATE path/token")));
        await operation.SearchAsync(Hash); var old = operation.Current;
        await operation.SearchAsync(Hash);
        Assert.Equal(Op.Status.Failed, operation.Current.Status);
        Assert.Empty(operation.Current.Page);
        Assert.DoesNotContain("PRIVATE", operation.Current.ToString());
        Assert.Null(operation.Select(old.Request, 0, 0));
        await operation.TerminateAsync();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void LocalCatalogAndRowProjectionNeverUsePathUrlOrFilename(string language)
    {
        string before = Texts.Language;
        try
        {
            Texts.SetLanguage(language);
            Assert.Equal(MediaHashSearchCatalog.Entries.Length, MediaHashSearchCatalog.Entries.Select(x => x.Key).Distinct().Count());
            foreach (var entry in MediaHashSearchCatalog.Entries)
            {
                string template = language == "de" ? entry.German : entry.English;
                Assert.False(string.IsNullOrWhiteSpace(template));
                int count = System.Text.CompositeFormat.Parse(template).MinimumArgumentCount;
                object?[] arguments = Enumerable.Repeat<object?>("SAFE", count).ToArray();
                Assert.Equal(string.Format(CultureInfo.GetCultureInfo(language), template, arguments),
                    MediaHashSearchCatalog.T(entry.Key, arguments));
            }
            string row = MediaHashSearchDialog.RowLabel(Asset("safe", variant: "preview", current: false));
            Assert.Contains("safe", row); Assert.Contains("preview", row);
            Assert.DoesNotContain("PRIVATE", row); Assert.DoesNotContain("secret", row);
            Assert.DoesNotContain("https:", row); Assert.DoesNotContain("/", row);
            string unsafeIds = MediaHashSearchDialog.RowLabel(Asset("/PRIVATE/path", variant: "https://PRIVATE.invalid"));
            Assert.DoesNotContain("PRIVATE", unsafeIds); Assert.DoesNotContain("https:", unsafeIds);
            Assert.DoesNotContain("/", unsafeIds);
            Assert.Equal(MediaHashSearchCatalog.T("mediahash.omitted"), MediaHashSearchDialog.DisplayToken("C:\\PRIVATE"));
        }
        finally { Texts.SetLanguage(before); }
    }
}
