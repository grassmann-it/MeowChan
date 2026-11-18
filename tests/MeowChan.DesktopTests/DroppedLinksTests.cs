using System.Text;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class DroppedLinksTests
{
    [Fact]
    public async Task DroppedTextIsForwardedUnchangedAndAdmittedLikeTypedText()
    {
        const string text = " https://boards.4chan.org/g/thread/1\r\nnot a URL, https://8kun.top/test/ \n";
        DroppedContent content = DroppedLinks.Read(Transfer(DataTransferItem.CreateText(text)));

        Assert.Equal(DroppedContentKind.Links, content.Kind);
        Assert.Equal(text, content.Text);
        // The same splitter as the Add line; explicit text never falls back to the clipboard.
        var admitted = new List<string>();
        await AddInput.SubmitAsync(content.Text, () => throw new InvalidOperationException("clipboard read"),
            entry => { admitted.Add(entry); return Task.CompletedTask; });
        Assert.Equal(new[] { "https://boards.4chan.org/g/thread/1", "not a URL", "https://8kun.top/test/" }, admitted);
    }

    [Fact]
    public void UnsupportedTextIsStillForwardedSoAdmissionReportsIt()
    {
        DroppedContent content = DroppedLinks.Classify("hello world", []);
        Assert.Equal(DroppedContentKind.Links, content.Kind);
        Assert.Equal("hello world", content.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n\t ")]
    public void WhitespaceIsEmptyAndNeverBecomesAnEmptyAddThatReadsTheClipboard(string? text)
    {
        Assert.Same(DroppedContent.Empty, DroppedLinks.Classify(text, []));
        Assert.Same(DroppedContent.Empty, DroppedLinks.Read(new DataTransfer()));
    }

    [Fact]
    public void FilesAndFoldersAreRefusedEvenWhenTheirPathsComeAsText()
    {
        string folder = Path.Combine(Path.GetTempPath(), "dropped folder " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "image.png");
            File.WriteAllText(file, "x");
            var transfer = Transfer(DataTransferItem.CreateFile(new FixtureFolder(new Uri(folder))),
                DataTransferItem.CreateFile(new FixtureFolder(new Uri(file))), DataTransferItem.CreateText(file));

            DroppedContent content = DroppedLinks.Read(transfer);

            Assert.Equal(DroppedContentKind.Files, content.Kind);
            Assert.Equal(2, content.FileCount);
            Assert.Null(content.Text);
            // A file: URI and an existing absolute path are files when they arrive as text.
            Assert.Equal(DroppedContentKind.Files, DroppedLinks.Classify(new Uri(file).AbsoluteUri + "\n" + file, []).Kind);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("/g/")]
    [InlineData("/rpt/")]
    [InlineData("/g/, /rpt/")]
    [InlineData("/definitely/not/here/a.png")]
    public void BoardShortTextAndUnknownPathsAreLinksNotFiles(string text)
    {
        // .NET on Unix would read these as implicit file:// URIs; they must reach the normal Add path.
        DroppedContent content = DroppedLinks.Classify(text, []);
        Assert.Equal(DroppedContentKind.Links, content.Kind);
        Assert.Equal(text, content.Text);
    }

    [Fact]
    public void UnixUriParsingReallyTreatsBoardShortTextAsAFileUri()
    {
        // Documents the .NET behaviour the classification above must not rely on.
        if (OperatingSystem.IsWindows()) return;
        Assert.True(Uri.TryCreate("/g/", UriKind.Absolute, out Uri? uri) && uri.IsFile);
    }

    [Fact]
    public void AnExistingAbsolutePathAsTextIsAFile()
    {
        string file = Path.Combine(Path.GetTempPath(), "dropped-" + Guid.NewGuid().ToString("N") + ".png");
        // A Windows drive path is recognized from its syntax even if the source disappears before
        // the drop handler runs. POSIX absolute text is classified as a file only once it exists.
        if (!OperatingSystem.IsWindows())
            Assert.Equal(DroppedContentKind.Links, DroppedLinks.Classify(file, []).Kind);
        File.WriteAllText(file, "x");
        try
        {
            DroppedContent content = DroppedLinks.Classify(file, []);
            Assert.Equal(DroppedContentKind.Files, content.Kind);
            Assert.Equal(1, content.FileCount);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("file:///home/x/a.png", 1)]
    [InlineData("FILE:///home/x/a.png\nfile:///home/x/b.png", 2)]
    [InlineData(@"C:\Users\x\a.png", 1)]
    [InlineData("C:/Users/x/a.png", 1)]
    [InlineData(@"\\server\share\a.png", 1)]
    public void FileUrisAndWindowsPathsAreFilesOnEveryPlatform(string text, int count)
    {
        DroppedContent content = DroppedLinks.Classify(text, []);
        Assert.Equal(DroppedContentKind.Files, content.Kind);
        Assert.Equal(count, content.FileCount);
    }

    [Fact]
    public void TextWithOnlySomeFileEntriesGoesToAdmissionLikeTypedText()
    {
        string text = "https://boards.4chan.org/g/thread/1\n" + new Uri(Path.Combine(Path.GetTempPath(), "a.png")).AbsoluteUri;
        DroppedContent content = DroppedLinks.Classify(text, []);
        Assert.Equal(DroppedContentKind.Links, content.Kind);
        Assert.Equal(text, content.Text);
    }

    [Fact]
    public void UriListIsReadWhenNoPlainTextIsOffered()
    {
        DataFormat<string> uriList = DataFormat.CreateStringPlatformFormat("text/uri-list");
        var transfer = Transfer(DataTransferItem.Create(uriList,
            "# dragged from a browser\r\nhttps://boards.4chan.org/g/thread/1\r\nhttps://boards.4chan.org/g/thread/2\r\n"));

        DroppedContent content = DroppedLinks.Read(transfer);

        Assert.Equal(DroppedContentKind.Links, content.Kind);
        Assert.Equal("https://boards.4chan.org/g/thread/1\nhttps://boards.4chan.org/g/thread/2", content.Text);
    }

    [Fact]
    public void PlainTextWinsOverRawUrlFormatsSoOneLinkIsNotAdmittedTwice()
    {
        DataFormat<byte[]> url = DataFormat.CreateBytesPlatformFormat("UniformResourceLocatorW");
        var item = new DataTransferItem();
        item.SetText("https://boards.4chan.org/g/thread/1");
        item.Set(url, Encoding.Unicode.GetBytes("https://other.invalid/\0"));

        Assert.Equal("https://boards.4chan.org/g/thread/1", DroppedLinks.Read(Transfer(item)).Text);
        Assert.Equal("https://other.invalid/",
            DroppedLinks.Read(Transfer(DataTransferItem.Create(url, Encoding.Unicode.GetBytes("https://other.invalid/\0")))).Text);
    }

    [Fact]
    public void RemoteStorageItemsAreLinksNotFiles()
    {
        var transfer = Transfer(DataTransferItem.CreateFile(new FixtureFolder(new Uri("https://boards.4chan.org/g/thread/1"))));
        DroppedContent content = DroppedLinks.Read(transfer);
        Assert.Equal(DroppedContentKind.Links, content.Kind);
        Assert.Equal("https://boards.4chan.org/g/thread/1", content.Text);
    }

    [Fact]
    public void UnrelatedFormatsAreEmpty()
    {
        DataFormat<string> html = DataFormat.CreateStringPlatformFormat("text/html");
        Assert.Same(DroppedContent.Empty,
            DroppedLinks.Read(Transfer(DataTransferItem.Create(html, "<a href=\"https://boards.4chan.org/g/thread/1\">x</a>"))));
    }

    [Theory]
    [InlineData(DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, DragDropEffects.Copy)]
    [InlineData(DragDropEffects.Link, DragDropEffects.Link)]
    [InlineData(DragDropEffects.Move, DragDropEffects.Copy)]
    [InlineData(DragDropEffects.Move | DragDropEffects.Link, DragDropEffects.Link)]
    public void DropsCopyOrLinkButNeverMove(DragDropEffects requested, DragDropEffects expected)
        => Assert.Equal(expected, DroppedLinks.AcceptedEffect(requested));

    private static DataTransfer Transfer(params DataTransferItem[] items)
    {
        var transfer = new DataTransfer();
        foreach (DataTransferItem item in items) transfer.Add(item);
        return transfer;
    }

    private sealed class FixtureFolder(Uri path) : IStorageFolder
    {
        public string Name => "Fixture";
        public Uri Path { get; } = path;
        public bool CanBookmark => false;
        public void Dispose() { }
        public Task<StorageItemProperties> GetBasicPropertiesAsync() => throw new NotSupportedException();
        public Task<string?> SaveBookmarkAsync() => throw new NotSupportedException();
        public Task<IStorageFolder?> GetParentAsync() => throw new NotSupportedException();
        public Task DeleteAsync() => throw new NotSupportedException();
        public Task<IStorageItem?> MoveAsync(IStorageFolder destination) => throw new NotSupportedException();
        public IAsyncEnumerable<IStorageItem> GetItemsAsync() => throw new NotSupportedException();
        public Task<IStorageFolder?> GetFolderAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFile?> GetFileAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFile?> CreateFileAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFolder?> CreateFolderAsync(string name) => throw new NotSupportedException();
    }
}
