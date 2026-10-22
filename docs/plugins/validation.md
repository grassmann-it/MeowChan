# Validate plugins and interpret failures

A successful compile, a valid description, a parsed fixture, a live metadata
response and a durable acquisition are different results. Record the candidate,
platform, input and actual path exercised. Do not label one as proof of another.
This guide documents the current 1.0 source tree, not an already published package
or completed native release matrix.

## Consume the SDK package

The [SDK consumer project](../../samples/MeowChan.PluginSdk.Consumer/MeowChan.PluginSdk.Consumer.csproj)
uses a `PackageReference` to `MeowChan.PluginSdk`, not a Core/desktop project
reference. Its [program](../../samples/MeowChan.PluginSdk.Consumer/Program.cs)
validates synthetic source results and JSON roundtrips without public HTTP.
Use that actual example instead of reconstructing it from a documentation snippet.

For a local candidate feed, development commands from the repository root are:

```sh
dotnet pack MeowChan.PluginSdk/MeowChan.PluginSdk.csproj -c Release -o artifacts/plugin-sdk-feed
dotnet restore samples/MeowChan.PluginSdk.Consumer/MeowChan.PluginSdk.Consumer.csproj --source artifacts/plugin-sdk-feed --packages artifacts/plugin-sdk-consumer-cache -p:MeowChanPluginSdkVersion=1.0.0
dotnet run --project samples/MeowChan.PluginSdk.Consumer/MeowChan.PluginSdk.Consumer.csproj --no-restore -p:MeowChanPluginSdkVersion=1.0.0
```

Choose a fresh feed/cache for a reproducible package check and inspect the restored
package identity/hash. These are development instructions, not a claim that the
package has been published. A project-reference build alone does not establish
that the packaged SDK contains everything an independent author needs.

## Source and filter validation

1. Validate `Describe()` with `SdkValidation`, including duplicate plugin/source
   IDs, URL overlap, canonical routes and settings scopes. Describe must work
   without site access.
2. Run deterministic local HTTP fixtures through the public plugin entry points.
   Include normal/partial/null/empty updates, multiple attachments, unchanged with
   and without an accepted baseline, verified NotFound, final archived snapshots,
   malformed data, oversize, provider waits and cancellation. Test real source
   differences; do not invent an archive flag just to fill a test case.
3. Build the single source through the delivered compiler with no developer SDK
   or network dependency. Check a cache hit, source change and an actual syntax
   error with file/line diagnostics. Test description/settings in a separate real
   worker, including timeout/crash/cancellation handling.
4. Exercise the normal registry/queue/store path with a fresh isolated profile:
   a thread, board discovery, repeat polls without duplicates, changed metadata,
   media failures, source end and pending-media completion. Restart in a fresh
   process and read/export retained posts and media. Disable/remove the plugin
   and verify offline access remains available.
5. For filters, use real controlled image bytes and grants. Cover Match, NoMatch,
   invalid/unsupported bytes, token misuse, grant/rule/generation changes and
   failure cleanup. A Match must reuse the inspected stage without a second
   original transfer; only a successful store commit counts as saved.
6. Make a few intentional provider requests only after local fixtures work.
   Retain bounded provenance and distinguish status/shape observations from
   media acquisition or historical completeness. Never turn a public site into
   a load-test fixture.

The maintained [example tests](../../tests/MeowChan.ExamplePlugins.Tests/ExamplePluginTests.cs)
cover Lainchan/Kohlchan parser, HostHttp-result and DTO behavior. Their
[fixtures](../../tests/MeowChan.ExamplePlugins.Tests/Fixtures/README.md) are synthetic,
not saved public posts. The [official source tests](../../tests/MeowChan.OfficialPlugins.Tests/OfficialSourceTests.cs)
cover the existing providers; [runtime process tests](../../tests/MeowChan.PluginRuntime.Tests/RuntimeProcessTests.cs)
and [compiler tests](../../tests/MeowChan.PluginCompiler.Tests/CompilerIntegrationTests.cs)
exercise their separate mechanisms.

## Current example evidence

On 2026-10-22 the Lainchan/Kohlchan example suite passed 48 targeted tests. Both
single files also compiled in a native Linux-x64 self-contained compiler probe
with the system SDK and NuGet home inaccessible, network disabled, a read-only
compiler installation and a foreign working directory. That probe establishes
compiler independence on that platform; it is not complete worker/registry/
queue/store or five-platform package acceptance.

Lainchan's documented sample established thread/catalog structure, multiple-file
shape, ETag/304, a missing-thread 404 and original-media URL shape. Kohlchan's
small thread established the LynxChan object shape, an original-media path and
If-Modified-Since304. Kohlchan's two live catalog responses exceeded the 256-KiB
**evidence collection** limit; they were not accepted as parsed full catalogs.
This smaller collection limit is distinct from the host's 8-MiB metadata limit.
See each example's [Lainchan](../../plugins/Examples/Lainchan/README.md) /
[Kohlchan](../../plugins/Examples/Kohlchan/README.md) evidence notes.

No original media were downloaded in that evidence collection. A documented
LynxChan archived flag and a synthetic terminal fixture do not prove a live
archiving transition. Full product acquisition/restart/export, package-consumer
acceptance, current GUI captures and the native release matrix remain separate
release checks; consult the candidate's release notes for their actual results.

## Diagnose without losing state

| Symptom | Meaning and next step |
| --- | --- |
| Compile error with file/line/column | Correct the saved source and build/test again; the active worker is unchanged during the session |
| Missing references/toolchain | Repair the complete application/compiler bundle; do not add arbitrary references or restore packages inside a source build |
| Incompatible SDK/protocol or invalid description | Rebuild/adapt against the shipped contract; the host explains unavailability instead of accepting incompatible code |
| Duplicate IDs or ambiguous routes | Choose/repair the conflicting candidate explicitly; filenames do not determine ownership |
| Startup update failed | Inspect the reported error; use the retained last-good build explicitly if needed, which pins that selection |
| Corrupt cached artifact | Integrity failure; do not overwrite an active/pinned artifact in place or delete the whole profile |
| Deferred/provider cooldown | Wait for the reported due time; do not loop inside the plugin |
| Missing filter access | Grant the intended source/board/rule scope or keep the technical omission; plugin availability alone does not grant bytes |
| Filter technical error | Preserve its reason; do not convert it to NoMatch or count it as stored media |
| Cancelled or timed-out call | The result is no longer current; stale replies cannot authorize archive/store mutation |

The current runtime defaults are a 10-second handshake, 45-second operation and
2-second cooperative cancellation grace, then termination of the owned process
for uncooperative work. Default capacities are four production workers, one test
worker, two simultaneous starts and four calls; queues are bounded. Idle workers
may be reclaimed after five minutes. Source/filter managed heaps default to
256/512 MiB. Worker crash restarts are bounded; repeated failure requires explicit
retry. These limits protect availability and do not turn local C# into sandboxed
code. Applications may select supported stricter settings.
