# C5 packaging-freshness test slice

Branch: claude/wave7-c5-packaging-freshness-20261113. Only `tests/test_plugin_packaging.py` changed
(no script, doc or release-note changes; `scripts/package-plugins.py` untouched).

## What was added (10 tests, 18 -> 28)
- Contract counts/types: 24 bundles = 16 Chan + 1 Danbooru + 5 filters + 2 blocked scaffolds
  (AnonImageboards, GuroCx); 21 editable sources; no duplicates.
- Blocked scaffolds are exactly the example sources declaring `PluginDeclarationStatus.Blocked`.
- `plugins/Examples/*/*.cs` on disk equals `SOURCES` (stale/extra example fails).
- Plugin/product version distinction: `Directory.Build.props` Version and `SdkContract.Version` are parsed
  separately; manifest `SchemaVersion` must be the integer 1 (product/SDK version strings rejected);
  verifier report carries no product version.
- Report vs manifest/hash output consistency (counts, sdk_sha256 == worker SDK digest == toolchain, key set).
- Stale 25th manifest / stale top-level file rejected; removing one bundle per type is rejected.
- Stale toolchain fingerprints, duplicate entries, malformed hashes, stale assembly hash rejected.
- Hash helpers deterministic/order-independent; CLI `--report` is byte-identical across runs and absent on failure.

## Commands (no build, no network)
    python3 -m unittest tests.test_plugin_packaging      # 28 tests, OK
    python3 -m unittest discover -s tests -p 'test_plugin_packaging.py'

## Integration notes
- Adding/removing a bundle requires updating PLUGINS/SOURCES in `scripts/package-plugins.py` plus the
  FILTERS/BOORU/BLOCKED constants in the new tests; the failures name the drifted contract.
- `scripts/test-plugin-packages.py` needs `--feed/--output` (builds); intentionally not run.
- Merge conflicts are possible only in the test file's tail before `test_cli_reports_failure_without_repairing_package`.
