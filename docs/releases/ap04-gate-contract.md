# AP04 gate contract (source upgrade versus packaged release)

This document describes what `tests/fixtures/upgrade-v1.0.2/ap04_run.py` may claim. It is a
run contract, not a release approval. Reconciled against source base
`63fd4190407973026d2a9ac96c479aa191325868` on 14.11.2026; this documentation pass did not
execute AP04 or verify real release packages. The runner must be used from a fresh, detached worktree
whose full commit ID is supplied as `--candidate-sha`; failed attempts are retained and a new
`--out` directory is required for the next attempt.

## Two deliberately separate claims

### Source-upgrade evidence

The upgrade side is built from the local Git object
`eec6c0169f8d52f7481ea0bf7febfd20981e97c3` and writes a **synthetic** profile with that exact
1.0.2 source. The current-side consumer and test assembly are built from the candidate SHA.
The run checks the migration, two offline restart results, writes and read-backs, plugin state,
and the byte identity of `private-tags.v1.bak` at conversion and after both read-backs.

This is not a local copy of a published 1.0.2 binary and is not a real user profile. The runner
must retain those limitations in its report. No live provider or scraping request is part of the
fixture.

### Packaged-release verification

`ap04_run.py` requires `--linux-package-dir` and `--sha256sums` together for a
package-bound run. They bind a Linux package artifact root to the candidate.
`ap04_candidate.py` checks the manifest schema, runtime, exact candidate source
commit/version and a well-formed apphost hash field. The manifest must list exactly
three artifacts, one per flavor, with these exact version/RID-derived filenames
(the default RID is `linux-x64`):

- `MeowChan-<version>-<rid>-system-vlc.tar.gz`
- `MeowChan-<version>-<rid>-bundled-vlc.tar.gz`
- `MeowChan-<version>-<rid>.AppImage`

Each asset's actual byte length and SHA-256 must match the manifest, and its hash
must match the supplied checksum file. The root allowlist is **strict already at
initial identity**, not just at final verification: exactly those three assets
and `<rid>-package-manifest.json` are allowed. If the explicitly supplied checksum
file itself is directly inside that root, its actual filename is also allowed;
if it is outside, no extra checksum file in the root is implicitly exempted.
The runner requires checksum binding even though the lower-level `package_set`
helper can be called without a checksum file for isolated checks.

Unlisted ordinary files are rejected, including files already present before the
run. Unexpected directories (even empty ones) and nested paths are rejected too;
symlink components, hardlinks and special files such as FIFOs or Unix sockets
are not valid artifact inputs. The full accepted inventory, asset hashes, manifest
hash and supplied checksum-file hash are recorded and rechecked at final
verification. A byte/inventory change, missing file or addition fails that gate;
there is no repair, ignored-file extension exemption or deferred allowlist check.
This checks the outer package files, not their internal payload inventory or an
executed apphost hash; payload/package/platform acceptance remains separate.

Those package files are **not executed** by AP04. A matching package root is dependency/artifact
provenance, not a packaged-apphost smoke test. The report therefore records
`packaged_release.verification = not-executed`; a source-upgrade result must never be described
as a packaged-release PASS. The optional `--release-artifact` pair only checks an owner-supplied
published 1.0.2 download against its `SHA256SUMS`; it is never run.

Without the Linux package root, `--allow-no-package` permits a source-only diagnosis and forces
`VERDICT=SOURCE-ONLY`, never `VERDICT=PASS`. The runner does not download or repair missing
artifacts.

## Reproducibility and safety rules

- All restore operations use the local `/home/tomka/.nuget/packages` feed and `--locked-mode`.
- Restore, build, test, generator execution, and consumer read/write smoke processes are
  serialized with `/home/tomka/.cache/claude-ssd-tmp/heavy.lock`; builds use one MSBuild node and
  disable shared compilation.
- Child `HOME`, `TMPDIR`, `DOTNET_CLI_HOME`, and XDG directories are below the fresh evidence
  output. Desktop and profile environment variables are removed; no `~/.oChan` is opened.
- The candidate, sealed build output trees, package root, and checksum file are checked again at
  final verification. A failed step remains a failed attempt even if the worktree is later clean;
  the report does not turn it green retroactively.

## Freeze-time invocation

Use a new short output directory for every attempt and substitute the exact detached candidate
and the locally supplied stable-candidate paths:

```sh
python3 tests/fixtures/upgrade-v1.0.2/ap04_run.py \
  --candidate-sha "$SHA" \
  --out /home/tomka/.cache/claude-ssd-tmp/ap4/r1 \
  --linux-package-dir /path/to/linux-distribution-candidate-linux-x64/linux-packages \
  --sha256sums /path/to/stable-candidate-"$SHA"/SHA256SUMS \
  [--release-artifact /path/to/MeowChan-1.0.2-linux-x64.AppImage \
   --release-sums /path/to/v1.0.2/SHA256SUMS]
```

Before that invocation, the owner may run the non-writing plan with `--dry-run`. If the package
root is not available, use the same command with `--allow-no-package` instead of the two package
options; that output is diagnostic only. No command in this contract is a final release PASS.
