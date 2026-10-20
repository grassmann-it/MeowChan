# Sessions and providers

`MeowChan.Registry` owns a session's watched boards and threads, their monitoring lifetimes, and one shared `DownloadQueue`. Constructing a registry or resolving a provider URL performs no HTTP requests. `AddAsync` starts monitoring after admission succeeds.

Create a queue with the desired global limits, register provider instances explicitly, and supply an absolute output root:

```csharp
using MeowChan;
using MeowChan.Boards.FourChan;
using MeowChan.Boards.EightKun;
using MeowChan.Downloader;
using MeowChan.Interfaces;

string threadOrBoardUrl = args[0];
string outputRoot = Path.GetFullPath("downloads");
IImageBoard[] providers = [new FourChanImageBoard(), new EightKunImageBoard()];
DownloadQueue queue = new DownloadQueue(5, 1024 * 1024);
await using Registry session = new Registry(
    providers, queue, outputRoot, TimeSpan.FromSeconds(60), 60, persistSession: false);

AdmissionResult result = await session.AddAsync(threadOrBoardUrl);
Console.WriteLine(result.Status);
Console.ReadLine();
await session.StopAsync();
```

The example watches the URL supplied as the first command-line argument until Enter is pressed. Keep the session alive for the application's desired monitoring lifetime. `DisposeAsync` or `StopAsync` cancels and awaits owned monitoring and downloads. `WaitForIdleAsync` on the queue only describes currently admitted media work; it does not mean that watched sources have stopped polling.

## Admission and removal

| API or result | Behavior |
| --- | --- |
| `AddAsync(url)` | Admits a canonical board or thread identity once. |
| `AdmissionStatus.Added` | A new watch was accepted. Media completion happens later. |
| `AdmissionStatus.AlreadyPresent` | The canonical resource already belongs to this session. |
| `AdmissionStatus.Invalid` | The URL is malformed or is not a recognized resource shape. |
| `AdmissionStatus.Unsupported` | No registered provider accepts the source. |
| `RemoveBoardAsync(board)` | Stops that board's discovery; existing thread watches continue. |
| `RemoveThreadAsync(thread)` | Cancels and awaits that watch and prevents automatic rediscovery. Explicit `AddAsync` clears the suppression. |
| `StopAsync()` | Stops runtime work while preserving saved watch intent for the next startup. |

Use the exact objects returned by admission or `GetView()` when removing resources. `RegistryView` pairs each `IThread`/`IBoard` handle with its copied snapshot from the same membership capture; this avoids joining a new lifetime to an old row by URL or ID. `Threads` and `Boards` also return copied membership lists. Re-adding a resource waits for its previous lifetime to finish. `IThread.DownloadDirectory` and `Registry.GetDownloadDirectory(board)` return assigned destinations, including after later configuration changes.

## Persistence ownership

Persistence is optional. A host that wants durable session intent **and** retry/lifecycle state acquires a profile lease, constructs `SessionStore` and `WorkStateStore`, passes both to `Registry`, and calls `RestoreAsync` before admitting new persistent watches:

```csharp
using MeowChan.Persistence;

string profileRoot = Path.GetFullPath("profile");
using ProfileLease lease = ProfileLease.Acquire(profileRoot);
SessionStore store = new SessionStore(profileRoot, lease);
WorkStateStore work = new WorkStateStore(profileRoot, lease);
await using Registry session = new Registry(
    providers, queue, outputRoot, TimeSpan.FromSeconds(60), 60,
    sessionStore: store, persistSession: true, workStore: work);
await session.RestoreAsync();
```

`SessionStore` persists the watch-list projection; it does not by itself persist
media retry history or source-end deadlines. Pass `workStore: new WorkStateStore(profileRoot, lease)`
explicitly for that authority. It writes `work-state.sqlite` under the same lease
and does not require post capture or Avalonia. The desktop supplies this store.
Omitting `workStore` uses `MemoryWorkStateStore`: policy still applies during that
registry's lifetime, but retry/end history is lost with that store. `persistSession: true`
alone does not make the default work store durable. A fresh volatile store cannot
reconstruct past failure times.

`IWorkStateStore` exposes `LoadResources`, generation-scoped `LoadMedia`,
`CommitIntent`, `CommitEnd`, `CommitState` and `CommitMedia`. `WorkResource` retains
watch intent, assigned root, generation, source end and pending capture;
`MediaWork` retains URI, destination, integrity metadata, attempts, failure and
earliest due time. Custom implementations must preserve generation fencing,
first-end immutability and confirmed intent before work admission. Do not edit
these records independently while Registry owns the session.

The lease must outlive the session; disposing in the order above satisfies that requirement.
With session persistence enabled and the default in-memory work store, saving the
session file remains the admission/removal barrier: a failed save propagates and
the requested runtime change does not proceed. With an explicitly supplied durable
work store, Registry commits authoritative intent, generation and assigned root
first. Failure of that authority commit propagates and prevents the runtime change.
A later failure to save the session JSON projection instead produces a warning;
it does not undo committed intent or falsely reject an admission/removal that can
proceed from the authority. Restore reconciles that projection against work state.
Hosts must surface both operation exceptions and snapshot warnings.
`GetSnapshot().Warnings` retains the latest 32 recoverable session warnings.
Unresolved saved URLs are retained for later recovery instead of silently discarded.

Before opening earlier-release profile or media data with MeowChan 0.9.0, stop older application and library processes and make a backup. Current profile and media locks use new names; older processes cannot coordinate with them. Concurrent old/new writers on the same data and downgrading migrated data are unsupported.

`ApplyOptionsAsync(SessionOptions, persistSession)` updates the queue limits and future polling delays. Existing watches keep their assigned destination. Directly added watches use the current root; discoveries from an existing board inherit that board's assigned root. Hosts should treat a settings update as complete only after the task succeeds. The desktop client's settings-file transaction is host behavior, not a requirement to use its `Config` type.

## Read state and observe changes

`GetSnapshot()` returns a `DownloadSessionSnapshot` with copied thread/board membership and resource state. The snapshot includes canonical `ResourceId` values, source availability, typed `ThreadActivity`, numeric known/completed media, actual queued/active counts, structured `FailureInfo` values, HTTP admission/cooldown progress, assigned directories and the latest 32 warnings. Individual components are captured coherently; independent workers are not paused for a global transaction.

Subscribe to `StateChanged` first, then read the initial snapshot:

```csharp
void Refresh()
{
    DownloadSessionSnapshot snapshot = session.GetSnapshot();
    foreach (ThreadSnapshot thread in snapshot.Threads)
    {
        Console.WriteLine($"{thread.Id}: {thread.Activity}, " +
            $"{thread.CompletedMedia}/{thread.KnownMedia}, " +
            $"queued={thread.Downloads.Queued}, active={thread.Downloads.Active}");
    }
}
session.StateChanged += Refresh;
Refresh();
```

`StateChanged` is an asynchronous invalidation signal, not a stream containing every intermediate transition. Each subscription has at most one running callback and one pending invalidation. Slow or throwing observers cannot block another subscriber or the session's download control path. UI consumers marshal their projection to their own dispatcher and fetch a fresh snapshot when notified.

Unsubscribe when a client detaches. Unsubscription or disposal discards pending invalidations and stops new callback admission; a callback already admitted for execution may still run or finish afterward. Session shutdown does not wait for arbitrary consumer callbacks. Hosts must keep their callback targets valid or guard their own lifetime accordingly.

## Provider and diagnostic contracts

Implement `IImageBoard` and the board/thread contracts, or derive from the provided monitoring bases, and pass the provider instance to the constructor. Provider names are stable, distinct source identities. A custom provider's `UpdateMonitoringInterval` must accept validated session intervals without I/O or exceptions, applying any documented provider minimum by clamping. URL classification and object creation must stay passive. Network calls, catalog iteration and media admission must propagate the lifetime cancellation token.

Provider discovery/removal sinks are exclusive session-control registrations, separate from consumer notifications. Their `IDisposable` registration belongs to the session; applications observe `StateChanged` and snapshots instead of installing another control owner.

Core components accept an optional caller-owned Serilog `ILogger` and are silent by default. The library neither reads nor configures the global logger. Pass a logger to the registry, provider and queue components whose diagnostics the host wants to receive; their lifetimes must not outlive a disposed logger.

The built-in default HTTP clients share the provider request policy. Supplying a custom `HttpClient` is an advanced injection path; use `ProviderHttpClientFactory.CreatePolicyClient` when the same request policy is required, and follow its ownership/cancellation contract. Never dispose or mutate process-owned default clients.

The [package consumer sample](../samples/LibMeowChan.Consumer/README.md) is the compiled reference for package restore and a custom provider. The library contains local single-thread HTML export, but no Avalonia UI, daemon or dynamic plugin loader.

## Registered source capabilities

`GetProviderDescriptors()` passively lists registered identities, canonical base
addresses and optional capabilities. Built-ins describe thread watches, board
catalogs, post capture and original media. Existing custom `IImageBoard` providers
remain compatible; capabilities are unspecified unless they implement
`IProviderDescriptorSource`. `CaptureAvailable` additionally requires live,
initialized session content services. No description lookup checks site health
or admits watches. Current failures and cooldowns belong to the observed watches.

Discovered thread origins are saved as the optional schema-1 field
`DiscoveredThreadUrls`. Legacy watches without that field use manual scheduling
priority. Explicitly admitting an already tracked thread URL promotes that watch
to manual priority after committing the changed session intent, when persistence
is enabled. Both classes still obey the same network policy.

`Registry.IsPaused` and `SetPaused(bool)` expose the queue's session pause. Built-in
thread/board automatic checks share the queue gate; ongoing requests/transfers
finish. Pause does not remove/re-add watches or clear media decisions. Library
clients choose their own persistence; the desktop persists pause in settings.

## Explicit checks and retry

`RequestCheck(thread)` and `RequestCheck(board)` target the exact registered
object/lifetime, never merely a URL. `Scheduled` means a request was signalled,
not that HTTP or a download has completed. Repeated requests coalesce into at
most one pending check. Paused, removed, stopping and unsupported watches return
separate statuses. The optional `ICheckRequestSource` contract leaves older
providers compatible; implementations must signal promptly without doing
provider work on the caller's thread.

Built-in BaseThread/BaseBoard polling consumes these requests in its existing
serialized loop. Automatic delays can be woken; the provider's minimum thread
interval and HTTP admission/cooldown remain in force. A terminal source stops remote polling; known media follow-up uses persisted
work descriptors rather than requiring another successful endpoint response.
Removing a watch or stopping the registry cancels its owned work.

`RetryMissingMedia(IThread, string? mediaIdentifier = null)` grants one outer
media attempt through the shared queue for the exact current watch.
`RetryCompletedMediaAsync(string threadUrl, string? mediaIdentifier = null)` repairs a
completed resource without starting a new source watch or 12-hour window.
Ordinary `RequestCheck` is not an override for media due times. See the
[download policy](Downloader.md#retry-and-source-end-policy).

The desktop exposes this action in Downloads details/context menus and on a
selected Diagnostics entry. The separate retry action retries missing selected media through the same
queue and storage verification; it does not clear errors in advance, re-evaluate
previous exclusions, or create replacement watches. Errors disappear only when
the corresponding operation actually succeeds.

## Restore and completion authority

Durable work intent is authoritative over `saved_urls.json`. Restore reconciles
watch generations and completion before activating work. `persistSession: false`
(the desktop's `SaveUrlsOnExit=false`) prevents automatic watch restoration even
when the work journal retains historical records. Retained archive data is separate.
A source ending does not mean all media succeeded: snapshots distinguish follow-up,
completed, repair and intervention states. Explicit re-add starts a new generation;
a board catalog does not silently revive a completed watch. Persistence errors must
be surfaced by the host; never treat a failed journal commit as successful completion.

## Local export API

`ArchiveHtmlExporter.ExportAsync(reader, threadId, destination, options, progress, token)`
is create-only and refuses an existing destination. `InspectAsync(destination, threadId)`
returns export identity, generation and inventory status. An explicit `UpdateAsync`
requires the expected export ID and generation; pass the inspection's
`ObservedFingerprint` as `expectedObservedFingerprint` to also fence external edits.
`confirmPreserveBackup: true` acknowledges keeping the old folder when contents
were changed, a legacy export was associated, or the new output is less complete.
The default is false. Results expose actual `Directory`, `IndexPath`, omissions,
`Warnings`, metadata and any retained `BackupDirectory`. Update stages the full
new output and journals the folder exchange; it is not a general-purpose directory
synchronizer or a power-loss guarantee for network storage. `ExportHistoryStore`
is an optional profile index; portable marker metadata remains with the export.
See the [offline export guide](Help.md#portable-html-export).
