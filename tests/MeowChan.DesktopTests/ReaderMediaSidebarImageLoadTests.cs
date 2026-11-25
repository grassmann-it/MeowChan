using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// No wall-clock sleeps: hold resolution/decode/preloading independently, then deliver results and the real timer callback in any order.
[Collection("Localization")]
public sealed class ReaderMediaSidebarImageLoadTests
{
    private static void OnDispatcher(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        test();
    });

    private static TaskCompletionSource<ReaderMediaSidebarContent> Pending() => new();
    private static WriteableBitmap Bitmap(int width = 40, int height = 64) =>
        new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    private static ReaderMediaSidebarContent.Picture Content(Bitmap bitmap, List<Bitmap> released) =>
        new(bitmap, released.Add, false, new PixelSize(100, 160));

    private static ReaderMediaSidebarImageLoad Begin(ReaderMediaSidebar view, string key, CancellationToken token = default,
        Func<bool>? available = null, double width = 40, double height = 64) =>
        new(view, key, view.Load.Begin(key, (width, height)), new GalleryFitPreferences(true, 150), available ?? (() => true), token);

    private static void Settled(Task task)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.True(task.IsCompletedSuccessfully, task.Exception?.ToString() ?? "The explicitly completed operation did not settle.");
    }

    [Fact]
    public void ReadyImageSurvivesItsLoadingCallbackWhileNeighbourPreloadingIsStillPending() => OnDispatcher(() =>
    {
        ReaderMediaSidebar view = new();
        List<Bitmap> released = [];
        using WriteableBitmap picture = Bitmap();
        TaskCompletionSource<ReaderMediaSidebarContent> decode = Pending();
        TaskCompletionSource neighbours = new();
        bool warming = false;
        ReaderMediaSidebarImageLoad load = Begin(view, "post-a");
        Task task = load.RunAsync(_ => decode.Task, preload: () => { warming = true; return neighbours.Task; });
        decode.SetResult(Content(picture, released));
        Dispatcher.UIThread.RunJobs();
        Assert.True(warming);
        Assert.False(task.IsCompleted);
        load.ShowLoading(); // The old product called ShowPlaceholder here, releasing the picture permanently.
        Assert.Same(picture, view.Picture.Source);
        Assert.Equal("post-a", view.Load.ShownKey);
        Assert.False(view.Load.Awaiting);
        Assert.Equal(string.Empty, view.StatusText);
        Assert.Empty(released);
        neighbours.SetException(new IOException("neighbour failed after current image was shown"));
        Settled(task);
        Assert.Same(picture, view.Shown);
        Assert.Equal(string.Empty, view.StatusText);
        view.ReleasePicture();
        Assert.Equal(new[] { (Bitmap)picture }, released);
    });

    [Fact]
    public void OlderCompletionAfterNewerImageReturnsItsLeaseWithoutChangingThePictureOrLoader() => OnDispatcher(() =>
    {
        ReaderMediaSidebar view = new();
        List<Bitmap> released = [];
        using WriteableBitmap old = Bitmap(), current = Bitmap();
        TaskCompletionSource<ReaderMediaSidebarContent> first = Pending(), second = Pending();
        ReaderMediaSidebarImageLoad older = Begin(view, "post-a");
        Task a = older.RunAsync(_ => first.Task);
        ReaderMediaSidebarImageLoad newer = Begin(view, "post-b");
        Task b = newer.RunAsync(_ => second.Task);
        second.SetResult(Content(current, released));
        Settled(b);
        first.SetResult(Content(old, released)); // Deliberately ignores the cancelled/superseded request.
        Settled(a);
        older.ShowLoading();
        newer.ShowLoading();
        Assert.Same(current, view.Picture.Source);
        Assert.Equal("post-b", view.Load.ShownKey);
        Assert.Equal(string.Empty, view.StatusText);
        Assert.Equal(new[] { (Bitmap)old }, released);
        view.ReleasePicture();
        Assert.Equal(new[] { (Bitmap)old, current }, released);
    });

    [Theory]
    [InlineData("picture")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public void AnObsoleteCompletionCannotStopOrReplaceTheNewRequestsLoadingState(string completion) => OnDispatcher(() =>
    {
        ReaderMediaSidebar view = new();
        List<Bitmap> released = [];
        using WriteableBitmap old = Bitmap(), current = Bitmap();
        TaskCompletionSource<ReaderMediaSidebarContent> first = Pending(), second = Pending();
        ReaderMediaSidebarImageLoad older = Begin(view, "post-a");
        Task a = older.RunAsync(_ => first.Task);
        ReaderMediaSidebarImageLoad newer = Begin(view, "post-b");
        Task b = newer.RunAsync(_ => second.Task);
        newer.ShowLoading();
        Assert.True(view.Load.Awaiting);
        if (completion == "picture") first.SetResult(Content(old, released));
        else if (completion == "failure") first.SetException(new IOException("obsolete failure"));
        else first.SetCanceled();
        Settled(a);
        older.ShowLoading();
        Assert.Equal(Texts.T("reader.sidebar.loading"), view.StatusText); // The old error/cancel cannot answer the new request.
        Assert.True(view.Load.Awaiting);
        Assert.False(b.IsCompleted);
        second.SetResult(Content(current, released));
        Settled(b);
        Assert.Same(current, view.Picture.Source);
        Assert.Equal("post-b", view.Load.ShownKey);
        Assert.Equal(string.Empty, view.StatusText);
        Assert.Equal(completion == "picture" ? 1 : 0, released.Count);
        view.ReleasePicture();
    });

    [Fact]
    public void SameMediumPreviewRemainsUntilLargerDecodeIsReadyWithFitBoundsAndExactlyOneRelease() => OnDispatcher(() =>
    {
        ReaderMediaSidebar view = new();
        List<Bitmap> released = [];
        using WriteableBitmap preview = Bitmap(), full = Bitmap(80, 128);
        view.ShowPicture(preview, released.Add, false, 100, 160, "post-a", new PixelSize(100, 160));
        ReaderMediaSidebarImageLoad upgrade = Begin(view, "post-a", width: 80, height: 128);
        TaskCompletionSource<ReaderMediaSidebarContent> decode = Pending();
        Task task = upgrade.RunAsync(_ => decode.Task);
        upgrade.ShowLoading();
        Assert.Same(preview, view.Picture.Source);
        Assert.False(view.IsDimmed);
        Assert.Equal(string.Empty, view.StatusText);
        Assert.True(view.Load.Awaiting);
        Assert.Empty(released);
        decode.SetResult(Content(full, released));
        Settled(task);
        Assert.Same(full, view.Picture.Source);
        Assert.Equal(new PixelSize(80, 128), view.Load.ShownPixels);
        Assert.Equal(150, view.Picture.MaxWidth);
        Assert.Equal(240, view.Picture.MaxHeight);
        Assert.False(view.Load.Awaiting);
        Assert.Equal(new[] { (Bitmap)preview }, released);
        view.ReleasePicture();
        Assert.Equal(new[] { (Bitmap)preview, full }, released);
    });

    [Theory]
    [InlineData("missing")]
    [InlineData("no-poster")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public void FailedSizeUpgradeKeepsTheSameMediumPreviewAndEndsLoading(string completion) => OnDispatcher(() =>
    {
        ReaderMediaSidebar view = new();
        List<Bitmap> released = [];
        using WriteableBitmap preview = Bitmap();
        view.ShowPicture(preview, released.Add, false, 100, 160, "post-a", new PixelSize(100, 160));
        ReaderMediaSidebarImageLoad load = Begin(view, "post-a", width: 80, height: 128);
        TaskCompletionSource<ReaderMediaSidebarContent> pending = Pending();
        Task task = load.RunAsync(_ => pending.Task);
        if (completion == "failure") pending.SetException(new IOException("full resolution failed"));
        else if (completion == "cancel") pending.SetCanceled();
        else pending.SetResult(new ReaderMediaSidebarContent.Unavailable(completion == "missing" ? "archive.notSaved" : "reader.sidebar.unavailable"));
        Settled(task);
        load.ShowLoading();
        Assert.Same(preview, view.Picture.Source);
        Assert.Equal("post-a", view.Load.ShownKey);
        Assert.False(view.Load.Awaiting);
        Assert.False(view.IsDimmed);
        Assert.Equal(string.Empty, view.StatusText);
        Assert.Empty(released);
        view.ReleasePicture();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentCancellationEndsLoadingAndReleasesANonCooperativeResult(bool returnsPicture) => OnDispatcher(() =>
    {
        ReaderMediaSidebar view = new();
        List<Bitmap> released = [];
        using WriteableBitmap picture = Bitmap();
        using CancellationTokenSource cancellation = new();
        ReaderMediaSidebarImageLoad load = Begin(view, "post-a", cancellation.Token);
        TaskCompletionSource<ReaderMediaSidebarContent> pending = Pending();
        Task task = load.RunAsync(_ => pending.Task);
        load.ShowLoading();
        Assert.Equal(Texts.T("reader.sidebar.loading"), view.StatusText);
        if (returnsPicture) { cancellation.Cancel(); pending.SetResult(Content(picture, released)); }
        else pending.SetCanceled(); // A cancelled producer with an otherwise current selection used to leave Loading forever.
        Settled(task);
        load.ShowLoading();
        Assert.Null(view.Picture.Source);
        Assert.False(view.Load.Awaiting);
        Assert.Equal(Texts.T("reader.sidebar.unavailable"), view.StatusText);
        Assert.Equal(returnsPicture ? 1 : 0, released.Count);
    });

    [Fact]
    public void PreviousPostPictureIsImmediatelyMarkedAsLoadingAndNeverReusedAsTheNewMedium() => OnDispatcher(() =>
    {
        ReaderMediaSidebar view = new();
        List<Bitmap> released = [];
        using WriteableBitmap previous = Bitmap();
        view.ShowPicture(previous, released.Add, false, 100, 160, "post-a", new PixelSize(100, 160));
        ReaderMediaSidebarImageLoad load = Begin(view, "post-b");
        TaskCompletionSource<ReaderMediaSidebarContent> pending = Pending();
        Task task = load.RunAsync(_ => pending.Task);
        Assert.True(view.IsDimmed);
        Assert.Null(view.Load.ShownKey);
        Assert.Equal(Texts.T("reader.sidebar.loading"), view.StatusText);
        pending.SetResult(new ReaderMediaSidebarContent.Unavailable("archive.notSaved"));
        Settled(task);
        Assert.Null(view.Shown);
        Assert.Null(view.Picture.Source);
        Assert.Equal(Texts.T("archive.notSaved"), view.StatusText);
        Assert.Equal(new[] { (Bitmap)previous }, released);
    });

    [Fact]
    public void HidingTheSidebarRetiresEveryPendingCompletionAndTimer() => OnDispatcher(() =>
    {
        ReaderMediaSidebar view = new();
        List<Bitmap> released = [];
        using WriteableBitmap picture = Bitmap();
        bool visible = true;
        ReaderMediaSidebarImageLoad load = Begin(view, "post-a", available: () => visible);
        TaskCompletionSource<ReaderMediaSidebarContent> pending = Pending();
        Task task = load.RunAsync(_ => pending.Task);
        visible = false;
        view.Load.Cancel();
        view.ShowEmpty();
        string empty = view.StatusText;
        pending.SetResult(Content(picture, released));
        Settled(task);
        load.ShowLoading();
        Assert.Null(view.Shown);
        Assert.Equal(empty, view.StatusText);
        Assert.Equal(new[] { (Bitmap)picture }, released);
    });

    [Fact]
    public void ReaderSelectionRetiresTheOldRequestBeforeTheFollowTimerCanStartTheNextLoad() => OnDispatcher(() =>
    {
        TokenApplication.Bind();
        ArchivePage page = new(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { });
        ReaderMediaSidebar view = new();
        using CancellationTokenSource pending = new(), strip = new();
        CancellationToken token = pending.Token, stripToken = strip.Token;
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Set(string name, object value) => typeof(ArchivePage).GetField(name, flags)!.SetValue(page, value);
            Set("_mediaSidebar", view);
            Set("_mediaSidebarShown", true);
            Set("_sidebarLoading", pending);
            Set("_sidebarStripLoading", strip);
            ReaderMediaLoadTicket old = view.Load.Begin("post-a", (40, 64));
            // Null selection takes the deferred path, exactly as a thread/page rebuild while Up/Down is navigating does.
            Assert.Null(page.FindControl<ListBox>("ArchivePosts")!.SelectedItem);
            MethodInfo selectionChanged = typeof(ArchivePage).GetMethod("OnMediaSidebarSelectionChanged", flags)!;
            selectionChanged.Invoke(page, selectionChanged.GetParameters().Length == 0 ? null : new object[] { true });
            Assert.True(token.IsCancellationRequested);
            Assert.True(stripToken.IsCancellationRequested);
            Assert.False(view.Load.IsCurrent(old.Request));
            Assert.True(((DispatcherTimer)typeof(ArchivePage).GetField("_sidebarFollowTimer", flags)!.GetValue(page)!).IsEnabled);
        }
        finally { page.EndLifetime(); }
    });

    [Theory]
    [InlineData(true, "missing")]
    [InlineData(true, "cancel")]
    [InlineData(true, "failure")]
    [InlineData(false, "cancel")]
    public void ReaderHostDelayedResolutionHasAFinalAnswerWithoutDestroyingItsValidPreview(bool hasPreview, string completion) => OnDispatcher(() =>
    {
        TokenApplication.Bind();
        ArchivePage page = new(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { });
        ReaderMediaSidebar view = new();
        List<Bitmap> released = [];
        using WriteableBitmap preview = Bitmap();
        TaskCompletionSource<GalleryEntry?> resolve = new();
        ArchiveAttachmentRow row = new(new CapturedAttachment(0, "a", OriginalFileName: "a.png")) { ResolveEntry = _ => resolve.Task };
        ArchivePostRow post = new("post-a", "", "", [row]);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            typeof(ArchivePage).GetField("_mediaSidebar", flags)!.SetValue(page, view);
            typeof(ArchivePage).GetField("_mediaSidebarShown", flags)!.SetValue(page, true);
            if (hasPreview) view.ShowPicture(preview, released.Add, false, 100, 160, "post-a", new PixelSize(100, 160));
            ReaderMediaLoadTicket ticket = view.Load.Begin("post-a", (80, 128));
            Task load = (Task)typeof(ArchivePage).GetMethod("LoadMediaSidebarAsync", flags)!.Invoke(page,
                new object[] { view, post, row, "post-a", ticket, (80d, 128d), CancellationToken.None })!;
            view.ShowLoading(view.Load.LoadingLook(ticket.Request), Texts.T("reader.sidebar.loading"));
            if (completion == "missing") resolve.SetResult(null);
            else if (completion == "cancel") resolve.SetCanceled();
            else resolve.SetException(new IOException("synthetic resolver failure"));
            Settled(load);
            Assert.False(view.Load.Awaiting);
            Assert.Equal(hasPreview ? string.Empty : Texts.T("reader.sidebar.unavailable"), view.StatusText);
            Assert.Same(hasPreview ? preview : null, view.Picture.Source);
            Assert.Empty(released);
        }
        finally { page.EndLifetime(); }
    });

    [Fact]
    public void TwoSizeUpgradesCannotRegressTheSameMediumToTheOlderDecode() => OnDispatcher(() =>
    {
        ReaderMediaSidebar view = new();
        List<Bitmap> released = [];
        using WriteableBitmap preview = Bitmap(), earlier = Bitmap(60, 96), latest = Bitmap(80, 128);
        view.ShowPicture(preview, released.Add, false, 100, 160, "post-a", new PixelSize(100, 160));
        TaskCompletionSource<ReaderMediaSidebarContent> first = Pending(), second = Pending();
        ReaderMediaSidebarImageLoad smaller = Begin(view, "post-a", width: 60, height: 96);
        Task a = smaller.RunAsync(_ => first.Task);
        ReaderMediaSidebarImageLoad larger = Begin(view, "post-a", width: 80, height: 128);
        Task b = larger.RunAsync(_ => second.Task);
        smaller.ShowLoading();
        larger.ShowLoading();
        Assert.Same(preview, view.Picture.Source);
        Assert.False(view.IsDimmed);
        second.SetResult(Content(latest, released));
        Settled(b);
        first.SetResult(Content(earlier, released));
        Settled(a);
        Assert.Same(latest, view.Picture.Source);
        Assert.Equal(new PixelSize(80, 128), view.Load.ShownPixels);
        Assert.Equal(string.Empty, view.StatusText);
        Assert.Equal(new[] { (Bitmap)preview, earlier }, released);
        view.ReleasePicture();
        Assert.Equal(new[] { (Bitmap)preview, earlier, latest }, released);
    });


    [Fact]
    public void ReaderRebuiltSelectionResumesMissingThumbnailsWhenStripLayoutDidNotChange() => OnDispatcher(() =>
    {
        TokenApplication.Bind();
        ArchivePage page = new(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { });
        ReaderMediaSidebar view = new();
        int requests = 0;
        ArchiveAttachmentRow Row(int index) => new(new CapturedAttachment(index, "a" + index))
        {
            LoadStripThumbnail = (_, _, _) => { requests++; return Task.FromResult<Bitmap?>(null); }
        };
        ArchivePostRow post = new("post-a", "", "", [Row(0), Row(1)]);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            typeof(ArchivePage).GetField("_mediaSidebar", flags)!.SetValue(page, view);
            typeof(ArchivePage).GetField("_mediaSidebarShown", flags)!.SetValue(page, true);
            // A cancelled previous load left the already-built strip with missing pictures.
            view.ShowStrip(2, 0, page.ReaderMedia.Clamped().StripLimit, index => "a" + index, samePost: false);
            typeof(ArchivePage).GetMethod("UpdateSidebarStrip", flags)!.Invoke(page, new object[] { view, post, true });
            Settled(page.MediaSidebarStripTask);
            Assert.Equal(2, requests);
        }
        finally { page.EndLifetime(); }
    });

}
