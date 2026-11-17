using System.Reflection;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class CtsDisposalTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    // .NET 10's live callback list, not its reusable/free nodes. Fail loudly if the runtime representation changes.
    internal static int RegistrationCount(CancellationTokenSource source)
    {
        object? registrations = typeof(CancellationTokenSource).GetField("_registrations", Fields)!.GetValue(source);
        if (registrations == null) return 0;
        object? node = registrations.GetType().GetField("Callbacks", Fields)!.GetValue(registrations);
        int count = 0;
        while (node != null)
        {
            Assert.NotNull(node.GetType().GetField("Callback", Fields)!.GetValue(node));
            count++;
            node = node.GetType().GetField("Next", Fields)!.GetValue(node);
        }
        return count;
    }

    [Theory]
    [InlineData("success")]
    [InlineData("fault")]
    [InlineData("cancel")]
    public async Task CancelledNonCooperativeWorkOwnsSourceUntilCompletion(string outcome)
    {
        using CancellationTokenSource parent = new();
        using CancellationTokenSource child = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        bool released = false;
        Task work = CancellationWork.RunAsync(child, value => { token = value; return pending.Task; }, () => released = true);
        Assert.Equal(1, RegistrationCount(parent));
        child.Cancel();
        Assert.False(work.IsCompleted);
        Assert.False(released);
        Assert.True(token.WaitHandle.WaitOne(0)); // Throws if cancellation disposed a still-running worker's source.
        if (outcome == "fault") pending.SetException(new IOException("controlled failure"));
        else if (outcome == "cancel") pending.SetCanceled();
        else pending.SetResult();
        if (outcome == "fault") await Assert.ThrowsAsync<IOException>(() => work);
        else if (outcome == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        else await work;
        Assert.True(released);
        Assert.Equal(0, RegistrationCount(parent));
        Assert.Throws<ObjectDisposedException>(() => child.Token);
    }

    [Fact]
    public void FiftyCompletedSidebarStripRefreshesReturnParentRegistrationsToBaseline() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        ArchivePage page = new(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { });
        ReaderMediaSidebar sidebar = new();
        var lifetime = (CancellationTokenSource)typeof(ArchivePage).GetField("_lifetime", Fields)!.GetValue(page)!;
        int baseline = RegistrationCount(lifetime);
        int loads = 0;
        ArchiveAttachmentRow Row(int index) => new(new CapturedAttachment(index, "a" + index))
        {
            LoadStripThumbnail = (_, _, _) => { loads++; return Task.FromResult<Avalonia.Media.Imaging.Bitmap?>(null); }
        };
        ArchivePostRow post = new("post-a", "", "", [Row(0), Row(1)]);
        try
        {
            typeof(ArchivePage).GetField("_mediaSidebar", Fields)!.SetValue(page, sidebar);
            typeof(ArchivePage).GetField("_mediaSidebarShown", Fields)!.SetValue(page, true);
            for (int i = 0; i < 50; i++)
            {
                // Rebuild each cycle, as when refreshing the post's projection.
                typeof(ArchivePage).GetMethod("UpdateSidebarStrip", Fields)!.Invoke(page, [sidebar, post, false]);
                Dispatcher.UIThread.RunJobs();
                Assert.True(page.MediaSidebarStripTask.IsCompletedSuccessfully, page.MediaSidebarStripTask.Exception?.ToString());
            }
            Assert.Equal(100, loads);
            Assert.Equal(baseline, RegistrationCount(lifetime));
        }
        finally { page.EndLifetime(); }
    });
}
