# Legacy SDK 1.0.2 Host/Worker boundary probe

This is a real, offline process-boundary probe for the unchanged prebuilt
`LegacySdk102Plugin.dll` from
[`MeowChan.PluginSdk.Legacy102Probe/LegacyPlugin`](../MeowChan.PluginSdk.Legacy102Probe/LegacyPlugin).
It is deliberately narrower than an upgrade or release gate. The executable
uses the current public `PluginWorkerManager`, `PluginWorkerLaunch`,
`PluginArtifact`, and `IPluginWorkerClient` contracts. It does not provide a
fake manager or call the worker protocol directly.

## Inputs and compilation

The project has no `ProjectReference` and no `PackageReference`. The current
assemblies are local references only:

- `CandidateRuntimeDirectory`: an absolute directory containing the current
  `MeowChan.PluginHost.dll` and `MeowChan.PluginSdk.dll`, plus any current Host
  runtime siblings needed by the local build;
- `ProbeDependencyDirectory`: an absolute directory containing the current
  `libmeowchan.dll` and its local dependency closure.

The project has explicit `HintPath` references for those three assemblies. The
other local sibling DLLs are copied/referenced locally; no feed, provider,
restore package, or network request is part of this sample.

The parent runner compiles it centrally. The following is the exact command
shape (documentation only; this worker did not run it):

```text
dotnet build samples/MeowChan.PluginHost.Legacy102Probe/MeowChan.PluginHost.Legacy102Probe.csproj \
  --no-restore --configuration Release --output /absolute/probe-output \
  -p:CandidateRuntimeDirectory=/absolute/current-host-sdk-runtime \
  -p:ProbeDependencyDirectory=/absolute/current-core-dependencies
```

At runtime the current worker DLL is supplied explicitly. The worker DLL is
launched through a resolved local `dotnet` host, not by guessing an apphost or
starting a shell:

```text
dotnet /absolute/probe-output/MeowChan.PluginHost.Legacy102Probe.dll \
  --plugin /absolute/LegacySdk102Plugin.dll \
  --worker /absolute/current/MeowChan.PluginWorker.dll \
  --profile /absolute/new-private-profile
```

`--plugin`, `--worker`, and `--profile` are required absolute paths. The
profile must be a new or empty directory. The probe creates only its own
`private/` environment directories below that path and never deletes the
caller-owned profile. It redirects HOME, temporary, XDG, and .NET CLI profile
variables for the worker process to that private tree. It rejects profile paths
with `.ochan`, `authstore`, or database suffixes.

The supplied legacy plugin is only opened for SHA-256 verification and then
loaded by the current child worker. It is never compiled, copied over, or
rewritten. The probe compares the plugin hash immediately before the manager
run and after bounded worker/client/manager disposal; a changed hash cannot
pass.

## Observations and exact boundary

The probe performs these real operations:

1. `PluginWorkerManager.DescribeAsync` starts a current child worker and invokes
   the plugin's public `Describe` path with host services unavailable.
2. `PluginWorkerManager.StartAsync` starts another current child worker with a
   fully specified `PluginConfiguration`; the probe records the actual worker
   PID exposed by `IPluginWorkerClient.Snapshot`.
3. Through `IPluginWorkerClient`, it makes a synthetic board/catalog call for
   `legacy102/board` and a synthetic thread call for
   `legacy102/board/thread-001`. The expected values are copied from the
   legacy plugin contract: plugin id `legacy102.probe`, declared SDK version
   `1.0.0`, catalog revision `legacy102-catalog-1`, thread revision
   `legacy102-thread-1`, post `post-001`, and text
   `Deterministic SDK 1.0.2 payload`.
4. `NoHostServices` throws if the plugin attempts HTTP or image access, so a
   successful source observation is offline rather than a mocked HTTP result.

Cancellation uses one 90-second deadline for startup and source calls.
Client and manager disposal each have a 15-second bound. These are probe
lifetime bounds, not claims about a release timeout or a production profile
policy.

Successful stdout is one JSON object with this shape:

```json
{
  "scope": "legacy102-hostworker",
  "passed": true,
  "legacyPluginSha256": "<sha256 before and after>",
  "declaredSdkVersion": "1.0.0",
  "workerProcessObserved": true,
  "describeOk": true,
  "sourceCallsOk": true
}
```

The field names are the lower-camel JSON contract. `passed` additionally
requires the before/after hash comparison, an actual worker PID, and no bounded
operation or cleanup failure. Exit `0` is reserved for that complete probe;
malformed arguments return `2`, and an execution failure returns `1` with false
fields.

This proves only the current public Host-to-current-Worker boundary for this
unchanged legacy SDK binary and these deterministic SDK source calls. It does
not prove SDK compilation, package restore, Core/profile migration,
reauthorization, authentication, AP04, provider HTTP, GUI behavior, release
packaging, or compatibility outside the exact supplied DLLs. It makes no
`AP04` or `Reauthorization` assertion.
