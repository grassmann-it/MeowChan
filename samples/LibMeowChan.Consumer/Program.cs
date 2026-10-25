using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowChan;
using MeowChan.Boards.Base;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace LibMeowChan.Consumer;

internal static class Program
{
    private static async Task Main()
    {
        string directory = PhysicalWorkspaceDirectory(Directory.CreateTempSubdirectory("libmeowchan-consumer-").FullName);
        using DemoHandler handler = new DemoHandler();
        using HttpClient transport = new HttpClient(handler);

        try
        {
            // New providers, queues, stores and sessions share only the destination.
            // The second session must recover the committed medium without HTTP.
            await RunSessionAsync(transport, handler, directory, run: 1);
            await RunSessionAsync(transport, handler, directory, run: 2);
            if (handler.RequestCount != 1)
                throw new InvalidOperationException("Restoring verified media issued another request.");

            using (ProfileLease lease = ProfileLease.Acquire(Path.Combine(directory, "profile")))
                CheckSavedWork(new WorkStateStore(lease.ProfileDirectory, lease), directory, generation: 2);

            await CheckIndependentArchiveAsync(directory);
            await LegacyIntegrityConsumerChecks.VerifyAsync(transport, directory);
            Console.WriteLine("Package consumer passed: durable work, verified media restore, passive gallery, independent archive, export update/history, and clean shutdown.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Resolve only this sample's own temporary workspace. Caller-supplied export targets stay subject to the library's strict link guard.
    private static string PhysicalWorkspaceDirectory(string directory)
    {
        string full = Path.GetFullPath(directory);
        string root = Path.GetPathRoot(full)!;
        string physical = root;
        foreach (string component in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            physical = Path.Combine(physical, component);
            FileSystemInfo? target = new DirectoryInfo(physical).ResolveLinkTarget(returnFinalTarget: true);
            if (target != null) physical = target.FullName;
        }
        return Path.TrimEndingDirectorySeparator(physical);
    }

    private static async Task CheckIndependentArchiveAsync(string directory)
    {
        string profile = Path.Combine(directory, "profile");
        ResourceId id = DemoPayload.Resource;
        MediaProviderMetadata provider = DemoPayload.Provider;
        const string capturedBody = "Package-only offline post<a href=\"#1\">&gt;&gt;1</a>";
        string mediaDirectory = StorageLayout.GetThreadDirectory(directory, id.Source, id.Board, id.Thread!);
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            await using ThreadContentRuntime content = new ThreadContentRuntime(profile, lease);
            await content.InitializeAsync();
            await content.Catalog.RegisterThreadAsync(new ArchiveThreadRegistration(id,
                "https://demo.invalid/demo/thread/1", "Offline package archive", mediaDirectory, true));
            await content.Catalog.CaptureAsync(new CapturedThreadSnapshot(id,
                "https://demo.invalid/demo/thread/1", "Offline package archive", ThreadSourceState.Archived,
                DateTimeOffset.UtcNow, mediaDirectory, new[]
                {
                    new CapturedPost("1", 0, PostFieldPresence.Html | PostFieldPresence.Attachments, Html: capturedBody,
                        // Capture the same observed revision that the queue already committed.
                        // A newly captured attachment never inherits a legacy mediaId-only association.
                        Attachments: new[] { new CapturedAttachment(DemoPayload.Reference.Position, provider.MediaId,
                            provider.Variant, Url: provider.Url, OriginalFileName: provider.OriginalFileName,
                            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
                        {
                            ExpectedSha256 = provider.ExpectedSha256, ExpectedSha1 = provider.ExpectedSha1, ProviderSizeHint = provider.ProviderSizeHint,
                            AttachmentRevisionKey = DemoPayload.Reference.RevisionKey
                        } })
                }));
        }
        // The reader outlives the writer and profile lease, with no provider or UI dependency.
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchiveThreadSummary summary = await reader.ReadThreadSummaryAsync(id)
            ?? throw new InvalidOperationException("Saved thread metadata is missing.");
        ArchivedThreadView saved = await reader.ReadThreadAsync(id)
            ?? throw new InvalidOperationException("The packaged catalog lost its thread.");
        if (saved.PostCount != 1 || saved.GetPosts(0, 1).Single().Html != capturedBody
            || saved.Summary.SourceState != ThreadSourceState.Archived)
            throw new InvalidOperationException("The packaged independent reader did not recover the catalog.");
        ArchiveThreadQuery query = new ArchiveThreadQuery("PACKAGE-ONLY", Source: "demo", Board: "demo", HasPosts: true);
        ArchiveThreadPage matches = await reader.SearchThreadsAsync(query);
        ArchivePostId postId = new ArchivePostId(id, "1");
        ArchiveQuote quote = (await reader.GetQuotesAsync(postId)).Items.Single();
        ArchiveQuoteResolution resolution = await reader.ResolveQuoteAsync(quote);
        if (matches.TotalCount != 1 || matches.Items.Single().Id != id || resolution.State != ArchiveQuoteState.Captured
            || resolution.PostIndex != 0 || (await reader.GetBacklinksAsync(postId)).TotalCount != 1)
            throw new InvalidOperationException("The packaged indexed search/quote APIs did not resolve retained local content.");
        // Selection spans the full query, independently of the displayed search page.
        if (summary.Id != saved.Summary.Id || summary.Revision != saved.Summary.Revision
            || (await reader.GetBacklinkCountsAsync(id, new[] { postId.PostId }))[postId.PostId] != 1)
            throw new InvalidOperationException("Passive summary/backlink counts differ from saved content.");
        IReadOnlyList<ResourceId> selectedIds = await reader.SelectThreadIdsAsync(query);
        if (selectedIds.Count != 1 || selectedIds.Single() != id)
            throw new InvalidOperationException("Query selection did not retain the found thread identity.");
        foreach (ResourceId selectedId in selectedIds)
        {
            ArchivedThreadView selected = await reader.ReadThreadAsync(selectedId)
                ?? throw new InvalidOperationException("A selected thread disappeared.");
            List<MediaAssetSnapshot> manifests = new List<MediaAssetSnapshot>();
            foreach (string savedDirectory in await reader.GetMediaDirectoriesAsync(selectedId))
                manifests.Add(await MediaAssetReader.OpenAsync(savedDirectory));
            GalleryThreadResult gallery = GallerySelection.Build(selected, manifests);
            GalleryEntry entry = gallery.Entries.Single();
            if (gallery.Warnings.Count != 0 || entry.Key.Thread != selectedId || entry.Unassigned || entry.Historical
                || entry.Post?.PostId != "1" || entry.Attachment?.Position != 0)
                throw new InvalidOperationException("The passive gallery lost its captured attachment association.");
            MediaAssetReference exactAsset = entry.Manifest.FindById(entry.Key.AssetId)
                ?? throw new InvalidOperationException("The gallery asset identity was not in its manifest.");
            MediaAttachmentBinding binding = entry.AttachmentResolution?.Binding
                ?? throw new InvalidOperationException("The public acquisition did not produce an exact attachment binding.");
            if (entry.AttachmentResolution!.IsLegacy || binding.Reference != DemoPayload.Reference
                || binding.AcquisitionRevisionKey != DemoPayload.AcquisitionRevisionKey
                || binding.AssetId != exactAsset.AssetId || binding.Provider != provider
                || binding.Acceptance.Kind != MediaAcceptanceKind.Strict
                || binding.Acceptance.SizeMismatch || binding.Acceptance.Md5Mismatch
                || binding.Actual.Length != DemoPayload.Bytes.Length || binding.Actual.Md5 != DemoPayload.Md5
                || !string.Equals(binding.Actual.Sha256, DemoPayload.Sha256, StringComparison.OrdinalIgnoreCase)
                || entry.Attachment?.AttachmentRevisionKey != DemoPayload.Reference.RevisionKey
                || entry.Attachment?.ExpectedSha256 != DemoPayload.Sha256
                || entry.Attachment?.ExpectedSha1 != DemoPayload.Sha1
                || entry.Attachment?.ProviderSizeHint != DemoPayload.Provider.ProviderSizeHint
                || binding.Actual.Sha1 != DemoPayload.Sha1 || exactAsset.Sha1 != DemoPayload.Sha1)
                throw new InvalidOperationException("The passive gallery did not retain the strict provider/actual revision binding.");
            await using Stream bytes = await entry.Manifest.OpenVerifiedReadAsync(exactAsset.AssetId);
            using MemoryStream copy = new MemoryStream();
            await bytes.CopyToAsync(copy);
            if (!copy.ToArray().AsSpan().SequenceEqual(DemoPayload.Bytes))
                throw new InvalidOperationException("The exact gallery asset did not recover the verified bytes.");
        }

        await ArchiveQueryConsumerChecks.VerifyAsync(reader, id, profile, directory);

        ArchiveHtmlExportResult exported = await ArchiveHtmlExporter.ExportAsync(reader, id, Path.Combine(directory, "html-export"));
        if (exported.PostCount != 1 || exported.FileCount != 1 || exported.Omissions.Count != 0
            || !File.Exists(exported.IndexPath) || !File.Exists(Path.Combine(exported.Directory, "style.css")))
            throw new InvalidOperationException("The packaged portable HTML export did not publish a complete local folder.");

        ArchiveExportInspection before = await ArchiveHtmlExporter.InspectAsync(exported.Directory, id);
        if (before.Metadata.Generation != 1 || before.Metadata.ExportId == Guid.Empty || before.RequiresBackup
            || string.IsNullOrEmpty(before.ObservedFingerprint)
            || !File.Exists(Path.Combine(exported.Directory, ArchiveExportMetadataFile.Name)))
            throw new InvalidOperationException("The new export marker did not identify a clean first generation.");
        ArchiveHtmlExportResult updated = await ArchiveHtmlExporter.UpdateAsync(reader, id, exported.Directory,
            before.Metadata.ExportId, before.Metadata.Generation, expectedObservedFingerprint: before.ObservedFingerprint);
        ArchiveExportInspection after = await ArchiveHtmlExporter.InspectAsync(updated.Directory, id);
        if (updated.Metadata == null || updated.Metadata.ExportId != before.Metadata.ExportId
            || updated.Metadata.Generation != before.Metadata.Generation + 1
            || after.Metadata.ExportId != updated.Metadata.ExportId || after.Metadata.Generation != updated.Metadata.Generation
            || after.RequiresBackup || updated.BackupDirectory != null || updated.PostCount != 1 || updated.FileCount != 1)
            throw new InvalidOperationException("The guarded export update did not publish the next clean generation.");
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            ExportHistoryStore history = new ExportHistoryStore(profile, lease);
            ArchiveExportOperationResult recorded = await history.RecordPublishedAsync(updated);
            if (!recorded.HistoryRecorded || recorded.HistoryError != null
                || recorded.Metadata.ExportId != after.Metadata.ExportId || recorded.Metadata.Generation != 2)
                throw new InvalidOperationException("The published export was not associated with its profile history.");
        }
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            // A fresh history store recognizes the marker after the recording lease has closed.
            ExportHistoryStore reopened = new ExportHistoryStore(profile, lease);
            var recognized = (await reopened.ListVerifiedAsync(id)).Single();
            if (recognized.Inspection == null || recognized.Entry.ExportId != before.Metadata.ExportId
                || recognized.Entry.Generation != 2 || recognized.Inspection.Metadata.Generation != 2
                || recognized.Entry.Destination != updated.Directory)
                throw new InvalidOperationException("Reopened export history did not recognize the published marker.");
        }
    }

    private static async Task RunSessionAsync(HttpClient transport, DemoHandler handler, string directory, int run)
    {
        string profile = Path.Combine(directory, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        WorkStateStore work = new WorkStateStore(profile, lease);
        if (run > 1) CheckSavedWork(work, directory, generation: run - 1);
        DemoProvider provider = new DemoProvider(transport);
        MediaStore store = new MediaStore();
        DownloadQueue queue = new DownloadQueue(2, 1024 * 1024, store);
        await using Registry session = new Registry(
            new IImageBoard[] { provider }, queue, directory,
            TimeSpan.FromHours(1), boardIntervalSeconds: 3600, persistSession: false, workStore: work);

        TaskCompletionSource<ThreadSnapshot> completedNotification = new TaskCompletionSource<ThreadSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveSession()
        {
            try
            {
                ThreadSnapshot? observed = session.GetSnapshot().Threads.SingleOrDefault();
                if (observed?.CompletedMedia == 1 && observed.Downloads.Queued == 0 && observed.Downloads.Active == 0
                    && observed.Work is { SavedMedia: 1 } currentWork && currentWork.Media.Count == 1
                    && currentWork.Media[0].Generation == run && currentWork.Media[0].Status == MediaWorkStatus.Succeeded)
                    completedNotification.TrySetResult(observed);
            }
            catch (Exception exception)
            {
                completedNotification.TrySetException(exception);
            }
        }

        // Subscribe before admission so a fast manifest restore cannot hide its final invalidation.
        session.StateChanged += ObserveSession;
        try
        {
            AdmissionResult admission = await session.AddAsync("https://demo.invalid/demo/thread/1");
            if (admission.Status != AdmissionStatus.Added || admission.Thread is not DemoThread thread)
                throw new InvalidOperationException("The custom provider's thread was not admitted.");

            // AddAsync starts monitoring. The queue may still be idle until its first poll.
            await thread.FirstPollEnqueued.WaitAsync(TimeSpan.FromSeconds(10));
            await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
            string destination = Path.Combine(thread.DownloadDirectory, "1.bin");
            byte[] saved = await File.ReadAllBytesAsync(destination);
            if (!saved.AsSpan().SequenceEqual(DemoPayload.Bytes) || !thread.IsMediaDownloaded("1"))
                throw new InvalidOperationException("The download did not produce verified media.");
            // Queue idle fences durable commits; the event separately proves that the
            // public notification delivers the refreshed work projection to a consumer.
            ThreadSnapshot snapshot = await completedNotification.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (snapshot.KnownMedia != 1 || snapshot.CompletedMedia != 1 || snapshot.MediaFailures.Count != 0
                || snapshot.Downloads.Queued != 0 || snapshot.Downloads.Active != 0)
                throw new InvalidOperationException("The exposed progress does not match the saved medium.");

            long expectedReceivedBytes = run == 1 ? DemoPayload.Bytes.Length : 0;
            if (queue.ReceivedMediaBytes != expectedReceivedBytes || session.ReceivedMediaBytes != expectedReceivedBytes)
                throw new InvalidOperationException("Public transfer counters must count received payload bytes and exclude restored media.");
            CheckSavedWork(work, directory, generation: run);
            ConsumerProgress progress = new ConsumerProgress(run, snapshot, saved.Length, handler.RequestCount, session.ReceivedMediaBytes);
            JsonSerializerOptions jsonOptions = new JsonSerializerOptions();
            jsonOptions.Converters.Add(new JsonStringEnumConverter());
            Console.WriteLine(JsonSerializer.Serialize(progress, jsonOptions));

            await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (session.Threads.Count != 0 || session.Boards.Count != 0)
                throw new InvalidOperationException("Stopped watches remained active.");
        }
        finally
        {
            session.StateChanged -= ObserveSession;
        }
    }

    private static void CheckSavedWork(WorkStateStore work, string directory, long generation)
    {
        WorkResource intent = work.LoadResources().Single();
        MediaWork medium = work.LoadMedia(intent.Url, intent.Generation).Single();
        string destination = StorageLayout.GetMediaPath(directory, "demo", "demo", "1", "1", ".bin");
        if (intent.Url != "https://demo.invalid/demo/thread/1" || !intent.IsThread
            || intent.OutputRoot != directory || intent.Generation != generation || intent.Intent != WorkIntent.Watching
            || medium.Generation != generation || medium.ResourceUrl != intent.Url || medium.MediaIdentifier != "1"
            || medium.DestinationPath != destination || medium.DownloadUrl != "https://demo.invalid/media/1.bin"
            || medium.ExpectedMd5 != DemoPayload.Md5 || medium.ExpectedSize != DemoPayload.Bytes.Length
            || medium.ExpectedSha1 != DemoPayload.Sha1 || medium.ProviderSizeHint != DemoPayload.Provider.ProviderSizeHint
            || medium.ExpectedSha256 != DemoPayload.Sha256 || medium.AcquisitionRevisionKey != DemoPayload.AcquisitionRevisionKey
            || medium.AttachmentReferences.Count != 1 || medium.AttachmentReferences.Single() != DemoPayload.Reference
            || string.IsNullOrEmpty(medium.CommittedAssetId)
            || medium.OriginalFileName != "sample.bin" || medium.Status != MediaWorkStatus.Succeeded
            || medium.LastAttemptAt == null || medium.DueAt != null || medium.Failure != null)
            throw new InvalidOperationException("The durable intent or retry descriptor did not survive with its verified outcome.");
    }

    private sealed record ConsumerProgress(int Run, ThreadSnapshot Thread, int SavedBytes, int MediaRequests, long ReceivedMediaBytes);
}

internal static class DemoPayload
{
    public static readonly byte[] Bytes = Encoding.UTF8.GetBytes("libmeowchan standalone consumer\n");
    public static readonly string Md5 = Convert.ToBase64String(MD5.HashData(Bytes));
    public static readonly string Sha1 = Convert.ToBase64String(SHA1.HashData(Bytes));
    public static readonly string Sha256 = Convert.ToHexString(SHA256.HashData(Bytes));
    // Freeze the observed provider metadata and all post references before enqueueing.
    // These are data identities, not a permission to accept provider discrepancies.
    public static readonly ResourceId Resource = new ResourceId("demo", "demo", "1");
    public static readonly MediaProviderMetadata Provider = new MediaProviderMetadata("1", "original",
        "https://demo.invalid/media/1.bin", "sample.bin", Bytes.Length, Md5, Sha256)
        { ExpectedSha1 = Sha1, ProviderSizeHint = new MediaProviderSizeHint("2.4e1", "KiB") };
    public static readonly string AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(Resource, Provider);
    public static readonly AttachmentRevisionReference Reference = new AttachmentRevisionReference(Resource, "1", 0,
        MediaRevisionKeys.CreateAttachmentKey(Resource, "1", 0, Provider));
}

internal sealed class DemoHandler : HttpMessageHandler
{
    private int _requests;
    public int RequestCount => Volatile.Read(ref _requests);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method != HttpMethod.Get || request.RequestUri?.AbsoluteUri != "https://demo.invalid/media/1.bin")
            throw new InvalidOperationException("The offline example received an unexpected HTTP request.");

        Interlocked.Increment(ref _requests);
        HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(DemoPayload.Bytes)
        };
        return Task.FromResult(response);
    }
}

internal sealed class DemoProvider : BaseImageBoard
{
    public DemoProvider(HttpClient transport) : base(transport) { }
    public override string Name => "demo";
    public override string NiceName => "Offline example";
    public override Uri BaseUri => new Uri("https://demo.invalid/");
    public override bool CanHandle(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort && uri.Host == "demo.invalid" && string.IsNullOrEmpty(uri.UserInfo);
    public override bool IsThreadUri(Uri uri) => CanHandle(uri) && uri.AbsolutePath == "/demo/thread/1";
    public override bool IsBoardUri(Uri uri) => CanHandle(uri) && uri.AbsolutePath == "/demo/";
    public override IBoard GetBoard(Uri uri) => new DemoBoard(this);
    public override IThread GetThread(Uri uri) => new DemoThread(new DemoBoard(this));
}

internal sealed class DemoBoard : BaseBoard
{
    public DemoBoard(IImageBoard provider) => ImageBoard = provider;
    public override IImageBoard ImageBoard { get; }
    public override string BoardCode => "demo";
    public override string Name { get; protected set; } = "Demo board";
    public override string NiceName => Name;
    public override Uri BoardUri => new Uri(ImageBoard.BaseUri, "/demo/");

    public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<IThread> threads = new IThread[] { new DemoThread(this) };
        return Task.FromResult(threads);
    }
}

internal sealed class DemoThread : BaseThread
{
    private readonly TaskCompletionSource _firstPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public DemoThread(IBoard board) => Board = board;
    public Task FirstPollEnqueued => _firstPoll.Task;
    public override IBoard Board { get; }
    public override string ThreadId => "1";
    public override string NiceName => "Demo thread";
    public override Uri ThreadUri => new Uri(Board.ImageBoard.BaseUri, "/demo/thread/1");

    public override Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Title = "Package-only custom provider";
            TotalMediaCount = 1;
            Activity = ThreadActivity.Downloading;
            string path = StorageLayout.GetMediaPath(OutputDirectory, Board.ImageBoard.Name,
                Board.BoardCode, ThreadId, "1", ".bin");
            MediaProviderMetadata provider = DemoPayload.Provider;
            DownloadItem item = new DownloadItem(new Uri(provider.Url), path,
                Board.ImageBoard, this, provider.MediaId, provider.ExpectedSize,
                provider.ExpectedMd5, provider.OriginalFileName, legacyPath: null, variant: provider.Variant)
            {
                ExpectedSha256 = provider.ExpectedSha256, ExpectedSha1 = provider.ExpectedSha1, ProviderSizeHint = provider.ProviderSizeHint,
                AcquisitionRevisionKey = DemoPayload.AcquisitionRevisionKey,
                AttachmentReferences = new[] { DemoPayload.Reference }
            };
            if (!queue.TryEnqueue(item))
                throw new InvalidOperationException("The demo media was not admitted to the queue.");

            _firstPoll.TrySetResult();
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            _firstPoll.TrySetException(exception);
            throw;
        }
    }
}
