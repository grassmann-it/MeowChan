using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Platform.Storage;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
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

    [Theory]
    [InlineData(0)] // No profiles: even a transient materialization changes the dictionary version.
    [InlineData(1)] // Inactive only.
    [InlineData(2)] // Active and inactive.
    [InlineData(3)] // Unknown profile must survive without the normalizing Config setter.
    public void CaptureIsPureBeforeSerializationAndKeepsEveryLayoutValue(int count)
    {
        Config saved = new() { WorkspaceMode = "Advanced", GalleryOverlayControls = false, GalleryAutoHideControls = true };
        foreach (string name in new[] { "Simple", "Advanced", "Future" }.Take(count))
            saved.LayoutProfiles.Add(name, new PresentationProfile
            {
                ReadingFontSize = "future-font", ArchiveThreadLayout = "future-layout", ArchiveSplitWidth = 123,
                ColumnWidths = new() { ["future-column"] = 271 },
                ColumnVisibility = new() { ["future-column"] = false },
                ColumnOrder = new() { ["future-column"] = 3 },
            });
        var profiles = saved.LayoutProfiles;
        var before = profiles.ToArray();
        int version = DictionaryVersion(profiles);
        var columnVersions = before.ToDictionary(pair => pair.Key, pair =>
            (DictionaryVersion(pair.Value.ColumnWidths), DictionaryVersion(pair.Value.ColumnVisibility), DictionaryVersion(pair.Value.ColumnOrder)));
        using IEnumerator<KeyValuePair<string, PresentationProfile>> enumerator = profiles.GetEnumerator();

        Config snapshot = SettingsExportUi.CaptureSavedSnapshot(saved);

        // Check the capture itself, not just the eventual filtered JSON. Clear/restore cannot hide here.
        Assert.Same(profiles, saved.LayoutProfiles);
        Assert.Equal(version, DictionaryVersion(profiles));
        Assert.Equal(before, profiles.ToArray());
        Assert.Equal(count, snapshot.LayoutProfiles.Count);
        Assert.NotSame(profiles, snapshot.LayoutProfiles);
        Assert.False(snapshot.GalleryOverlayControls);
        Assert.True(snapshot.GalleryAutoHideControls);
        foreach ((string name, PresentationProfile original) in before)
        {
            Assert.True(enumerator.MoveNext());
            Assert.Same(original, enumerator.Current.Value);
            var copy = snapshot.LayoutProfiles[name];
            Assert.NotSame(original, copy);
            Assert.NotSame(original.ColumnWidths, copy.ColumnWidths);
            Assert.NotSame(original.ColumnVisibility, copy.ColumnVisibility);
            Assert.NotSame(original.ColumnOrder, copy.ColumnOrder);
            Assert.Equal("future-font", copy.ReadingFontSize);
            Assert.Equal("future-layout", copy.ArchiveThreadLayout);
            Assert.Equal(123d, copy.ArchiveSplitWidth);
            Assert.Equal(original.ColumnWidths, copy.ColumnWidths);
            Assert.Equal(original.ColumnVisibility, copy.ColumnVisibility);
            Assert.Equal(original.ColumnOrder, copy.ColumnOrder);
            Assert.Equal(columnVersions[name],
                (DictionaryVersion(original.ColumnWidths), DictionaryVersion(original.ColumnVisibility), DictionaryVersion(original.ColumnOrder)));
            copy.ColumnWidths["future-column"] = 999;
            copy.ColumnVisibility["future-column"] = true;
            copy.ColumnOrder["future-column"] = 9;
            Assert.Equal(271d, original.ColumnWidths["future-column"]);
            Assert.False(original.ColumnVisibility["future-column"]);
            Assert.Equal(3, original.ColumnOrder["future-column"]);
        }
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public void CaptureNeverCopiesConsentRuntimeStateOrEditorBaselines()
    {
        Config saved = new()
        {
            SourceMediaIntegrityPermissions = new() { ["consent-canary"] = new("owner-canary", "source-canary", true, 7) },
            FirstRunSetupPending = true, FirstRunSetupVersion = 42, WorkViewNoticePending = true,
            DownloadsPaused = true, LastExportDirectory = "directory-canary", GalleryFrameDirectory = "frames-canary",
            GalleryContextOpen = true, ArchiveBridgeHintSeen = true, StatusPageSegment = Config.StatusPageSegmentDiagnostics,
            WindowWidth = 1910, WindowHeight = 1080, WindowPositionX = 17, WindowPositionY = 29, WindowMaximized = true,
            SettingsBaselineReadingFontSize = "Large", SettingsBaselineProfiles = new() { ["baseline-canary"] = new() },
            PluginEditorArguments = ["{file}", "猫"], ShortcutBindings = new ShortcutBindingsSection().WithSingleKeyShortcuts(false),
        };
        var consent = saved.SourceMediaIntegrityPermissions;
        var arguments = saved.PluginEditorArguments;
        var shortcuts = saved.ShortcutBindings;

        Config snapshot = SettingsExportUi.CaptureSavedSnapshot(saved);

        Assert.Empty(snapshot.SourceMediaIntegrityPermissions);
        Assert.NotSame(consent, snapshot.SourceMediaIntegrityPermissions);
        Assert.False(snapshot.FirstRunSetupPending);
        Assert.Equal(0, snapshot.FirstRunSetupVersion);
        Assert.False(snapshot.WorkViewNoticePending);
        Assert.False(snapshot.DownloadsPaused);
        Assert.Empty(snapshot.LastExportDirectory);
        Assert.Empty(snapshot.GalleryFrameDirectory);
        Assert.False(snapshot.GalleryContextOpen);
        Assert.False(snapshot.ArchiveBridgeHintSeen);
        Assert.Equal(new Config().StatusPageSegment, snapshot.StatusPageSegment);
        Assert.Equal(new Config().WindowWidth, snapshot.WindowWidth);
        Assert.Equal(new Config().WindowHeight, snapshot.WindowHeight);
        Assert.Null(snapshot.WindowPositionX);
        Assert.Null(snapshot.WindowPositionY);
        Assert.False(snapshot.WindowMaximized);
        Assert.Null(snapshot.SettingsBaselineReadingFontSize);
        Assert.Null(snapshot.SettingsBaselineProfiles);
        Assert.NotSame(arguments, snapshot.PluginEditorArguments);
        Assert.Equal(arguments, snapshot.PluginEditorArguments);
        Assert.NotSame(shortcuts, snapshot.ShortcutBindings);
        Assert.False(snapshot.ShortcutBindings.SingleKeyShortcuts);
        Assert.Same(consent, saved.SourceMediaIntegrityPermissions);
        Assert.Same(arguments, saved.PluginEditorArguments);
        Assert.Same(shortcuts, saved.ShortcutBindings);
        Assert.Single(consent);
        Assert.True(saved.GalleryContextOpen);
    }

    [Fact]
    public void CaptureFailureDuringProfileCopyLeavesOriginalReferencesAndVersionIntact()
    {
        Config saved = new() { WorkspaceMode = "Advanced" };
        var original = new PresentationProfile { ColumnWidths = new() { ["Title"] = 271 } };
        saved.LayoutProfiles.Add("Simple", original);
        saved.LayoutProfiles.Add("broken", null!); // Fails only after the first profile has been copied.
        var profiles = saved.LayoutProfiles;
        var widths = original.ColumnWidths;
        int version = DictionaryVersion(profiles);
        using IEnumerator<KeyValuePair<string, PresentationProfile>> enumerator = profiles.GetEnumerator();

        Assert.Throws<NullReferenceException>(() => SettingsExportUi.CaptureSavedSnapshot(saved));

        Assert.Same(profiles, saved.LayoutProfiles);
        Assert.Equal(version, DictionaryVersion(profiles));
        Assert.Equal(new[] { "Simple", "broken" }, profiles.Keys);
        Assert.Same(original, profiles["Simple"]);
        Assert.Same(widths, original.ColumnWidths);
        Assert.Equal(271d, widths["Title"]);
        Assert.Null(profiles["broken"]);
        Assert.True(enumerator.MoveNext());
        Assert.Same(original, enumerator.Current.Value);
        Assert.True(enumerator.MoveNext());
        Assert.Null(enumerator.Current.Value);
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public void CaptureRejectsNullWithoutUsingConfigClone()
    {
        Assert.Throws<ArgumentNullException>(() => SettingsExportUi.CaptureSavedSnapshot(null!));
        Assert.Throws<ArgumentNullException>(() => SettingsTransfer.CreatePortableSnapshot(null!));
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
        Assert.Equal(1, file.DisposeCalls);
        Assert.Null(file.DisposeContext);
        Assert.Equal(new[] { "truncate", "write", "flush", "dispose" }, file.Bytes.Operations.Select(operation => operation.Name));
        Assert.All(file.Bytes.Operations, operation => Assert.Null(operation.Context));
        Assert.Equal(1, file.Bytes.DisposeCalls);
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
        Assert.Equal(1, file.DisposeCalls);
        Assert.Null(file.DisposeContext);
        Assert.Empty(file.Bytes.Operations);
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
    public async Task StartedStreamWriteOrFlushDrainsBeforeDisposingEvenAfterCancellation(bool duringFlush)
    {
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var file = new MemoryFile { Bytes = new ExportStream { GateWrite = duringFlush ? null : gate, GateFlush = duringFlush ? gate : null } };
        var provider = new SaveProvider { Result = Task.FromResult<IStorageFile?>(file) };
        Task export = SettingsExportUi.ExportSnapshotAsync(SettingsExportUi.CaptureSavedSnapshot(new Config()), provider, DateTime.UnixEpoch, cancellation.Token);
        try
        {
            await (duringFlush ? file.Bytes.FlushStarted : file.Bytes.WriteStarted).Task.WaitAsync(Watchdog);
            cancellation.Cancel();
            Assert.False(export.IsCompleted);
            Assert.False(file.Disposed);
            Assert.Equal(0, file.Bytes.DisposeCalls);
        }
        finally
        {
            gate.TrySetResult();
            await export.WaitAsync(Watchdog);
        }
        Assert.Equal(1, file.WriteCalls);
        Assert.Equal(1, file.DisposeCalls);
        Assert.Null(file.DisposeContext);
        Assert.Equal(1, file.Bytes.DisposeCalls);
        Assert.NotNull(JsonNode.Parse(file.Bytes.ToArray()));
        Assert.All(file.Bytes.Operations, operation => Assert.Null(operation.Context));
    }

    [Theory]
    [InlineData("truncate")]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("dispose")]
    public async Task StreamFailureDisposesBothResourcesWithoutRetryOrAlternateTarget(string failure)
    {
        using var file = new MemoryFile { Bytes = new ExportStream { Failure = failure } };
        var provider = new SaveProvider { Result = Task.FromResult<IStorageFile?>(file) };

        await Assert.ThrowsAsync<IOException>(() => SettingsExportUi.ExportSnapshotAsync(
            SettingsExportUi.CaptureSavedSnapshot(new Config()), provider, DateTime.UnixEpoch, CancellationToken.None));

        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, file.WriteCalls);
        Assert.Equal(1, file.DisposeCalls);
        Assert.Null(file.DisposeContext);
        Assert.Equal(1, file.Bytes.DisposeCalls);
        Assert.Equal(1, file.Bytes.Operations.Count(operation => operation.Name == failure));
        Assert.All(file.Bytes.Operations, operation => Assert.Null(operation.Context));
    }

    [Fact]
    public async Task SerializationFailureCannotOpenOrTruncateTheAuthorizedFileAndDoesNotMutateSavedConfig()
    {
        Config saved = new() { WorkspaceMode = "Advanced" };
        var profile = new PresentationProfile { ColumnWidths = new() { ["Title"] = double.NaN } };
        saved.LayoutProfiles.Add("Simple", profile);
        var profiles = saved.LayoutProfiles;
        int version = DictionaryVersion(profiles);
        using IEnumerator<KeyValuePair<string, PresentationProfile>> enumerator = profiles.GetEnumerator();
        Config snapshot = SettingsExportUi.CaptureSavedSnapshot(saved);
        using var file = new MemoryFile();
        byte[] originalBytes = Encoding.UTF8.GetBytes("original target contents");
        file.Bytes.Write(originalBytes);
        var provider = new SaveProvider { Result = Task.FromResult<IStorageFile?>(file) };

        await Assert.ThrowsAsync<ArgumentException>(() => SettingsExportUi.ExportSnapshotAsync(snapshot, provider, DateTime.UnixEpoch, CancellationToken.None));

        Assert.Equal(1, provider.Calls);
        Assert.Equal(0, file.WriteCalls);
        Assert.Equal(1, file.DisposeCalls);
        Assert.Null(file.DisposeContext);
        Assert.Equal(originalBytes, file.Bytes.ToArray());
        Assert.Empty(file.Bytes.Operations);
        Assert.Same(profiles, saved.LayoutProfiles);
        Assert.Equal(version, DictionaryVersion(profiles));
        Assert.Single(profiles);
        Assert.True(enumerator.MoveNext());
        Assert.Same(profile, enumerator.Current.Value);
        Assert.True(double.IsNaN(profile.ColumnWidths["Title"]));
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public async Task PendingPickerUsesFrozenDeepCopyAndNeverCarriesConsentToTheOutput()
    {
        Config saved = new()
        {
            WorkspaceMode = "Advanced", GalleryOverlayControls = false, GalleryAutoHideControls = true,
            PluginEditorArguments = ["{file}", "saved-value"],
            SourceMediaIntegrityPermissions = new() { ["consent-canary"] = new("owner-canary", "source-canary", true, 7) },
            LastExportDirectory = "directory-canary",
        };
        var profile = new PresentationProfile { ColumnWidths = new() { ["Title"] = 271 } };
        saved.LayoutProfiles.Add("Simple", profile);
        Config snapshot = SettingsExportUi.CaptureSavedSnapshot(saved);
        using var file = new MemoryFile();
        var choice = new TaskCompletionSource<IStorageFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new SaveProvider { Result = choice.Task };
        Task export = SettingsExportUi.ExportSnapshotAsync(snapshot, provider, DateTime.UnixEpoch, CancellationToken.None);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(0, file.WriteCalls);
        saved.PluginEditorArguments[1] = "later-value";
        saved.GalleryAutoHideControls = false;
        profile.ColumnWidths["Title"] = 999;
        saved.LayoutProfiles.Clear();
        choice.SetResult(file);
        await export.WaitAsync(Watchdog);

        JsonObject json = JsonNode.Parse(file.Bytes.ToArray())!.AsObject();
        Assert.Equal("saved-value", json[nameof(Config.PluginEditorArguments)]![1]!.GetValue<string>());
        Assert.Equal(271d, json[nameof(Config.LayoutProfiles)]!["Simple"]![nameof(PresentationProfile.ColumnWidths)]!["Title"]!.GetValue<double>());
        Assert.False(json[nameof(Config.GalleryOverlayControls)]!.GetValue<bool>());
        Assert.True(json[nameof(Config.GalleryAutoHideControls)]!.GetValue<bool>());
        Assert.DoesNotContain("canary", json.ToJsonString(), StringComparison.Ordinal);
        Assert.Empty(saved.LayoutProfiles); // Serialization must not materialize even the active profile here.
        Assert.Equal(1, file.DisposeCalls);
        Assert.Null(file.DisposeContext);
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
        Assert.Equal(duringWrite ? 1 : 0, file.DisposeCalls);
        if (duringWrite) Assert.Null(file.DisposeContext);
        Assert.Empty(file.Bytes.Operations);
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
        Assert.Contains("SettingsTransfer.CreatePortableSnapshot(saved)", transfer);
        Assert.True(transfer.IndexOf("CaptureSavedSnapshot(_config)", StringComparison.Ordinal) <
            transfer.IndexOf("await SettingsExportUi.ExportSnapshotAsync", StringComparison.Ordinal));
        Assert.DoesNotContain("saved.Clone()", transfer);
        Assert.DoesNotContain("saved.LayoutProfiles", transfer);
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

    // .NET dictionary version catches transient Add/Clear/reinsert that equal final bytes miss.
    private static int DictionaryVersion<TKey, TValue>(Dictionary<TKey, TValue> dictionary) where TKey : notnull =>
        (int)typeof(Dictionary<TKey, TValue>).GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dictionary)!;

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
        internal ExportStream Bytes { get; init; } = new();
        internal bool Disposed => DisposeCalls != 0;
        internal int DisposeCalls { get; private set; }
        internal bool FailOpen { get; init; }
        internal int WriteCalls { get; private set; }
        internal SynchronizationContext? WriteContext { get; private set; }
        internal SynchronizationContext? DisposeContext { get; private set; }
        internal TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? GateOpen { get; init; }
        public string Name => throw new NotSupportedException();
        public Uri Path => throw new NotSupportedException();
        public bool CanBookmark => false;
        public void Dispose()
        {
            DisposeCalls++;
            DisposeContext = SynchronizationContext.Current;
        }
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

    private sealed class ExportStream : MemoryStream
    {
        internal List<(string Name, SynchronizationContext? Context)> Operations { get; } = new();
        internal string? Failure { get; init; }
        internal int DisposeCalls { get; private set; }
        internal TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FlushStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? GateWrite { get; init; }
        internal TaskCompletionSource? GateFlush { get; init; }

        private void Record(string name)
        {
            Operations.Add((name, SynchronizationContext.Current));
            if (Failure == name) throw new IOException("synthetic private path");
        }

        public override void SetLength(long value)
        {
            Record("truncate");
            base.SetLength(value);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Record("write");
            WriteStarted.TrySetResult();
            if (GateWrite != null) await GateWrite.Task.ConfigureAwait(false);
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            Record("flush");
            FlushStarted.TrySetResult();
            if (GateFlush != null) await GateFlush.Task.ConfigureAwait(false);
            await base.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCalls++;
            // Also close the fixture stream when its synthetic async-dispose operation fails.
            try { Record("dispose"); }
            finally { Dispose(); }
            return ValueTask.CompletedTask;
        }
    }

    private static string Root(string relative, [CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative));
}
