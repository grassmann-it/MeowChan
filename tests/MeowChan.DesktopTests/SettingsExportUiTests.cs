using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Platform.Storage;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class SettingsExportUiTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Fact]
    public void SnapshotDoesNotKeepSourceReferencesOrMaterializeSourceProfiles()
    {
        Config saved = new() { ArchiveTagFilterTagsPerList = 52, ShrinkWindowForBarebones = false };
        var original = saved.LayoutProfiles.ToArray();
        Config snapshot = SettingsExportUi.CaptureSavedSnapshot(saved);
        Assert.Equal(original, saved.LayoutProfiles.ToArray());
        saved.ArchiveTagFilterTagsPerList = 97;
        saved.ShrinkWindowForBarebones = true;
        saved.GetLayoutProfile("Simple").ColumnVisibility["synthetic-column"] = true;
        Assert.Equal(52, snapshot.ArchiveTagFilterTagsPerList);
        Assert.False(snapshot.ShrinkWindowForBarebones);
        Assert.False(snapshot.GetLayoutProfile("Simple").ColumnVisibility.ContainsKey("synthetic-column"));
    }

    [Fact]
    public async Task SaveDialogWritesOnlyItsReturnedFileFromFrozenSavedSettings()
    {
        Config saved = new() { UrlTextExportLimitMiB = 7, SlashTagSuggestionCount = 14, VideoScrubCacheLimitMiB = 256 };
        Config snapshot = SettingsExportUi.CaptureSavedSnapshot(saved);
        using var file = new MemoryFile();
        // Simulate a pre-existing longer file: overwrite must not leave trailing content.
        file.Bytes.Write(Enumerable.Repeat((byte)'X', 100_000).ToArray());
        var provider = new SaveProvider { Result = Task.FromResult<IStorageFile?>(file) };
        saved.UrlTextExportLimitMiB = 11;
        await SettingsExportUi.ExportSnapshotAsync(snapshot, provider, new DateTime(2026, 11, 14), CancellationToken.None);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, file.WriteCalls);
        Assert.True(file.Disposed);
        Assert.Null(file.WriteContext); // Task.Run does not inherit xUnit's/UI's SynchronizationContext.
        Assert.Equal("meowchan-settings-2026-11-14.json", provider.Options!.SuggestedFileName);
        JsonObject json = JsonNode.Parse(file.Bytes.ToArray())!.AsObject();
        Assert.Equal(7, json[nameof(Config.UrlTextExportLimitMiB)]!.GetValue<int>());
        Assert.Equal(14, json[nameof(Config.SlashTagSuggestionCount)]!.GetValue<int>());
        Assert.Equal(256, json[nameof(Config.VideoScrubCacheLimitMiB)]!.GetValue<int>());
        Assert.NotNull(json["ExportedBy"]);
        Assert.False(json.ContainsKey(nameof(Config.WindowWidth)));
        Assert.False(json.ContainsKey(nameof(Config.SourceMediaIntegrityPermissions)));
    }

    [Fact]
    public async Task CancelledDialogDoesNotWriteOrTurnCancellationIntoAnError()
    {
        var provider = new SaveProvider();
        await SettingsExportUi.ExportSnapshotAsync(new Config(), provider, DateTime.UnixEpoch, CancellationToken.None);
        Assert.Equal(1, provider.Calls);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SettingsExportUi.ExportSnapshotAsync(new Config(), provider, DateTime.UnixEpoch, cancellation.Token));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task ClosingWhilePickerIsOpenDisposesLateFileWithoutOpeningIt()
    {
        using var cancellation = new CancellationTokenSource();
        using var file = new MemoryFile();
        var choice = new TaskCompletionSource<IStorageFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new SaveProvider { Result = choice.Task };
        Task export = SettingsExportUi.ExportSnapshotAsync(new Config(), provider, DateTime.UnixEpoch, cancellation.Token);
        cancellation.Cancel();
        choice.SetResult(file);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export.WaitAsync(Watchdog));
        Assert.Equal(0, file.WriteCalls);
        Assert.True(file.Disposed);
    }

    [Fact]
    public async Task StartedWriteDrainsEvenWhenTheEditorCloses()
    {
        using var cancellation = new CancellationTokenSource();
        using var file = new MemoryFile { GateOpen = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var provider = new SaveProvider { Result = Task.FromResult<IStorageFile?>(file) };
        Task export = SettingsExportUi.ExportSnapshotAsync(new Config(), provider, DateTime.UnixEpoch, cancellation.Token);
        try
        {
            await file.OpenStarted.Task.WaitAsync(Watchdog);
            cancellation.Cancel();
            Assert.False(export.IsCompleted);
        }
        finally
        {
            file.GateOpen.TrySetResult();
            await export.WaitAsync(Watchdog);
        }
        Assert.True(file.Disposed);
        Assert.NotNull(JsonNode.Parse(file.Bytes.ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderOrWriteFailureIsNotRetriedAndNeverSelectsAnAlternateFile(bool duringWrite)
    {
        using var file = new MemoryFile { FailOpen = duringWrite };
        var provider = new SaveProvider
        {
            Result = duringWrite ? Task.FromResult<IStorageFile?>(file)
                : Task.FromException<IStorageFile?>(new IOException("synthetic private path"))
        };
        await Assert.ThrowsAsync<IOException>(() => SettingsExportUi.ExportSnapshotAsync(new Config(), provider, DateTime.UnixEpoch, CancellationToken.None));
        Assert.Equal(1, provider.Calls);
        Assert.Equal(duringWrite ? 1 : 0, file.WriteCalls);
        if (duringWrite) Assert.True(file.Disposed);
    }

    [Fact]
    public async Task UnavailableProviderFailsBeforePicking()
    {
        var provider = new SaveProvider { CanSave = false };
        await Assert.ThrowsAsync<NotSupportedException>(() => SettingsExportUi.ExportSnapshotAsync(new Config(), provider, DateTime.UnixEpoch, CancellationToken.None));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public void PickerContractHasJsonExtensionAndExplicitOverwritePromptRegardlessOfCurrentCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            FilePickerSaveOptions options = SettingsExportUi.SaveOptions(new DateTime(2026, 2, 3));
            Assert.Equal("meowchan-settings-2026-02-03.json", options.SuggestedFileName);
            Assert.Equal("json", options.DefaultExtension);
            Assert.Equal(true, options.ShowOverwritePrompt);
            Assert.Equal("*.json", Assert.Single(Assert.Single(options.FileTypeChoices!).Patterns!));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void EditorExportUsesConfirmedConfigNeverSavesDraftAndUsesLocalizedPathFreeBanner()
    {
        string transfer = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.Transfer.cs"));
        string editor = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.axaml.cs"));
        Assert.Contains("CaptureSavedSnapshot(_config)", transfer);
        Assert.Contains("TopLevel.GetTopLevel(this)?.StorageProvider", transfer);
        Assert.Contains("SetErrorMessage(\"settings.transfer.exportFailed\");", transfer);
        Assert.Contains("catch (OperationCanceledException)", transfer);
        Assert.Contains("EndSettingsTransferLifetime();", editor);
        Assert.Contains("_settingsExportCancellation?.Cancel();", editor);
        foreach (string forbidden in new[] { "SaveConfig(", "_applySettings(", "ApplyLimitsDraft(", "TryGetLocalPath(", "File.Open", "File.Write", "exception.Message", "exception.ToString", "Console.", "Logger.", "Log." })
            Assert.DoesNotContain(forbidden, transfer);
        Assert.All(SettingsTransferUiCatalog.Entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.English));
            Assert.False(string.IsNullOrWhiteSpace(entry.German));
        });
    }

    private sealed class SaveProvider : IStorageProvider
    {
        public bool CanOpen => false;
        public bool CanSave { get; init; } = true;
        public bool CanPickFolder => false;
        public int Calls { get; private set; }
        public FilePickerSaveOptions? Options { get; private set; }
        public Task<IStorageFile?> Result { get; init; } = Task.FromResult<IStorageFile?>(null);
        public Task<IStorageFile?> SaveFilePickerAsync(FilePickerSaveOptions options)
        {
            Calls++;
            Options = options;
            return Result;
        }
        public Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options) => throw new NotSupportedException();
        public Task<OpenFilePickerResult> OpenFilePickerWithResultAsync(FilePickerOpenOptions options) => throw new NotSupportedException();
        public Task<SaveFilePickerResult> SaveFilePickerWithResultAsync(FilePickerSaveOptions options) => throw new NotSupportedException();
        public Task<IReadOnlyList<IStorageFolder>> OpenFolderPickerAsync(FolderPickerOpenOptions options) => throw new NotSupportedException();
        public Task<IStorageBookmarkFile?> OpenFileBookmarkAsync(string bookmark) => throw new NotSupportedException();
        public Task<IStorageBookmarkFolder?> OpenFolderBookmarkAsync(string bookmark) => throw new NotSupportedException();
        public Task<IStorageFile?> TryGetFileFromPathAsync(Uri filePath) => throw new NotSupportedException();
        public Task<IStorageFolder?> TryGetFolderFromPathAsync(Uri folderPath) => throw new NotSupportedException();
        public Task<IStorageFolder?> TryGetWellKnownFolderAsync(WellKnownFolder wellKnownFolder) => throw new NotSupportedException();
    }

    // URI/Name/metadata APIs intentionally throw: the export must never query or reopen a path.
    private sealed class MemoryFile : IStorageFile
    {
        internal MemoryStream Bytes { get; } = new();
        internal bool Disposed { get; private set; }
        internal bool FailOpen { get; init; }
        internal int WriteCalls { get; private set; }
        internal SynchronizationContext? WriteContext { get; private set; }
        internal TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? GateOpen { get; init; }
        public string Name => throw new NotSupportedException();
        public Uri Path => throw new NotSupportedException();
        public bool CanBookmark => false;
        public void Dispose() => Disposed = true;
        public Task<Stream> OpenReadAsync() => throw new NotSupportedException();
        public async Task<Stream> OpenWriteAsync()
        {
            WriteCalls++;
            WriteContext = SynchronizationContext.Current;
            OpenStarted.TrySetResult();
            if (FailOpen) throw new IOException("synthetic private path");
            if (GateOpen != null) await GateOpen.Task.ConfigureAwait(false);
            return Bytes;
        }
        public Task<StorageItemProperties> GetBasicPropertiesAsync() => throw new NotSupportedException();
        public Task<string?> SaveBookmarkAsync() => throw new NotSupportedException();
        public Task<IStorageFolder?> GetParentAsync() => throw new NotSupportedException();
        public Task DeleteAsync() => throw new NotSupportedException();
        public Task<IStorageItem?> MoveAsync(IStorageFolder destination) => throw new NotSupportedException();
    }

    private static string Root(string relative, [CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative));
}
