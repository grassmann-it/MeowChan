using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.Persistence;
using MeowChan.PluginHost.Taggers;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class LocalTaggerModelWindowTests
{
    [Fact]
    public void WaitingForModelVerificationShowsCancelAndRefreshesTheDisplayedStatusInGerman()
        => BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            using TempDirectory directory = new();
            using ProfileLease lease = ProfileLease.Acquire(directory.Path);
            LocalTaggerModelStore store = new(lease);
            TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> read = store.WithVerifiedModelAsync(_ => release.Task, CancellationToken.None);

            LocalTaggerModelWindow window = new(store, CancellationToken.None);
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Local image tagger", window.Title);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.IsVisible && text.Text == Texts.T("localTagger.engineUnavailable"));
                Button cancel = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                    button => Equals(button.Content, Texts.T("plugins.cancel")));
                Assert.True(cancel.IsVisible);
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(SpinWait.SpinUntil(() =>
                {
                    Dispatcher.UIThread.RunJobs();
                    return !cancel.IsVisible;
                }, TimeSpan.FromSeconds(5)));
                Texts.SetLanguage("de");
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Lokaler Bildtagger", window.Title);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.IsVisible && text.Text == Texts.T("localTagger.cancelled"));
            }
            finally
            {
                window.Close();
                release.TrySetResult(true);
                BooruComponentDispatcher.Wait(read);
            }
        });
}
