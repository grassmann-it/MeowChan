using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

/// <summary>
/// Real workbench action controls with delayed, injected local callbacks. These tests do not open a
/// window or call a provider; they exercise the view's async action lifetime and its existing notices.
/// </summary>
[Collection("Localization")]
public sealed class BooruLinkLifetimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string PostId = "42";
    private const string Url = "https://danbooru.donmai.us/posts/42";

    public BooruLinkLifetimeTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    [Fact]
    public async Task DisposingDuringShowInMediaNavigationSuppressesTheLateMissingNotice()
    {
        BooruUiHarness harness = new();
        using BooruWorkbenchModel model = new(harness.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        TaskCompletionSource<bool> started = NewSource<bool>();
        TaskCompletionSource<bool> release = NewSource<bool>();
        view.ShowInMedia = id =>
        {
            Assert.Equal(PostId, id);
            started.TrySetResult(true);
            return release.Task;
        };
        Button action = ShowInMediaButton(view);

        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        view.Dispose();
        release.SetResult(false);
        await EventuallyAsync(() => !LinkNote(view).IsVisible);

        Assert.False(LinkNote(view).IsVisible);
    }

    [Fact]
    public async Task ShowInMediaNavigationExceptionDoesNotEscapeTheAsyncClickHandler()
    {
        BooruUiHarness harness = new();
        using BooruWorkbenchModel model = new(harness.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        TaskCompletionSource<bool> invoked = NewSource<bool>();
        view.ShowInMedia = _ =>
        {
            invoked.TrySetResult(true);
            return Task.FromException<bool>(new InvalidOperationException("retired media page"));
        };
        Button action = ShowInMediaButton(view);

        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await EventuallyAsync(() => !LinkNote(view).IsVisible);

        // The test remains alive after the async-void continuation and no false "missing" result is shown.
        Assert.False(LinkNote(view).IsVisible);
    }

    [Fact]
    public async Task ANewNoticeInvalidatesAnOlderShowInMediaCompletion()
    {
        BooruUiHarness harness = new();
        using BooruWorkbenchModel model = new(harness.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        TaskCompletionSource<bool> started = NewSource<bool>();
        TaskCompletionSource<bool> release = NewSource<bool>();
        view.ShowInMedia = _ =>
        {
            started.TrySetResult(true);
            return release.Task;
        };
        Button action = ShowInMediaButton(view);

        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        view.AddTagToSearch("cat");
        string currentNotice = Texts.T("booru.ui.tags.addedToSearch", "cat");
        Assert.Equal(currentNotice, LinkNote(view).Text);

        release.SetResult(false);
        await EventuallyAsync(() => !LinkNote(view).IsVisible || LinkNote(view).Text == currentNotice);

        Assert.Equal(currentNotice, LinkNote(view).Text);
    }

    [Fact]
    public async Task DisposingDuringOpenOrCopySuppressesBothLateNotices()
    {
        BooruUiHarness harness = new();
        using BooruWorkbenchModel model = new(harness.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        TaskCompletionSource<bool> openStarted = NewSource<bool>();
        TaskCompletionSource<bool> copyStarted = NewSource<bool>();
        TaskCompletionSource<bool> openRelease = NewSource<bool>();
        TaskCompletionSource<bool> copyRelease = NewSource<bool>();
        harness.Links.OnOpen = _ =>
        {
            openStarted.TrySetResult(true);
            return openRelease.Task;
        };
        harness.Links.OnCopy = _ =>
        {
            copyStarted.TrySetResult(true);
            return copyRelease.Task;
        };

        Task open = InvokeLink(view, "OpenLinkAsync", Url);
        await openStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        view.Dispose();
        openRelease.SetResult(true);
        await open;
        Assert.False(LinkNote(view).IsVisible);

        // A disposed view rejects a later action before it reaches the injected link port.
        Task copy = InvokeLink(view, "CopyLinkAsync", Url);
        await copy;
        Assert.False(copyStarted.Task.IsCompleted);
        copyRelease.SetResult(true);
        Assert.False(LinkNote(view).IsVisible);
    }

    [Fact]
    public async Task OlderOpenCompletionCannotClearANewerCopyNotice()
    {
        BooruUiHarness harness = new();
        using BooruWorkbenchModel model = new(harness.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        TaskCompletionSource<bool> openStarted = NewSource<bool>();
        TaskCompletionSource<bool> copyStarted = NewSource<bool>();
        TaskCompletionSource<bool> openRelease = NewSource<bool>();
        TaskCompletionSource<bool> copyRelease = NewSource<bool>();
        harness.Links.OnOpen = _ =>
        {
            openStarted.TrySetResult(true);
            return openRelease.Task;
        };
        harness.Links.OnCopy = _ =>
        {
            copyStarted.TrySetResult(true);
            return copyRelease.Task;
        };

        Task open = InvokeLink(view, "OpenLinkAsync", Url);
        await openStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task copy = InvokeLink(view, "CopyLinkAsync", Url);
        await copyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        copyRelease.SetResult(true);
        await copy;
        string copied = Texts.T("booru.ui.link.copied", Url);
        Assert.Equal(copied, LinkNote(view).Text);

        openRelease.SetResult(true);
        await open;
        Assert.Equal(copied, LinkNote(view).Text);
    }

    [Fact]
    public async Task ExistingOpenAndCopyFailureNoticesRemainLocalAndExplicit()
    {
        BooruUiHarness harness = new();
        using BooruWorkbenchModel model = new(harness.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        harness.Links.OnOpen = _ => Task.FromException(new InvalidOperationException("browser unavailable"));
        harness.Links.OnCopy = _ => Task.FromException(new InvalidOperationException("clipboard unavailable"));

        await InvokeLink(view, "OpenLinkAsync", Url);
        Assert.Equal(Texts.T("booru.ui.link.openFailed"), LinkNote(view).Text);
        await InvokeLink(view, "CopyLinkAsync", Url);
        Assert.Equal(Texts.T("booru.ui.link.copyFailed"), LinkNote(view).Text);
    }

    private static Button ShowInMediaButton(BooruWorkbenchView view) =>
        (Button)(typeof(BooruWorkbenchView).GetMethod("CreateShowInMediaAction", Private)!
            .Invoke(view, new object[] { "test-show", "Show post 42", PostId })!);

    private static Task InvokeLink(BooruWorkbenchView view, string method, string url) =>
        (Task)(typeof(BooruWorkbenchView).GetMethod(method, Private)!.Invoke(view, new object[] { url })!);

    private static TextBlock LinkNote(BooruWorkbenchView view) =>
        Assert.Single(view.Footer.GetVisualDescendants().OfType<TextBlock>()
            .Where(control => control.Name == BooruWorkbenchView.LinkNoteName));

    private static TaskCompletionSource<T> NewSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for the view action continuation.");
            await Task.Delay(5);
        }
    }
}
