using oChan.Desktop;

namespace oChan.DesktopTests;

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
}
