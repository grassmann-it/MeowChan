# MeowChan.PluginHost

Optional process host for `MeowChan.PluginSdk` sources and image filters. The public
API is not stable in 1.x; minor versions may change it. `libmeowchan` remains
independent of this package, Avalonia and Roslyn. Loading this package does not
start a worker or read an archive.

Own one `PluginWorkerManager` per application session. Supply a verified
`PluginArtifact`, active `PluginConfiguration`, `PluginWorkerLaunch` with an
absolute executable path, and request-bound `IPluginHostServices`. The worker
executable is delivered beside the application under `plugin-worker/`.
`StartAsync` loads one immutable revision; `DescribeAsync` uses a separate,
inert test process without HTTP or image services. Test calls pass
`testWorker: true`; filter workers pass `filterWorker: true`. Dispose clients
when a plugin is disabled and dispose the manager on application shutdown.

Each asynchronous source/filter request carries a resource, generation and
optional rule/grant revision. Callers cancel the request token when those values
are invalidated. Runtime checks reply and host-service correlation and rejects
late replies. Grant and staged-image lease enforcement remains the supplied
host-service owner's responsibility. Neither discovery nor runtime mutates
watches or archives, activates new builds, or chooses a fallback build.

`PluginHostHttp` borrows the session's `RequestPolicyState` and transport, and
uses the existing HTTP retry/pacing policy. `CreateTransport()` disables implicit
redirects and enables standard decompression. Injected transports must not follow
redirects or add retries. Every explicit redirect is checked against declared
metadata hosts; bodies are limited after decompression. Provider pacing/cooldown
returns `HostHttpDeferred` immediately, releasing the call slot.

Default limits:

| Resource | Default |
| --- | --- |
| Production / test workers | 4 / 1 |
| Concurrent starts / operations | 2 / 4 |
| Global waiting operations | 128 |
| Per-worker waiting operations | at most 8 |
| Idle eviction | 5 minutes; earlier when another plugin needs capacity |
| Source / filter managed heap | 256 / 512 MiB |
| Handshake / Describe | 10 seconds each |
| Source / filter call | 45 seconds |
| Cooperative cancellation | 2 seconds, then owned process tree termination |
| Automatic restarts | 3 in 10 minutes, delayed 1 / 5 / 30 seconds |
| Protocol frame / decompressed HTTP metadata | 16 / 8 MiB |
| Image read chunk / retained worker logs | 64 KiB / 1 MiB |
| Shared temporary workspace | 512 MiB |

`RetryAsync` explicitly resets the failed worker's bounded restart episode.
Unexpected exits are reported as worker failures. Only an explicit worker
out-of-memory marker and exit code are classified as a managed heap failure.
`GetSnapshots()` exposes worker identity, state, process id, failure and bounded
logs for passive UI consumption.

Workers receive unique owned working directories. A bounded scan checks their
aggregate size each second; `ExternalTemporaryBytes` can include image staging,
and `GetTemporaryBytes()` exposes the last bounded scan for staging admission
without rescanning directories on every image chunk. Scans count both files and
directories and reject more than 10,000 entries per worker. This is
accounting and termination, not an OS filesystem quota. Unix worker groups and
Windows kill-on-close jobs cover owned descendants, including after parent exit.
Native Windows/macOS execution still requires the release platform checks.

Local plugins run with the user's permissions. The process boundary and host
service grants are not an OS sandbox and cannot stop arbitrary C# code from
accessing other files or the network.
