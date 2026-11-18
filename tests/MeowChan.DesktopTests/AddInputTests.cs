using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class AddInputTests
{
    [Fact]
    public async Task TextInputIsAdmittedOnceWithoutReadingClipboard()
    {
        const string input = "https://input.test/g/thread/123";
        int clipboardReads = 0;
        List<string> admitted = new List<string>();

        await AddInput.SubmitAsync(
            $"  {input} \t",
            () =>
            {
                clipboardReads++;
                return Task.FromResult<string?>("https://clipboard.test/g/thread/456");
            },
            url =>
            {
                admitted.Add(url);
                return Task.CompletedTask;
            });

        Assert.Equal(0, clipboardReads);
        Assert.Equal(input, Assert.Single(admitted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedListPassesInvalidEntriesAndDuplicatesInOrder(bool useClipboard)
    {
        const string thread = "https://boards.4chan.org/g/thread/123";
        const string alias = "https://boards.4channel.org/g/thread/123";
        const string board = "https://8kun.top/test/";
        string input = $"  {thread}, \r\ninvalid URL\r {alias}\n{board}, {thread}, ,\t\n";
        int clipboardReads = 0;
        List<string> admitted = new List<string>();

        await AddInput.SubmitAsync(
            useClipboard ? string.Empty : input,
            () =>
            {
                clipboardReads++;
                return Task.FromResult<string?>(input);
            },
            entry =>
            {
                admitted.Add(entry);
                return Task.CompletedTask;
            });

        Assert.Equal(useClipboard ? 1 : 0, clipboardReads);
        Assert.Equal(new[] { thread, "invalid URL", alias, board, thread }, admitted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task EmptyInputReadsClipboardOnceAndImportsOneUrl(string? input)
    {
        const string clipboardUrl = "https://clipboard.test/g/thread/456";
        int clipboardReads = 0;
        List<string> admitted = new List<string>();

        await AddInput.SubmitAsync(
            input,
            () =>
            {
                clipboardReads++;
                return Task.FromResult<string?>($" \r\n  {clipboardUrl}  \n");
            },
            url =>
            {
                admitted.Add(url);
                return Task.CompletedTask;
            });

        Assert.Equal(1, clipboardReads);
        Assert.Equal(clipboardUrl, Assert.Single(admitted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task EmptyClipboardDoesNotAdmitAnything(string? clipboardText)
    {
        int clipboardReads = 0;
        int admissions = 0;

        await AddInput.SubmitAsync(
            string.Empty,
            () =>
            {
                clipboardReads++;
                return Task.FromResult(clipboardText);
            },
            _ =>
            {
                admissions++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, clipboardReads);
        Assert.Equal(0, admissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAwaitsEachAdmissionBeforeStartingTheNext(bool useClipboard)
    {
        const string first = "https://clipboard.test/g/thread/123";
        const string second = "https://clipboard.test/g/thread/456";
        string entries = $" {first},\r\ninvalid URL\r{second}\n";
        TaskCompletionSource firstAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> admitted = new List<string>();
        int clipboardReads = 0;

        Task submission = AddInput.SubmitAsync(
            useClipboard ? string.Empty : entries,
            () =>
            {
                clipboardReads++;
                return Task.FromResult<string?>(entries);
            },
            url =>
            {
                admitted.Add(url);
                if (url != first)
                    return Task.CompletedTask;

                firstStarted.SetResult();
                return firstAdmission.Task;
            });

        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(submission.IsCompleted);
            Assert.Equal(first, Assert.Single(admitted));
        }
        finally
        {
            firstAdmission.TrySetResult();
        }

        await submission.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(useClipboard ? 1 : 0, clipboardReads);
        Assert.Equal(new[] { first, "invalid URL", second }, admitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparatorOnlyInputDoesNotAdmitAnEmptyEntry(bool useClipboard)
    {
        const string separators = " , \r\n,\r,\n\t ";
        int clipboardReads = 0;
        int admissions = 0;

        await AddInput.SubmitAsync(
            useClipboard ? string.Empty : separators,
            () =>
            {
                clipboardReads++;
                return Task.FromResult<string?>(separators);
            },
            _ =>
            {
                admissions++;
                return Task.CompletedTask;
            });

        Assert.Equal(useClipboard ? 1 : 0, clipboardReads);
        Assert.Equal(0, admissions);
    }

    [Fact]
    public async Task DanbooruAddressKeepsItsOwnCommasWhileOtherEntriesStillSplit()
    {
        const string search = "https://danbooru.donmai.us/posts?tags=cat+rating:g,s&page=2";
        const string post = "https://safebooru.donmai.us/posts/123?q=a,b";
        const string thread = "https://boards.4chan.org/g/thread/123";
        List<string> admitted = new List<string>();

        await AddInput.SubmitAsync(
            $"{thread},{search},{post}, {thread}\n{search}\r\nnot a url, {post},",
            () => Task.FromResult<string?>(null),
            entry =>
            {
                admitted.Add(entry);
                return Task.CompletedTask;
            });

        Assert.Equal(new[] { thread, search, post, thread, search, "not a url", post }, admitted);
    }

    [Fact]
    public void AddressesOnOtherHostsAreSplitAtCommasAsBefore()
    {
        Assert.Equal(new[] { "https://other.test/posts?tags=rating:g", "s" },
            AddInput.Split("https://other.test/posts?tags=rating:g,s"));
    }

    [Theory]
    [InlineData("https://danbooru.donmai.us/posts?tags=blue_sky cat", "https://danbooru.donmai.us/posts?tags=blue_sky cat")]
    [InlineData("https://danbooru.donmai.us/posts?tags=blue_sky   cat  solo", "https://danbooru.donmai.us/posts?tags=blue_sky   cat  solo")]
    public void UnencodedSpacesInADanbooruAddressKeepTheQueryInOneEntry(string line, string expected) =>
        Assert.Equal(new[] { expected }, AddInput.Split(line));

    [Fact]
    public void WordsAfterADanbooruAddressAndACommaOrAnotherUrlAreTheirOwnEntries()
    {
        const string first = "https://danbooru.donmai.us/posts?tags=blue_sky cat";
        Assert.Equal(new[] { first, "dog" }, AddInput.Split(first + ", dog"));
        Assert.Equal(new[] { first, "https://danbooru.donmai.us/posts/9" },
            AddInput.Split(first + " https://danbooru.donmai.us/posts/9"));
        Assert.Equal(new[] { first, "https://boards.4chan.org/g/thread/1" },
            AddInput.Split(first + "\nhttps://boards.4chan.org/g/thread/1"));
        // Other hosts keep splitting at commas and line breaks only, as before.
        Assert.Equal(new[] { "https://other.test/a b c" }, AddInput.Split("https://other.test/a b c"));
    }
}
