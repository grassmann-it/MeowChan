using Avalonia.Platform.Storage;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class LocalFolderPickerTests
{
    [Fact]
    public async Task UnavailableAndCancelDoNotSynthesizePaths()
    {
        Assert.Equal(LocalFolderPickStatus.Unavailable, (await LocalFolderPicker.PickAsync(null, "Pick")).Status);
        var provider = new FixtureStorageProvider { CanPickFolder = false };
        Assert.Equal(LocalFolderPickStatus.Unavailable, (await LocalFolderPicker.PickAsync(provider, "Pick")).Status);
        Assert.Equal(0, provider.PickerCalls);
        provider.CanPickFolder = true;
        var cancelled = await LocalFolderPicker.PickAsync(provider, "Pick");
        Assert.Equal(LocalFolderPickStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.Path);
    }

    [Fact]
    public async Task AcceptsLocalUnicodePathAndDisposesEveryResult()
    {
        string path = Path.Combine(Path.GetTempPath(), "local folder ünicode 日本");
        var first = new FixtureStorageFolder(new Uri(path));
        var extra = new FixtureStorageFolder(new Uri("content://unexpected/provider"));
        var provider = new FixtureStorageProvider { Result = Task.FromResult<IReadOnlyList<IStorageFolder>>(new IStorageFolder[] { first, extra }) };
        var result = await LocalFolderPicker.PickAsync(provider, "Pick");
        Assert.Equal(LocalFolderPickStatus.Selected, result.Status);
        Assert.Equal(path, result.Path);
        Assert.True(first.Disposed && extra.Disposed);
    }

    [Fact]
    public async Task NonLocalAndFailureNeverReturnDisplayNameOrUri()
    {
        var remote = new FixtureStorageFolder(new Uri("content://fixture/remote"));
        var provider = new FixtureStorageProvider { Result = Task.FromResult<IReadOnlyList<IStorageFolder>>(new IStorageFolder[] { remote }) };
        var result = await LocalFolderPicker.PickAsync(provider, "Pick");
        Assert.Equal(LocalFolderPickStatus.NonLocal, result.Status);
        Assert.Null(result.Path);
        Assert.True(remote.Disposed);
        provider.Result = Task.FromException<IReadOnlyList<IStorageFolder>>(new IOException("Picker failure"));
        result = await LocalFolderPicker.PickAsync(provider, "Pick");
        Assert.Equal(LocalFolderPickStatus.Failed, result.Status);
        Assert.Equal("Picker failure", result.Error);
        Assert.Null(result.Path);
    }
    private sealed class FixtureStorageProvider : IStorageProvider
    {
        public bool CanOpen => false;
        public bool CanSave => false;
        public bool CanPickFolder { get; set; } = true;
        public int PickerCalls { get; private set; }
        public Task<IReadOnlyList<IStorageFolder>> Result { get; set; } = Task.FromResult<IReadOnlyList<IStorageFolder>>(Array.Empty<IStorageFolder>());
        public Task<IReadOnlyList<IStorageFolder>> OpenFolderPickerAsync(FolderPickerOpenOptions options)
        {
            Assert.False(options.AllowMultiple);
            PickerCalls++;
            return Result;
        }
        public Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options) => throw new NotSupportedException();
        public Task<OpenFilePickerResult> OpenFilePickerWithResultAsync(FilePickerOpenOptions options) => throw new NotSupportedException();
        public Task<IStorageFile?> SaveFilePickerAsync(FilePickerSaveOptions options) => throw new NotSupportedException();
        public Task<SaveFilePickerResult> SaveFilePickerWithResultAsync(FilePickerSaveOptions options) => throw new NotSupportedException();
        public Task<IStorageBookmarkFile?> OpenFileBookmarkAsync(string bookmark) => throw new NotSupportedException();
        public Task<IStorageBookmarkFolder?> OpenFolderBookmarkAsync(string bookmark) => throw new NotSupportedException();
        public Task<IStorageFile?> TryGetFileFromPathAsync(Uri filePath) => throw new NotSupportedException();
        public Task<IStorageFolder?> TryGetFolderFromPathAsync(Uri folderPath) => throw new NotSupportedException();
        public Task<IStorageFolder?> TryGetWellKnownFolderAsync(WellKnownFolder wellKnownFolder) => throw new NotSupportedException();
    }

    private sealed class FixtureStorageFolder : IStorageFolder
    {
        public FixtureStorageFolder(Uri path) => Path = path;
        public string Name => "Fixture folder";
        public Uri Path { get; }
        public bool CanBookmark => false;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
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
