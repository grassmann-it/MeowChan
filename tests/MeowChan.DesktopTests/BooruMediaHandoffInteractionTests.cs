using System.Reflection;
using System.Runtime.CompilerServices;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// Unit/guard coverage for the real MediaPage handoff-interaction API. This deliberately does not
// construct a window or a page host: native routed-input delivery remains a shell/GUI acceptance hook.
public sealed class BooruMediaHandoffInteractionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void CapturedHandoffIsValidUntilARealInteractionIsNoted()
    {
        MediaPage page = NewPage(out CancellationTokenSource lifetime);
        using var lifetimeLease = lifetime;
        Func<bool> guard = page.CaptureBooruHandoffInteraction();

        Assert.True(guard());
        page.EndPendingReturn();
        Assert.False(guard());
        Assert.False(lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task CapturedHandoffStaysValidAcrossActivationBuildAndPresentationChanges()
    {
        MediaPage page = NewPage(out CancellationTokenSource lifetime);
        using var lifetimeLease = lifetime;
        Func<bool> guard = page.CaptureBooruHandoffInteraction();
        var activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> continuation = ContinueAfterAsync(activation.Task, guard);

        // A normal activation/build can replace all of these objects without being user takeover.
        Set(page, "_snapshot", Snapshot());
        SetProperty(page, "BuildTask", Task.FromResult(1));
        SetProperty(page, "PresentTask", Task.FromResult(2));
        SetProperty(page, "PresentCount", 1);
        SetProperty(page, "PresentRunCount", 1);
        Set(page, "_presentPending", true);
        using var building = new CancellationTokenSource();
        Set(page, "_building", building);

        activation.SetResult(true);
        Assert.True(await continuation);
        Assert.True(guard());
    }

    [Fact]
    public async Task UserInteractionRevokesAnAwaitingActivationButSnapshotBuildAndPresentChangesDoNot()
    {
        MediaPage page = NewPage(out CancellationTokenSource lifetime);
        using var lifetimeLease = lifetime;
        Func<bool> guard = page.CaptureBooruHandoffInteraction();
        var activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> continuation = ContinueAfterAsync(activation.Task, guard);

        Set(page, "_snapshot", Snapshot());
        SetProperty(page, "BuildTask", Task.CompletedTask);
        SetProperty(page, "PresentTask", Task.CompletedTask);
        SetProperty(page, "PresentCount", 3);
        SetProperty(page, "PresentRunCount", 2);
        Set(page, "_presentPending", false);
        NoteInteraction(page); // The same production note used by pointer/key/filter paths.

        activation.SetResult(true);
        Assert.False(await continuation);
        Assert.False(guard());
    }

    [Fact]
    public void SelectionStampAndPageLifetimeRevokeTheCapturedHandoff()
    {
        MediaPage page = NewPage(out CancellationTokenSource lifetime);
        using var lifetimeLease = lifetime;
        Func<bool> selectionGuard = page.CaptureBooruHandoffInteraction();
        Assert.True(selectionGuard());
        Set(page, "_selectionStamp", 1L);
        Assert.False(selectionGuard());

        Func<bool> lifetimeGuard = page.CaptureBooruHandoffInteraction();
        Assert.True(lifetimeGuard());
        lifetime.Cancel(); // The cancellation signal raised by the real EndLifetime path.
        Assert.False(lifetimeGuard());
    }

    [Fact]
    public void LocalizationRefreshDoesNotCountAsUserInteraction()
    {
        MediaPage page = NewPage(out CancellationTokenSource lifetime);
        using var lifetimeLease = lifetime;
        Func<bool> guard = page.CaptureBooruHandoffInteraction();

        Set(page, "_localizing", true);
        NoteInteraction(page);
        Assert.True(guard());

        Set(page, "_localizing", false);
        NoteInteraction(page);
        Assert.False(guard());
    }

    private static async Task<bool> ContinueAfterAsync(Task activation, Func<bool> guard)
    {
        await activation;
        return guard();
    }

    private static MediaPage NewPage(out CancellationTokenSource lifetime)
    {
        var page = (MediaPage)RuntimeHelpers.GetUninitializedObject(typeof(MediaPage));
        lifetime = new CancellationTokenSource();
        Set(page, "_lifetime", lifetime);
        return page;
    }

    private static MediaLibrarySnapshot Snapshot() =>
        new(Array.Empty<MediaLibraryEntry>(), Array.Empty<MediaLibraryThreadState>(), Array.Empty<string>(), true);

    private static void NoteInteraction(MediaPage page) =>
        typeof(MediaPage).GetMethod("NoteBooruHandoffInteraction", Private)!.Invoke(page, null);

    private static void Set(MediaPage page, string field, object? value) =>
        typeof(MediaPage).GetField(field, Private)!.SetValue(page, value);

    private static void SetProperty(MediaPage page, string property, object value) =>
        typeof(MediaPage).GetProperty(property, Private)!.SetValue(page, value);
}
