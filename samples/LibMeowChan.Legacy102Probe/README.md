# Core 1.0.2 binary migration probe

This directory contains the Core half of the offline `core102` migration probe. It
is an executable API-behavior check, not AP04 and not a real-user profile upgrade.
Both consumers create a private `ProfileLease`, `SessionStore`, `WorkStateStore`
and `Registry` with an empty provider list and no download work. The profile path is
supplied by the caller; the probe never reads a personal profile, an auth store or a
live source.

## Projects and source pin

| Project | Output assembly | Assembly used to compile it | Lifecycle under test |
|---|---|---|---|
| `LegacyConsumer/LegacyConsumer.csproj` | `LegacyCore102Consumer.dll` | `CoreAssemblyPath` must point at the released 1.0.2 `libmeowchan.dll` from source pin `eec6c0169f8d52f7481ea0bf7febfd20981e97c3` | Published old composition, ending in `await session.RestoreAsync()` |
| `MigratedConsumer/MigratedConsumer.csproj` | `MigratedCore110Consumer.dll` | `CoreAssemblyPath` must point at the current candidate `libmeowchan.dll` | Current composition, ending in `await session.StartAsync(lease)` |

The legacy project is intentionally compiled only against the supplied released
assembly. It is not recompiled against the candidate to manufacture compatibility.
The candidate run reuses the resulting old executable bytes and changes only the
runtime Core assembly in the isolated runner directory.

## Local inputs and properties

The projects have no `ProjectReference`, `PackageReference` or package feed. The
runner supplies these MSBuild properties:

- `CoreAssemblyPath`: one existing absolute `libmeowchan.dll`; this explicit
  reference wins over every other file.
- `ProbeDependencyDirectory` (optional): the local directory containing the
  matching compile/runtime closure for that Core assembly. Every sibling `.dll`
  there is referenced/copied locally except a duplicate `libmeowchan.dll`; other
  files are copied as runtime data/native assets. This is intentionally a local
  dependency directory, not a restore source.

For each of the released 1.0.2 and current candidate inputs, the dependency
closure is the Core assembly plus the matching files named by its `.deps.json`:
`Microsoft.Data.Sqlite.dll`, `Newtonsoft.Json.dll`, `NodaTime.dll`, `Serilog.dll`,
`SQLitePCLRaw.core.dll`, `SQLitePCLRaw.batteries_v2.dll`,
`SQLitePCLRaw.provider.e_sqlite3.dll`, and the platform-native `e_sqlite3` asset.
For a plain local-reference probe, place the active platform-native library next
to the executable in the supplied runtime input (for example `libe_sqlite3.so`
for Linux x64). A local-reference `.deps.json` does not inherit the package's RID
runtime-target graph. Keeping the library only under `runtimes/linux-x64/native/`
is therefore insufficient for this probe's native loader. Preserve the original
package-relative file and record the hash of the side-by-side copy; do not use a
different system SQLite as a substitute. Windows/macOS need their matching native
asset and separate native validation.

The runner copies only each probe's own executable, deps/runtimeconfig and debug
artifacts over that selected runtime closure. It never lets an old copy-local
`libmeowchan.dll` from the legacy build overwrite the candidate runtime.

The .NET 10 shared framework is supplied by the SDK/runtime and is not a probe
input. The old and current release locks pin the managed package versions; the
runner must provide the real files and must not fetch them from the network.

The projects deliberately opt out of inherited package-lock/audit behavior while
retaining normal SDK framework compilation. A parent/runner may use an empty local
feed if the SDK requires a framework-only restore; no package installation is part
of this probe.

## CLI contract

Both executables require one absolute private profile path:

```text
dotnet LegacyCore102Consumer.dll --profile /absolute/private/profile
 dotnet MigratedCore110Consumer.dll --profile /absolute/private/profile
```

The legacy executable additionally accepts
`--expect-startup-admission-rejection`; use it only when the **same legacy
executable bytes** run with the 1.1.0 candidate Core assembly:

```text
dotnet LegacyCore102Consumer.dll \
  --profile /absolute/private/profile \
  --expect-startup-admission-rejection
```

Every invocation writes exactly one final JSON object to stdout. Successful
observations are:

- released old Core + old consumer: `probe=core102`, `started=true`,
  `startupAdmissionRejected=false`, `exceptionType=null`, exit `0`;
- old consumer bytes + candidate Core with the expectation flag:
  `started=false`, `startupAdmissionRejected=true`,
  `exceptionType="System.InvalidOperationException"`, exit `0`;
- migrated consumer + candidate Core: `probe=core110`, `started=true`,
  `startupAdmissionRejected=false`, `exceptionType=null`, exit `0`.

The candidate rejection is accepted only for the documented message from the
1.1.0 admission guard (`Await Registry.StartAsync(lease) before restoring or
writing to a new profile-bound session.`) and the exact exception type. A loader
failure, arbitrary exception, or other profile error is non-zero and is never a
compatibility pass. The probe does not print exception messages or profile paths.

`StartAsync` performs current recovery/restore and activation without any provider
or network action because the provider list is empty. This proves the old binary
startup boundary and the current migration composition only; it does **not** prove
full Host/Worker/SDK/reauthorization behavior, AP04 upgrade policy, GUI behavior,
network behavior or release packaging.
