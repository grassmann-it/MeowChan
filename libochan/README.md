# libochan

`libochan` is the .NET 10 media-download library used by the oChan desktop application. It includes the 4chan and 8kun adapters, a shared download queue, verified file storage, optional session persistence, media-selection rules and a local post archive. It has no Avalonia dependency and reads no desktop configuration files. Existing namespaces remain `oChan`, `oChan.Interfaces`, `oChan.Downloader` and `oChan.Boards.*`; content APIs use `oChan.Archive`, `oChan.Rules` and `oChan.Storage`.

This branch prepares the 0.9.0 desktop update and adds optional library content services. Existing download-only hosts can continue without those services. The repository can prepare the library package locally. Local packaging does not imply public NuGet publication or completion of desktop platform acceptance.

## Start a session

The host supplies providers, output location and limits explicitly. This example watches the URL passed as the first command-line argument until Enter is pressed:

```csharp
using oChan;
using oChan.Boards.EightKun;
using oChan.Boards.FourChan;
using oChan.Downloader;
using oChan.Interfaces;

string outputDirectory = Path.GetFullPath("downloads");
IImageBoard[] providers = new IImageBoard[]
{
    new FourChanImageBoard(),
    new EightKunImageBoard()
};
DownloadQueue queue = new DownloadQueue(
    maxParallelDownloads: 3,
    maxBandwidthBytesPerSecond: 1024 * 1024);

await using Registry session = new Registry(
    providers, queue, outputDirectory,
    threadInterval: TimeSpan.FromSeconds(60),
    boardIntervalSeconds: 60,
    persistSession: false);

AdmissionResult result = await session.AddAsync(args[0]);
Console.WriteLine(result.Status);
Console.ReadLine();
await session.StopAsync();
```

`Added` means that the watch was admitted and started. It does not mean that its first source request or media downloads have completed. `AlreadyPresent`, `Invalid` and `Unsupported` are separate admission outcomes. Operational failures such as persistence errors are reported to the caller through exceptions.

## Ownership and persistence

Construction is passive. `Registry` owns watch lifetimes and the supplied queue's shutdown. Use one queue per session. `StopAsync` and `DisposeAsync` stop further work, cancel active requests and transfers, and await cleanup. Repeated shutdown is valid; a new run uses a new session and fresh provider resources. Provider transports and profile leases supplied by the host remain host-owned and must outlive the session.

Registry mutation commands can be called concurrently; admission, persistence and removal decisions are serialized internally. Snapshots can be read from any thread. Await the initial restore before submitting user commands, and use the registry's lifecycle methods rather than starting or stopping its provider resources independently. A stopped session rejects new admissions.

For durable watches, acquire `ProfileLease.Acquire(profileDirectory)`, create `SessionStore(profileDirectory, lease)`, pass that store to `Registry`, and await `RestoreAsync()` before accepting new mutations. Keep the lease alive until the registry has stopped. Successful persistent Add/Remove calls await the session commit; closing the application does not erase tracked intent. The built-in store allows one writer per profile. Separate profiles can coexist; media publication uses per-directory leases between cooperating writers.

Removing a board stops catalog discovery while admitted threads continue. Removing a thread stops its work and suppresses automatic rediscovery; explicitly adding its URL clears that suppression. `SessionOptions` with `ApplyOptionsAsync` updates queue limits and future polling delays. Existing watches retain their assigned output roots. New directly added watches use the new root; threads discovered by an existing board inherit that board's assigned root. Persistence and settings-write errors are observable by the host.

Files are stored under `{root}/{source}/{board}/{thread}/{mediaId}.{ext}`. The store verifies available size/hash evidence, publishes the complete file, then commits its manifest. Existing foreign files are preserved. A new store verifies saved media before reuse. Within a store, unchanged file fingerprints can reuse verification for up to ten minutes; preserving every fingerprint during an external edit can defer detection until that deadline.

## Add rules and post capture to a session

`ThreadContentRuntime` joins a local `ArchiveCatalog` and `MediaRuleStore` under the host's profile lease. Initialize it before restoring or adding watches, then pass it to `Registry`. The registry borrows the runtime; stop/dispose the registry before disposing the runtime and lease. Session restoration remains a separate choice.

```csharp
using oChan;
using oChan.Archive;
using oChan.Boards.EightKun;
using oChan.Boards.FourChan;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Persistence;

string profile = Path.GetFullPath("profile");
using ProfileLease lease = ProfileLease.Acquire(profile);
await using ThreadContentRuntime content = new ThreadContentRuntime(profile, lease);
await content.InitializeAsync();
if (content.LoadWarning != null) Console.Error.WriteLine(content.LoadWarning);

SessionStore store = new SessionStore(profile, lease);
IImageBoard[] providers = new IImageBoard[] { new FourChanImageBoard(), new EightKunImageBoard() };
DownloadQueue queue = new DownloadQueue(maxParallelDownloads: 3, maxBandwidthBytesPerSecond: 1024 * 1024);
await using Registry session = new Registry(
    providers, queue, Path.GetFullPath("downloads"),
    threadInterval: TimeSpan.FromSeconds(60), boardIntervalSeconds: 60,
    sessionStore: store, persistSession: true, contentRuntime: content);
await session.RestoreAsync();

AdmissionResult result = await session.AddAsync(args[0]);
Console.WriteLine(result.Status);
Console.ReadLine();
await session.StopAsync();
```

The active profile and its SQLite catalog, `archive.db`, belong on local storage. Media directories may be on a NAS or network share; this does not make the catalog a shared network database. Keep the entire profile and the referenced media when backing up. Stop the writer before taking a simple file-copy backup.

Both built-in providers capture post metadata from the same successful response used to discover media. New thread registrations enable capture; previously persisted watches without a capture preference start media-only. Existing preferences survive registration and restart. New threads discovered by an existing board use the new-thread default. For a live built-in watch, explicitly change capture with `BaseThread.SetCaptureEnabledAsync(bool)`; an offline registration can be changed through `ArchiveCatalog.SetCaptureEnabledAsync`. Turning capture off or removing a watch preserves previously stored posts and files.

The archive retains posts seen in successful observations, including posts absent from later responses. Missing fields preserve known values, explicitly supplied fields replace them, and explicit deletion markers are retained. This is the latest captured version of each post, not a revision history or proof that the entire original thread was observed. An archived/gone source state is separate from the locally captured content and pending media work. Storage failures are exposed through `ThreadSnapshot.Content` and do not manufacture a successful capture.

## Media-selection rules

Rules resolve each field independently through **Global → Source → Board → Thread**. `MediaTypeSelectionMode` distinguishes `Inherit`, `All` and `Selected`; an empty `Selected` list excludes every type. Extensions such as `JPG` normalize to `.jpg`. `MediaSizeLimitMode` distinguishes `Inherit`, `Unlimited` and `Limited`; the public API takes bytes. A disabled rule inherits from its parents. Effective values include the origin scope and rule revision for both fields.

For example, update one board while preserving every other override:

```csharp
using oChan.Rules;

MediaRuleSet baseline = content.Rules.Current;
MediaRuleScope scope = MediaRuleScope.ForBoard("4chan", "g");
MediaRule replacement = new MediaRule(scope,
    types: new MediaTypeSelection(MediaTypeSelectionMode.Selected, new[] { ".jpg", ".png" }),
    sizeLimit: new MediaSizeLimit(MediaSizeLimitMode.Limited, 10_000_000));
MediaRuleSet saved = await content.Rules.SaveAsync(
    baseline.Rules.Where(rule => rule.Scope != scope).Append(replacement),
    expectedRevision: baseline.Revision);
EffectiveMediaRule effective = new MediaRuleResolver(saved).Resolve(scope);
Console.WriteLine(effective.SizeLimit.Bytes);
```

`SaveAsync` replaces the complete catalog. Supply `expectedRevision` for an editor so a stale draft cannot overwrite a newer save. The store writes `media_rules.json` atomically and activates the new revision only after the commit. A recoverable backup produces a load warning; unrecoverable or unsupported documents are preserved and reported to the caller.

The size limit evaluates provider metadata, not transferred bytes: an unknown size is allowed with reason `AllowedUnknownSize`. An unknown extension is allowed only by `All`. The desktop editor uses decimal MB (1 MB = 1,000,000 bytes). Rules select downloads; they do not pause watches, delete files or enforce a transfer-byte budget.

With content services attached, the first decision for `(source, board, thread, media ID, variant)` is committed in `archive.db` before queue admission. Its result, reason, rule revision and field origins survive restart and later rule changes. Refreshed source URLs and metadata do not silently reevaluate an existing decision. Previously saved media retain their selected intent; normal storage verification still decides whether their bytes need repair. Disabling post capture leaves these decisions active. A failed decision commit prevents that new admission and exposes a storage failure.

`MediaRuleResolver.Evaluate` alone is a stateless evaluation of the supplied rules. Custom hosts that do not use `ThreadContentRuntime` must explicitly persist decisions with `ArchiveCatalog.GetOrCreateDecisionAsync` if they need the same restart semantics. A selection decision never proves that a file has finished downloading; the media manifest remains the completion authority.

## Read captured content offline

`ArchiveReader` needs only an existing profile directory. It does not require a provider, registry or writer lease, performs no HTTP requests, and never creates, migrates or repairs a database. A missing catalog, unsupported schema or read failure is reported rather than replaced with an empty archive.

```csharp
using oChan.Archive;

await using ArchiveReader reader = await ArchiveReader.OpenAsync(Path.GetFullPath("profile"));
ArchiveThreadPage threads = await reader.ListThreadsAsync(offset: 0, limit: 50);
foreach (ArchiveThreadSummary summary in threads.Items)
{
    ArchivedThreadView? thread = await reader.ReadThreadAsync(summary.Id);
    if (thread == null) continue;
    Console.WriteLine($"{summary.Id}: {thread.PostCount} captured posts");
    foreach (CapturedPost post in thread.GetPosts(offset: 0, limit: 50))
        Console.WriteLine($"{post.PostId}: {post.Subject}");
}
```

Each list page includes rows and count from one read transaction. `ReadThreadAsync` materializes one complete, immutable thread view; `GetPosts` pages that retained view in memory. Subsequent writes cannot change its pages. A later list/read is a new observation, not a continuation of one archive-wide snapshot. Page limits are 1–500. Captured HTML is source data: a consuming UI must render it as text or sanitize it before displaying active markup.

### Search and quotes

```csharp
ArchiveThreadPage matches = await reader.SearchThreadsAsync(new ArchiveThreadQuery(
    Text: "search phrase", Source: "4chan", Board: "g", HasPosts: true), limit: 50);
ArchivePostId postId = new ArchivePostId(new ResourceId("4chan", "g", "123"), "124");
ArchiveQuotePage quotes = await reader.GetQuotesAsync(postId, limit: 50);
foreach (ArchiveQuote quote in quotes.Items)
{
    ArchiveQuoteResolution target = await reader.ResolveQuoteAsync(quote);
    Console.WriteLine($"{quote.Label}: {target.State} at index {target.PostIndex}");
}
ArchiveQuotePage backlinks = await reader.GetBacklinksAsync(postId, limit: 50);
```

Search treats input as a literal case-insensitive substring, not a query language.
It covers source/board/thread identities, titles, post identities, subjects, authors
and readable post text. Source/board filters are exact stored identities; source
state, capture preference, posts/media-only and sort order are separate filters.
Text is limited to 512 characters. Counts and rows share one read transaction,
and page limits remain 1–500. Search never opens media directories.

Archive schema 2 indexes decoded inert text with bundled SQLite FTS5/trigram.
One/two-character terms use a cancellable scan so short terms do not become false
empty results. Opening `ArchiveCatalog` with its writer lease transactionally
migrates schema 1 and backfills search/quote indexes, retaining posts, preferences,
media decisions and directories. A failed migration rolls back. Independent
`ArchiveReader` also reads schema 1 without modifying it: legacy search/backlink
queries use local scans. Use `ArchiveCatalog` for writes so merge semantics and
derived indexes stay consistent; raw SQL edits are not the public writer API.

Quotes recognize actual source anchor attributes, same-thread fragments and the
supported 4chan/8kun thread paths/host aliases. `Captured`, `MissingPost`,
`MissingThread`, `External` and `Unresolved` are distinct; a guessed identity alone
does not prove that content is captured. `PostIndex` addresses `resolution.View`, the same immutable read, including a
target beyond the first reader page. Later captures cannot shift those pages. Backlinks span
retained local threads/boards and are paginated. Disappeared posts retain their
last observed references; an observed HTML change replaces that post's old
references transactionally. Resolution never fetches a remote target.
`ArchiveText.PlainText` provides the same inert readable text used by indexing.

Post attachments describe what the provider reported. To read actually saved bytes, obtain the thread's `MediaDirectories` and open each with `MediaAssetReader.OpenAsync`. This yields an immutable manifest view, including retained previous media versions, without scanning unrecorded files into the archive. `MediaManifestStatus` distinguishes missing, available and corrupt manifests; unsupported versions and I/O errors throw. Media paths may be temporarily unavailable even while local posts remain readable. Here `threadDirectory` is one of those recorded directories:

```csharp
using oChan.Storage;

MediaAssetSnapshot assets = await MediaAssetReader.OpenAsync(threadDirectory);
foreach (MediaAssetReference asset in assets.GetPage(offset: 0, limit: 100))
{
    Console.WriteLine($"{asset.AssetId}: {asset.OriginalFileName} ({asset.Length} bytes)");
    await using Stream verified = await assets.OpenVerifiedReadAsync(asset.AssetId);
    // Consume this verified stream, for example with an image decoder.
}
```

`OpenVerifiedReadAsync` verifies the selected file's length and SHA-256 and returns the same stream rewound for consumption. A manifest entry or a previously displayed preview alone cannot guarantee that an external file still exists or is unchanged. `IsCurrent` identifies the last committed version for that requested filename in this manifest view; it does not claim a live match to the latest provider response. The current store publishes original media; independent preview/video variants and HTML export remain separate work.

## State and provider extensions

`Registry.GetSnapshot()` returns defensive `DownloadSessionSnapshot`, `ThreadSnapshot` and `BoardSnapshot` values. Canonical `ResourceId` values identify source, board and thread. Thread snapshots expose typed `Activity` and `SourceState`, request admission/cooldown progress, numeric media totals, actual queued/active worker counts, and `FailureInfo` values with categories and available HTTP status codes. A source can be archived or gone while its known pending media still complete. The legacy formatted `Status`/`Progress` properties remain display conveniences; consumers use snapshots for decisions. Registry membership, individual resources and queue counts are captured coherently at their own boundaries; snapshot creation does not freeze independent workers into one global transaction.

In-process clients that also remove resources can use `Registry.GetView()`. Its read-only dictionaries pair the exact `IThread`/`IBoard` handles with snapshots from the same membership capture. This keeps removal commands tied to the displayed lifetime even if the same canonical URL is removed and added again. `GetSnapshot()` is the simpler choice for consumers needing only copied state.

Subscribe to `Registry.StateChanged` before reading the first snapshot, then reread after each signal. Notifications are asynchronous invalidations; intermediate changes can coalesce. Each subscription has at most one executing callback and one pending invalidation. Slow or throwing subscribers cannot block producers or other subscribers. Unsubscribing and disposal discard pending notifications without waiting for arbitrary consumer code; an already executing callback may finish afterward. A permanently blocked synchronous handler retains its one dispatch worker until it returns. GUI clients marshal and coalesce their own UI updates. Base-resource `PropertyChanged` follows the same policy and an empty property name means reread all properties. Snapshot warnings retain the latest 32 entries; awaited command completion remains the authority for a successful durable change.

Logging is optional and explicit: pass a host-owned `Serilog.ILogger` through the `logger` parameters of the registry, queue and providers. They default to a no-op logger and neither replace nor read the global `Log.Logger`. Workers/limiters inherit the queue logger and resources inherit their provider's logger. The library never disposes a supplied logger. Logging sinks should return promptly; their execution is separate from state-notification isolation.

Register a custom `IImageBoard` in the constructor. `BaseImageBoard`, `BaseBoard` and `BaseThread` supply the standard lifecycle for adapters. Resolvers and constructors must be passive, return canonical identities, and create fresh resources for fresh lifetimes. Provider code propagates cancellation, assigns typed activity, and submits `DownloadItem` instances to the session queue. Source IDs must be unique within a session and path components must meet `StorageLayout`'s portable rules. Discovery/removal sink registrations belong exclusively to the session owner and provide reliable control; consumers observe `StateChanged` instead. The package-only console sample demonstrates this using only public APIs and an entirely synthetic HTTP handler.

Custom `UpdateMonitoringInterval` implementations must accept intervals validated by `SessionOptions.Validate` without throwing or performing I/O. They update the next waiting period; stricter provider minimums may increase the effective delay. This contract allows `ApplyOptionsAsync` to commit settings and then apply the already validated runtime values consistently. The supplied base implementations follow this contract.

Post capture and persistent rule decisions require adapter participation. A `BaseThread` adapter opts in with `SupportsContent`, restores candidates through `RestoreContentCandidatesAsync`, submits normalized snapshots through `CaptureContentAsync`, records terminal source states through `CaptureSourceStateAsync`, and admits media through `EnqueueKnownMediaAsync`. The built-in thread adapters demonstrate this awaited path. Setting a capability flag without implementing these steps does not provide capture or filtering to a custom adapter. `IImageBoard` currently has no general capability descriptor or online-health API; registering a provider proves neither current reachability nor successful capture.

Built-in default clients share rate admission and cooldown policy. An injected `HttpClient` is an advanced host-owned transport: wrap real HTTP with `ProviderHttpClientFactory.CreatePolicyClient` and share `RequestPolicyState` when limits must be coordinated. The underlying transport must disable automatic redirects/retries and honor cancellation. Caller-owned policy clients are disposed after session shutdown; process-owned default clients must not be disposed or mutated.

## Build and verify the package

From the repository root:

```sh
dotnet test oChan.sln -c Release
dotnet pack libochan/libochan.csproj -c Release -o artifacts/packages
python3 scripts/test-local-package.py --feed artifacts/packages --consumer samples/LibOChan.Consumer/LibOChan.Consumer.csproj
```

Use a fresh feed containing one candidate package. The verifier restores the sample with a fresh package cache and forces `libochan` to come from that local feed, checks the restored package hash and excludes desktop packages from the dependency graph. The sample performs a verified download, starts a fresh session over the same files, confirms reuse without another media request, and stops cleanly. It then creates and reads a SQLite post catalog after disposing its writer and lease, verifying the native dependency through the actual package. It contacts no public imageboard.

`libochan` is licensed under Artistic License 2.0; the package includes `LICENSE`. Its NuGet dependencies retain their own licenses. This package contains no Actipro or Avalonia binaries, desktop assets or desktop redistribution terms.

## Passive provider descriptions

`Registry.GetProviderDescriptors()` returns registered source identities, addresses
and optional implementation capabilities without requesting a site or creating
a watch. Existing providers need no new mandatory interface. Implement
`IProviderDescriptorSource` to declare capabilities, explicit supported hosts and
an optional adapter version. Missing capabilities mean unspecified, not unsupported.
`CaptureAvailable` additionally requires initialized session content services;
registration and descriptions do not imply online availability.
