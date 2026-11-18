using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// Targeted recovery coverage for the page-side query terms. A failed queue/catalog read must stop the
// filter from showing guessed results, but the error must expose one explicit retry that re-runs only
// the failed local read.
[Collection("Booru exact navigation")]
public sealed class MediaFilterRecoveryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void QueueFilterErrorOffersRetryAndRecoversWhenTheQueueBecomesReadable() => BooruComponentDispatcher.Run(() =>
    {
        MediaPage page = NewPage();
        try
        {
            MediaLibraryEntry entry = MediaTagCountsTests.Entry(1);
            Set(page, "_snapshot", new MediaLibrarySnapshot([entry], [], [], complete: true));
            bool unreadable = true;
            page.ReadTagQueue = () => unreadable
                ? throw new InvalidDataException("synthetic damaged queue")
                : new TagQueueSnapshot(1, 0, [], [], TagQueueLoadState.Ready);
            page.RefreshTagQueue();
            page.NameFilter.Text = "queue:yes";
            Assert.True(page.SubmitQuery());

            Assert.Contains("queue", page.FilterError!, StringComparison.OrdinalIgnoreCase);
            Assert.True(page.FilterRetryButton.IsVisible);
            Assert.Equal("Try again", page.FilterRetryButton.Content);

            unreadable = false;
            page.FilterRetryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.False(page.FilterRetryButton.IsVisible);
            Assert.Null(page.FilterError);
            Assert.Empty(page.ShownEntries);
        }
        finally { page.EndLifetime(); }
    });

    [Fact]
    public void OrdinaryFilterRefusalsDoNotOfferTheLocalDataRetry() => BooruComponentDispatcher.Run(() =>
    {
        MediaPage page = NewPage();
        try
        {
            page.FilterRetryButton.Content = "stale";
            Invoke(page, "ShowFilterError", "not a queue or booru read failure");
            Assert.True(page.FilterError != null);
            Assert.False(page.FilterRetryButton.IsVisible);
        }
        finally { page.EndLifetime(); }
    });

    private static MediaPage NewPage() => new(_ => throw new InvalidOperationException("The recovery fixture must not open archive data."));

    private static void Set(MediaPage page, string field, object value) =>
        typeof(MediaPage).GetField(field, Private)!.SetValue(page, value);

    private static object? Invoke(MediaPage page, string method, params object[] args) =>
        typeof(MediaPage).GetMethod(method, Private)!.Invoke(page, args);
}
