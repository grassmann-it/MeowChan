using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

    [Fact]
    public void DisposingDuringShowInMediaNavigationSuppressesTheLateMissingNotice() => OnDispatcher(async () =>
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
        await AwaitClickHandlersAsync();

        Assert.False(LinkNote(view).IsVisible);
    });

    [Fact]
    public void ShowInMediaNavigationExceptionShowsAnHonestActiveFailureNotice() => OnDispatcher(async () =>
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
        string failure = Texts.T("booru.crossLinks.navigationFailed", PostId);
        await AwaitClickHandlersAsync();

        Assert.Equal(failure, LinkNote(view).Text);
        Assert.NotEqual(Texts.T("booru.crossLinks.missingLocal", PostId), LinkNote(view).Text);
    });

    [Fact]
    public void AStaleShowInMediaExceptionCannotReplaceANewerNotice() => OnDispatcher(async () =>
    {
        BooruUiHarness harness = new();
        using BooruWorkbenchModel model = new(harness.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        TaskCompletionSource<bool> started = NewSource<bool>();
        TaskCompletionSource<bool> release = NewSource<bool>();
        view.ShowInMedia = async _ =>
        {
            started.TrySetResult(true);
            await release.Task;
            throw new InvalidOperationException("retired media page");
        };
        Button action = ShowInMediaButton(view);

        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        view.AddTagToSearch("cat");
        string currentNotice = Texts.T("booru.ui.tags.addedToSearch", "cat");
        Assert.Equal(currentNotice, LinkNote(view).Text);

        release.SetResult(true);
        await AwaitClickHandlersAsync();

        Assert.Equal(currentNotice, LinkNote(view).Text);
        Assert.NotEqual(Texts.T("booru.crossLinks.navigationFailed", PostId), LinkNote(view).Text);
        Assert.NotEqual(Texts.T("booru.crossLinks.missingLocal", PostId), LinkNote(view).Text);
    });

    [Fact]
    public void DisposingDuringShowInMediaExceptionSuppressesTheFailureNotice() => OnDispatcher(async () =>
    {
        BooruUiHarness harness = new();
        using BooruWorkbenchModel model = new(harness.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        TaskCompletionSource<bool> started = NewSource<bool>();
        TaskCompletionSource<bool> release = NewSource<bool>();
        view.ShowInMedia = async _ =>
        {
            started.TrySetResult(true);
            await release.Task;
            throw new InvalidOperationException("disposed media page");
        };
        Button action = ShowInMediaButton(view);

        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        view.Dispose();
        release.SetResult(true);
        await AwaitClickHandlersAsync();

        Assert.False(LinkNote(view).IsVisible);
        Assert.NotEqual(Texts.T("booru.crossLinks.navigationFailed", PostId), LinkNote(view).Text);
        Assert.NotEqual(Texts.T("booru.crossLinks.missingLocal", PostId), LinkNote(view).Text);
    });

    [Fact]
    public void DisposingDuringOpenOrCopySuppressesBothLateNotices() => OnDispatcher(async () =>
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
    });

    [Fact]
    public void OlderOpenCompletionCannotClearANewerCopyNotice() => OnDispatcher(async () =>
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
    });

    [Fact]
    public void ExistingOpenAndCopyFailureNoticesRemainLocalAndExplicit() => OnDispatcher(async () =>
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
    });

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

    // One actual Avalonia dispatcher owns creation, awaited continuations and assertions. No
    // application, platform renderer, native window, X server or user's profile is involved.
    private static void OnDispatcher(Func<Task> test)
    {
        FieldInfo primary = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previousDispatcher = primary.GetValue(null);
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        string language = Texts.Language;
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        try
        {
            primary.SetValue(null, owner);
            SynchronizationContext.SetSynchronizationContext(new ActionContext(owner));
            Texts.Initialize();
            Texts.SetLanguage("en");
            Task pending = test();
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!pending.IsCompleted)
            {
                owner.RunJobs();
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Workbench test dispatcher did not finish.");
                if (!pending.IsCompleted) Thread.Sleep(1);
            }
            pending.GetAwaiter().GetResult();
        }
        finally
        {
            Texts.SetLanguage(language);
            SynchronizationContext.SetSynchronizationContext(previousContext);
            primary.SetValue(null, previousDispatcher);
        }
    }

    // async-void Click handlers announce completion through SynchronizationContext. Wait for
    // that causal completion, rather than checking an unchanged notice before the handler ran.
    private static async Task AwaitClickHandlersAsync()
    {
        ActionContext context = Assert.IsType<ActionContext>(SynchronizationContext.Current);
        while (context.PendingOperations != 0) await Task.Delay(1);
    }

    private sealed class ActionContext(Dispatcher owner) : SynchronizationContext
    {
        internal int PendingOperations { get; private set; }
        public override void OperationStarted() => PendingOperations++;
        public override void OperationCompleted() => PendingOperations--;
        public override void Post(SendOrPostCallback callback, object? state) => owner.Post(() =>
        {
            SynchronizationContext? previous = Current;
            SetSynchronizationContext(this);
            try { callback(state); }
            finally { SetSynchronizationContext(previous); }
        });
    }
}
