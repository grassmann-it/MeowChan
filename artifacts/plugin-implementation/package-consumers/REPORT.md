# R10-10: SDK / Host package consumers

Date: 2026-10-22. Source/runner handoff; final candidate execution remains open.

## Delivered

- New `samples/MeowChan.PluginHost.Consumer` references only exact Host and SDK
  NuGet versions. No project references or implicit repository project graph.
- Existing `samples/MeowChan.PluginSdk.Consumer` is reused without changes as
  the independent SDK-only consumer.
- `scripts/test-plugin-packages.py` copies both consumers outside the checkout,
  uses a fresh package/HTTP cache and CLI home, selects the repository-pinned
  SDK, and maps the three candidate package IDs exclusively to the supplied
  local feed. It owns the shared build lock for all restore/build/probe work.
- Candidate metadata, included README/license, package SHA-256, restored and
  executed DLL SHA-256, exact package versions, generated lock-file SHA-256 and
  the resolved dependency graph are recorded. SDK dependencies and
  Host/Core desktop, Roslyn or helper package dependencies are rejected.
- Host persistence probe covers empty profile, startup, pending configuration,
  stale revision rejection, reopen/startup persistence, exact grant scope,
  revocation revision and persisted pinned rollback.
- Optional `--application-directory` executes the delivered compiler and worker
  against the included SDK-only synthetic source/filter: unchanged-source cache
  hit, mapped syntax diagnostics, description/configuration, thread/catalog
  validation, duplex metadata and four-byte image callbacks, settings
  validation, process exit and owned-workspace cleanup. Fixture host services
  never perform network I/O. Compiler service and workers are disposed.

## Executed checks

- `python3 -m unittest discover -s tests -p test_plugin_packages.py -v`: **17 PASS**.
  These reject ambiguous/missing candidates, wrong identity or assemblies,
  forbidden dependencies, missing package documentation/license, ZIP duplicate
  entries, project references, reused output/lock files, package and DLL byte
  tampering, extra caches, wrong versions, cache path escape, forbidden
  transitive dependencies, SDK-to-Host/Core coupling, missing required packages
  and consumer-output DLL substitution.
- `python3 scripts/test-plugin-packages.py --help`: **PASS**.
- `python3 -m py_compile scripts/test-plugin-packages.py tests/test_plugin_packages.py`:
  **PASS**.
- Empty-feed CLI smoke: **PASS**. Exit 1, `RESULT.json` records `passed: false`
  and all three missing candidates, without invoking dotnet.
- Static C# contract review against the current integrated Host/SDK contracts:
  no definite API/compile mismatch found. Follow-up fixes use asynchronous
  compiler disposal, distinguish requested/running/failed process probes, and require
  a real worker PID before claiming process exit.
- `git diff --check`: **PASS**.

## Required final execution

No .NET restore, build or execution was performed for this source handoff. The
root integration agent supplies the final coordinated `1.0.0` SDK, Host and Core
feed and extracted application before collecting actual consumer evidence:

```sh
python3 scripts/test-plugin-packages.py \
  --feed /path/to/final-feed \
  --output /path/to/new-evidence \
  --application-directory /path/to/extracted-app
```

Do not wrap this runner in `flock /tmp/meowchan-ui-1.0-build.lock`; the runner
already acquires that lock. Without the application argument, helper integration
is explicitly `not_requested`.

Consumer compilation deliberately uses a developer SDK. Package-consumer and
optional helper results do not prove SDK-free product E2E, read-only application
installation, full official-provider behavior, GUI acceptance or native support
for untested OS/RID combinations. Those R10-10/R10-12 checks remain separate.
