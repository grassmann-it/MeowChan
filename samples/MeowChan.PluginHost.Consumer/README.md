# Plugin Host package consumer

This executable uses public Host and SDK APIs through exact `PackageReference`
versions. It has no `ProjectReference`. The separate existing
[`MeowChan.PluginSdk.Consumer`](../MeowChan.PluginSdk.Consumer/) remains the SDK-only
probe.

From the repository root, provide a fresh feed containing exactly one version of
each `MeowChan.PluginSdk`, `MeowChan.PluginHost` and `libmeowchan` NuGet package:

```sh
python3 scripts/test-plugin-packages.py \
  --feed /path/to/final-1.0-feed \
  --output /path/to/new-consumer-evidence
```

The runner copies the Core, SDK and Host consumers outside the repository, creates an empty NuGet
cache, pins candidate IDs to the supplied feed, restores, builds and runs them.
The Core consumer exercises the library directly without SDK/Host dependencies.
It checks package hashes, extracted and executed DLL hashes, generated lock files
and the resolved dependency graph. SDK must have no package dependencies; Host
must declare SDK and Core, without Avalonia, Roslyn or helper package dependencies.
Included package README and license files are required. External dependencies
default to nuget.org; repeat `--dependency-source PATH_OR_URL` to use other feeds.

The Host probe uses a new isolated profile to check startup selection, pending
configuration, stale-write rejection, reopening, grant scope/revocation and pinned
rollback. It never reads or changes a personal profile. Its initial synthetic
artifact exercises passive persistence APIs only.

To additionally run the delivered compiler and worker against a synthetic
single-file source/filter, provide an extracted application directory:

```sh
python3 scripts/test-plugin-packages.py \
  --feed /path/to/final-1.0-feed \
  --output /path/to/new-consumer-with-helpers-evidence \
  --application-directory /path/to/extracted-app
```

The directory must contain `plugin-compiler/MeowChan.PluginCompiler` and
`plugin-worker/MeowChan.PluginWorker` (with `.exe` on Windows), together with their
delivered dependencies. This checks source compilation, an unchanged-source cache
hit, source syntax diagnostics, worker description/configuration, thread/catalog
results, duplex metadata/image callbacks, filter evaluation and worker cleanup.
The host supplies fixture bytes directly; the `.invalid` addresses never reach a
network client. No live provider HTTP is used.

`RESULT.json`, `host-probe.json` and per-step logs are written to the requested new
output directory. An existing output directory is rejected. Temporary projects,
caches and profiles are removed when the runner exits. Without
`--application-directory`, the process probe is recorded as `not_requested`.

**The runner owns `/tmp/meowchan-ui-1.0-build.lock` on Linux** (the same filename in
the system temporary directory elsewhere). Do not wrap it in another lock of
that file. `--build-lock` can select a coordinated lock on another platform.

Consumer compilation uses the repository-pinned developer .NET SDK. These checks
prove package consumption and optionally helper integration; they do not establish
SDK-free product E2E, native acceptance for another OS/RID, read-only installation
support or the full GUI/provider workflow.
