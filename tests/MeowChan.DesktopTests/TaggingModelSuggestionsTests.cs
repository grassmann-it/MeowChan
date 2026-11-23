using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class TaggingModelSuggestionsTests
{
    [Fact]
    public async Task TheDesktopHostReadsStoredProposalsUsingTheQueueContentAndTheDefaultThreshold()
    {
        using TempDirectory directory = new();
        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        TagSuggestionStore store = new(host.ProfileLeaseForTests);
        TagSuggestionProvenance origin = new("test-adapter", "test-tagger", "1.2", "test-model", "r3");
        string hash = new('a', 64);
        store.CommitRuns([new(hash, 100, origin, TagSuggestionStore.TaggedOutcome,
            [new("strong", "General", 0.9), new("weak", "General", 0.2)])]);
        byte[] saved = File.ReadAllBytes(store.FilePath);

        var suggestions = await host.LoadTagSuggestionsAsync(hash, 100, CancellationToken.None);

        Assert.Equal("strong", Assert.Single(suggestions).Name);
        Assert.Empty(await host.LoadTagSuggestionsAsync(hash, 101, CancellationToken.None));
        Assert.Equal(saved, File.ReadAllBytes(store.FilePath));
        Assert.False(File.Exists(Path.Combine(directory.Path, PrivateTagStore.FileName)));
    }

    [Fact]
    public void TheDialogShowsTheRawTagItsScoreItsOriginAndItsReviewState()
        => BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            TagSuggestionProvenance origin = new("test-adapter", "test-tagger", "1.2", "test-model", "r3");
            StoredTagSuggestion row = new(1, new string('a', 64), 100, "long_hair", "Character", 0.876,
                origin, TagSuggestionState.Rejected, DateTimeOffset.UnixEpoch);
            Window dialog = TaggingPage.CreateSuggestionsDialog("selected.png", [row]);
            try
            {
                dialog.Show();
                Dispatcher.UIThread.RunJobs();
                dialog.UpdateLayout();
                Assert.Equal("Model suggestions", dialog.Title);
                string?[] texts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
                Assert.Contains("selected.png", texts);
                Assert.Contains("long_hair", texts);
                Assert.Contains(string.Join(" · ", Texts.T("booru.tagCategory.Character"), 0.876.ToString("P1", Texts.Culture), "Rejected"), texts);
                Assert.Contains("Model test-model, revision r3 · test-adapter 1.2 / test-tagger", texts);
            }
            finally { dialog.Close(); }
        });
}
