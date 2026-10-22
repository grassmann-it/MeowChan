# 64-bit source post ordinal follow-up

Status: complete. Core preserves the SDK's nonnegative `long` ordinal contract without truncation, clamping, or a parallel shadow ordinal.

## Own commits

- `c94ea08` — `CapturedPost.Ordinal` and its constructor now use `long`; SQLite reads use `GetInt64`; the API analyzer baseline marks the historical `int` constructor/getter removed and records their `long` replacements.
- `31654cc` — seven focused regression cases for full-width ordinals, restart, archive merge, passive reading, pagination, quote resolution, offline HTML export, legacy integer/JSON values, explicit decimal post-ID ordering, and negative value rejection.
- This report is delivered as a separate documentation commit.

The SQLite column was already an INTEGER with a nonnegative constraint, so this change requires no schema migration. Old Int32-range SQLite values and the existing JSON shape remain readable. The public API signature change is intentional; Root owns the Breaking documentation in `e7471b8` and release baseline freezing.

## Validation

All .NET builds/tests used `/tmp/meowchan-ui-1.0-build.lock`, disabled node reuse, and isolated temporary profiles.

The first broader run passed 70 of 71 cases, including every new ordinal test. Its only failure was the existing legacy-migration regression still expecting schema version 2 after the independently integrated schema version 3 change. The existing correction was imported from adapter commit `9611017` (Root integration `d8157a5`) as local dependency `cc33ac8`; this is not part of this agent's delivery.

After that correction, the requested targeted rerun passed **8/8**, with no failures or skips:

```sh
flock --close /tmp/meowchan-ui-1.0-build.lock env MSBUILDDISABLENODEREUSE=1 \
  dotnet test tests/MeowChan.Tests/MeowChan.Tests.csproj -c Release -nodeReuse:false \
  --filter 'FullyQualifiedName~ArchiveOrdinalTests|FullyQualifiedName~LegacyReaderRemainsReadOnlyAndWriterAtomicallyMigratesPostsReferencesAndDecisions' \
  --logger 'trx;LogFileName=archive-ordinals.trx' \
  --results-directory artifacts/plugin-implementation/runtime-ordinal/tests
```

Evidence: `artifacts/plugin-implementation/runtime-ordinal/tests/archive-ordinals.trx` (generated test artifact, not committed). The existing xUnit2031 warning in `BoardAdmissionTests.cs` is unrelated. Root runs the final integrated Core suite.

## Scope

No Host or Worker runtime code changed in this follow-up. The adapter owner incorporated the production commit and removed its checked Int32 cast; their separate end-to-end adapter coverage remains in their delivery. This report claims only the local validation above.
