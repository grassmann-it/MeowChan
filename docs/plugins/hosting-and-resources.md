# Optional hosting, bundles and resources

Plugin authors use `MeowChan.PluginSdk`; applications that execute plugins use
`MeowChan.PluginHost`. The core `libmeowchan` library remains usable with explicit
in-process `IImageBoard` providers and does not depend on the host, compiler,
Avalonia or Roslyn. Adding a plugin is not a reason to put host lifecycle objects
inside the SDK or pass them across the process boundary.

## An application may add the host

All three public projects target .NET 10 and have no 1.x API stability promise.
Their project/package identities do not assert public-feed availability. The
[core guide](../../libmeowchan/README.md) remains the entry point for sessions,
queues, persistence and offline reading.

The optional host supplies these concrete building blocks:

| API | Responsibility |
| --- | --- |
| [PluginInstallationInventory](../../MeowChan.PluginHost/Installation/InstallationContracts.cs) | Scan top-level candidate files and retain diagnostics for invalid/unsupported entries |
| [IPluginCompilationService](../../MeowChan.PluginHost/Compilation/CompilationContracts.cs) | Build a saved source snapshot, retrieve verified artifacts and clean only unretained cache entries |
| [IPluginBundleInstaller](../../MeowChan.PluginHost/Installation/PluginBundleManifest.cs) | Import a verified prebuilt bundle |
| [IPluginProfileStore](../../MeowChan.PluginHost/Configuration/IPluginProfileStore.cs) | Own candidate selection, active/pending/last-good/pinned releases, configuration and scoped grants |
| [PluginWorkerManager](../../MeowChan.PluginHost/Runtime/PluginWorkerManager.cs) | Own bounded production/test workers, calls, cancellation and shutdown |
| [IPluginHostServices](../../MeowChan.PluginHost/Contracts/RuntimeContracts.cs) | Supply correlated metadata HTTP and token-bound image reads |
| [PluginSourceImageBoard](../../MeowChan.PluginHost/Sources/PluginSourceImageBoard.cs) | Adapt a passive source description and asynchronous worker factory to a Core provider |

Start from these maintained contracts and their tests; do not copy private desktop
`PluginSession` into an author plugin. A host owns the profile lease, HTTP policy,
configuration/grants, worker manager, media queue and archive lifetimes. `Registry`
receives explicitly selected providers. Pass cancellation and generation/revision
context through asynchronous calls, and dispose workers before releasing the
profile. Local routing must remain passive, with no worker wait under a session
or UI gate.

Build/import returns a verified immutable `PluginArtifact`, not an active plugin.
Description and configuration are validated in a worker. The activation store
selects a build together with its matching configuration; a test observation does
not activate it. Store commands require the last observed profile revision, so a
stale writer fails instead of overwriting another change. Respect lifecycle and
grant events when cancelling or rejecting in-flight work.

The normal SDK transport is private length-prefixed JSON over worker stdin/stdout.
Plugin code does not implement that wire protocol. **Do not write to stdout**;
use `context.Log` for bounded messages. Keep source bodies, original image bytes,
secrets and sensitive extracted content out of diagnostics. Logs are diagnostic
context, not an unbounded event database.

## Build inputs and cache

The [bundled compiler guide](../../MeowChan.PluginCompiler/README.md) is the
maintained reference for toolchain preparation and the manifest format. Its
helper receives one saved source byte snapshot and delivered .NET/SDK references;
it never evaluates the user's project or searches an installed SDK/NuGet cache at
runtime. A development build of the repository still requires its pinned SDK.

The compilation fingerprint includes source bytes, reference hashes, SDK,
compiler/options and TFM. `ExecutionRevision` additionally identifies verified
resources. A resource-only change changes execution revision without forcing a
source recompile. Display versions, timestamps and configuration values do not
replace these fingerprints.

Current compiler defaults are one active build plus eight waiting requests,
60 seconds, a 4-MiB source limit and a 256-MiB managed helper heap. Cancellation
and timeout stop the owned helper tree. Diagnostics map to the original source
path/line/column. Completed artifacts are hash-checked before publication and
again on retrieval. A damaged active/pinned artifact is reported, not silently
rewritten. Cache cleanup must preserve the activation store's retained active,
pending, last-good, pinned and disabled selections.

## Prebuilt bundles

The importer accepts a `*.plugin.json` serialized from
[PluginBundleManifest](../../MeowChan.PluginHost/Installation/PluginBundleManifest.cs).
Use the [existing manifest example](../../MeowChan.PluginCompiler/README.md) rather
than an unversioned second schema. It declares schema version, relative assembly
path/hash, SDK/compiler fingerprints and resource records. Assembly/resources
are copied into the verified immutable cache before use.

A loose DLL is not a complete installed bundle. Importing prebuilt code avoids
running the compiler helper, but the current importer still validates the
supplied SDK fingerprint against the delivered toolchain manifest. It does not
make the complete runtime/toolchain packaging optional or initialize the plugin
inside the importing process.

Paths are normalized and relative to the bundle; traversal, symbolic links,
reserved cache names, duplicate identities/paths and mismatching hashes are
rejected. Keep hashes lowercase hexadecimal where the manifest requires them.
A hash detects changed bytes; it is not a signature or an endorsement of trusted
plugin code.

## Resources and helper processes

An embedding host can pass `PluginResourceInput` values explicitly to `BuildAsync`.
Prebuilt bundles use `PluginArtifactResource` records with logical `Name`,
`RelativePath`, SHA-256 and optional `Rid`. Dropping extra files beside a `.cs`
source does not automatically include them. Current resource limits are 128
records, 32 MiB per resource and 64 MiB total.

During worker startup, resources are verified and filtered by the exact current
runtime identifier. `context.Resources.ResolvePath(logicalName)` returns only
an applicable verified resource from that plugin's bundle, or null. Applicable
logical names must be unique; do not assume an unspecified RID fallback or
specific-over-generic precedence. Resource resolution is not arbitrary host file
access.

A future plugin may resolve its own packaged native helper/data and invoke that
helper using literal process arguments. It must honor the call cancellation,
bound its own temporary copies and child processes, and clean them up. Resolve
the bundled executable explicitly rather than finding a random program on PATH.
The host's original-byte stage remains authoritative for filter commit. A native
helper is still trusted user code, not a sandboxed service.

OCR/Tesseract and language data are not delivered by the 1.0 example filter. These
resource hooks permit a later separate implementation; they do not authorize an
automatic engine/model download or claim that the native OCR path is validated.
