# SDK 1.0.2 binary probe

This sample is an offline, executable compatibility probe for the published
SDK assembly from MeowChan **1.0.2** and the candidate SDK **1.1.0**. It is
not a release approval and does not claim that the PluginHost, PluginWorker,
profile admission, reauthorization, packaged upgrade, or AP04 flow is
compatible.

## Inputs and ownership

The probe deliberately has two independent compilations:

1. `LegacyPlugin/LegacyPlugin.csproj` is a library compiled only against an
   absolute `SdkAssemblyPath` pointing to the actual released
   `v1.0.2` `MeowChan.PluginSdk.dll` from commit/tag
   `eec6c0169f8d52f7481ea0bf7febfd20981e97c3`. It emits
   `LegacySdk102Plugin.dll` and implements only the old `ISourcePlugin` API.
2. `CandidateProbe/CandidateProbe.csproj` is an executable compiled against an
   absolute `SdkAssemblyPath` pointing to the candidate `1.1.0`
   `MeowChan.PluginSdk.dll`. It emits `LegacySdk102CandidateProbe.dll`.

Both projects target `net10.0`, use only the explicit local assembly reference
and the .NET targeting pack, and contain no `ProjectReference`,
`PackageReference`, package feed, network, host, worker, or production-code
reference. The legacy output must not be rebuilt against the candidate DLL.

The parent freeze supplies the real assembly paths and performs the build. For
example (commands are documentation only and were not run by this worker):

```text
dotnet build LegacyPlugin/LegacyPlugin.csproj --no-restore \
  -p:SdkAssemblyPath=/absolute/released-v1.0.2/MeowChan.PluginSdk.dll

dotnet build CandidateProbe/CandidateProbe.csproj --no-restore \
  -p:SdkAssemblyPath=/absolute/candidate-v1.1.0/MeowChan.PluginSdk.dll
```

At runtime the candidate output directory must contain the candidate
`MeowChan.PluginSdk.dll`; `--plugin` is the absolute path to the resulting
`LegacySdk102Plugin.dll`:

```text
/path/to/LegacySdk102CandidateProbe --plugin /absolute/LegacySdk102Plugin.dll
```

The loader resolves the old plugin's `MeowChan.PluginSdk` reference to the
candidate assembly already loaded by the probe. It still checks the old
assembly-reference metadata (`1.0.2.0`) and the old declaration literal
(`1.0.0`) at runtime; it does not merely inspect whether similarly named types
exist.

## Result contract

Successful stdout is one JSON object with these fields:

```json
{
  "probe": "sdk102",
  "legacySdkVersion": "1.0.0",
  "candidateSdkVersion": "1.1.0",
  "legacyAssemblySha256": "<sha256 of LegacySdk102Plugin.dll>",
  "legacyLoaded": true,
  "sourceContractOk": true,
  "jsonRoundTripOk": true
}
```

The executable loads the old binary, casts it to the current `IPlugin` and
`ISourcePlugin`, calls `Describe`, calls both old source methods, validates the
returned description/catalog/thread using current `SdkValidation`, and
round-trips a returned polymorphic result through current `SdkJson`. Exit code
`0` is reserved for all seven required observations being true/valid. Exit
code `1` reports a failed load, version, cast, source validation, or JSON
observation; exit code `2` is a malformed or non-absolute `--plugin` argument.
Failure stdout retains the same bounded JSON shape with false/empty values;
only a stable diagnostic code is written to stderr.

This probe uses deterministic synthetic values only. It performs no HTTP,
file/profile discovery beyond reading the explicitly supplied plugin DLL, no
live source access, no authentication store or database access, and no retry
loop.

## Open parent gates

The parent must still perform and record the real compile and runtime gates:

- compile the legacy library against the exact released 1.0.2 SDK assembly and
  the candidate executable against the exact 1.1.0 SDK assembly;
- run the candidate executable with the unchanged legacy plugin bytes and
  retain the JSON, exit code, assembly hashes, and SDK assembly identities;
- verify the runner's isolated staging, dependency closure, and no-network
  boundary under its own gate.

Those gates are intentionally not claimed here. This artifact does not prove
PluginHost/PluginWorker behavior, AP04, reauthorization, or a complete product
upgrade.
