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

`PluginOriginalMediaHttp.CreateClient(transport, source, policy, logger)` creates
an original-media client for one source. It accepts bodyless GET/HEAD and follows
at most five explicit redirects. The initial URI and every redirect destination
must use an exact declared `HttpHostPurpose.OriginalMedia` host; metadata or
preview permission is insufficient. Credentials, fragments, custom ports,
HTTPS-to-HTTP downgrades and unsupported or credential-bearing headers are
rejected. Transport must still disable automatic redirects/retries; a raw
transport guard detects an unexpected final URI before the request policy can
replace the response's request message.

All redirect headers and policy retries share one 60-second deadline, including
initial admission. This additionally bounds the initial healthy admission queue,
which the general request policy can otherwise retain before starting its own
network budget. Caller cancellation propagates; this shared deadline expires as
`TimeoutException`. The deadline ends when final response headers are returned.
The caller owns that response and its body, including read cancellation and body
idle deadlines; `DownloadWorker` supplies those checks for application downloads.
Redirect handling grants no source-integrity exception and does not alter
transport-length, hash or commit checks.

The returned media `HttpClient` owns its redirect/guard/policy wrappers and must
be disposed by its consumer. It borrows the supplied transport and never disposes
or mutates it. Intermediate and rejected responses are disposed by the wrapper;
the final response remains the caller's responsibility. As with other public
Host helpers, this API is part of the explicitly unstable 1.x Host surface, not
an additional Core or PluginSdk contract.

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
