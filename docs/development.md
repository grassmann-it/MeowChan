# Developing MeowChan

Use the .NET SDK version pinned in [global.json](../global.json). Build and run
from the repository root.

```sh
dotnet restore MeowChan.sln -p:Configuration=Release --locked-mode
dotnet build MeowChan.sln -c Release --no-restore
dotnet run --project MeowChan.csproj -c Release --no-build --no-restore
```

- [Repository layout](development/repository-layout.md)
- [Analyzer policy and baseline maintenance](analyzer-baseline.md)
- [Desktop smoke harness](../tests/MeowChan.DesktopSmoke/README.md) and [package acceptance boundary](smoke-release-boundary.md)
- [Booru test coverage](development/booru-test-matrix.md)
- [Plugin development](plugins/README.md)
- [Platform requirements and dependency updates](platform-support.md)

## Reviewing translations that match English

The desktop tests compare each bundled pack's embedded JSON with the compiled English catalog.
An exact match uses the English text for that key, or the base key for a pack-specific plural
variant with no English entry of its own. The audit rejects duplicate JSON properties.

Run the audit and pack completeness checks after editing or merging translations:

```sh
xvfb-run -a dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --filter 'FullyQualifiedName~IdenticalLocalization|FullyQualifiedName~BundledLanguagePackCompleteness'
```

The failure lists the pack and affected keys. Translate an unintended English copy. For an
intentional match, add the key and a concrete reason to that pack's
[`LocalizationAudit/<code>.json`](../tests/MeowChan.DesktopTests/LocalizationAudit) test resource.
Remove an exception once the translation differs from English or its key is removed. Add an
allowlist file, which can be an empty JSON object, when adding a bundled pack.

Merges that only add or edit translated strings need no audit update. Resolve any allowlist
conflict by reviewing the affected keys and their reasons, then rerun the tests. The old
`/tmp/audit-resolve.py` count and digest merge helper is obsolete for this audit.

## Source-text tests

See the [source-text test inventory](development/source-text-tests-inventory.md)
for the tests that inspect product or smoke source and the assessment of each check.
