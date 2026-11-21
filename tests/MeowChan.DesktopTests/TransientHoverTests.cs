using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class TransientHoverTests
{
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("top")]
    [InlineData("bottom")]
    [InlineData("top-right corner")]
    public void CurrentHitTestReleasesEveryWindowEdgeAndMovementReentersWithoutClick(string edge)
    {
        Assert.NotEmpty(edge);
        var state = new TransientHoverState();
        Assert.True(state.Observe(pointerOver: true, usable: true, moved: true));
        // Native coordinates may remain on the last interior pixel. The platform's
        // current IsPointerOver=false is authoritative even with no final move/exit handler.
        Assert.False(state.Observe(pointerOver: false, usable: true));
        Assert.False(state.Hovered);
        Assert.True(state.Observe(pointerOver: true, usable: true, moved: true));
    }

    [Theory]
    [InlineData("deactivated")]
    [InlineData("minimized")]
    [InlineData("hidden/workspace/lock")]
    [InlineData("modal")]
    [InlineData("detached")]
    public void UnusableSurfaceCannotRestoreStaleHoverOnActivation(string reason)
    {
        Assert.NotEmpty(reason);
        var state = new TransientHoverState();
        Assert.True(state.Observe(true, true, moved: true));
        Assert.False(state.Observe(true, false));
        Assert.False(state.Observe(true, true)); // activation alone is not pointer evidence
        Assert.True(state.Observe(true, true, moved: true));
    }

    [Fact]
    public void ReplacedItemOrDifferentInteractionNeedsFreshPointerEvidence()
    {
        var state = new TransientHoverState();
        Assert.True(state.Observe(true, true, moved: true));
        state.Release();
        Assert.False(state.Hovered);
        Assert.False(state.Observe(true, true));
        Assert.True(state.Observe(true, true, moved: true));
    }

    [Theory]
    [InlineData(false, true)] // native leave, including right edge
    [InlineData(true, false)] // deactivation, minimize, modal, detach
    public void ReleaseCancelsRealPendingDecodeAndLateCompletionCannotShow(bool pointerOver, bool usable) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        QuotePreviewMedia media = new();
        TaskCompletionSource<Bitmap?> pending = new();
        CancellationToken token = default;
        ArchiveAttachmentRow row = new(new CapturedAttachment(0, "hover", OriginalFileName: "slow.png"))
        {
            LoadSized = (_, _, cancellation) => { token = cancellation; return pending.Task; }
        };
        using var hover = new TransientHover(new Border(), active =>
        {
            if (active) media.Show(row, 1, new Size(400, 300), 1, ReaderInlineVideoOptions.Default);
            else media.Clear();
        });
        // Same state reconciliation/callback path as the native adapter, with explicit
        // window facts because this unit lane has no native pointer/window server.
        hover.Reconcile(true, true, moved: true);
        Assert.True(token.CanBeCanceled);
        Assert.False(token.IsCancellationRequested);
        hover.Reconcile(pointerOver, usable, moved: false);
        Assert.True(token.IsCancellationRequested);
        pending.SetResult(new WriteableBitmap(new PixelSize(40, 30), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(media.Shown);
        Assert.Null(media.Picture.Source);
        Assert.False(media.ShowsMedium);
    });
}
