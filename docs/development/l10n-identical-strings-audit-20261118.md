# Localization: identical and duplicated strings audit (Paket 6D)

Date: 2026-11-18
Scope: oChan Package 6D — identical localization strings
Base: `origin/goal/gesamtprogramm-profile-a` at `866ad8ea2`

## Result

No translation values were changed. The audit found no duplicate JSON properties,
no duplicate source keys, and no proven untranslated value that justifies an
artificial translation. The existing preview packs remain **Untested**; an exact
English match is not, by itself, evidence of a linguistic defect.

The audit compares every bundled pack with the compiled English source catalog
(`BuiltInCatalog.English`). The source inventory contains **5,196 unique keys**;
two are deliberately empty saved-search sentinels and are not representable in a
pack. Pack-specific plural variants are compared with their base source key and
are not treated as duplicate keys.

## Inventory against sources

| Pack | Pack entries | Exact English values | Repeated-value groups | Entries participating in repeated groups |
|---|---:|---:|---:|---:|
| `cs` | 5,240 | 93 | 374 | 997 |
| `es` | 5,192 | 84 | 304 | 827 |
| `fr` | 5,192 | 120 | 312 | 851 |
| `it` | 5,198 | 126 | 328 | 901 |
| `ja` | 5,091 | 58 | 328 | 966 |
| `ko` | 5,091 | 53 | 331 | 956 |
| `pl` | 5,241 | 93 | 389 | 997 |
| `pt-BR` | 5,198 | 141 | 311 | 849 |
| `ru` | 5,218 | 60 | 384 | 997 |
| `zh-Hans` | 5,091 | 48 | 332 | 950 |
| **Total** | **51,752** | **876** | — | — |

The total-entry cell is intentionally not used as a source invariant because the
packs have language-specific plural forms and intentionally omitted CJK `.one`
forms. The exact per-pack counts are the invariant.

The test `IdenticalLocalizationStringsAuditTests` stores a SHA-256 fingerprint of
each exact-match set (`key`, tab, source-equal value) and recomputes it from the
embedded JSON and `BuiltInCatalog.English`. This prevents a future newly copied
English value from being silently accepted without an explicit audit update.

## Why the equal values remain

The audited equal values are source-backed, context-specific values in these
categories:

- **Provider, product, protocol and format vocabulary:** `Booru`, `Danbooru`,
  `MeowChan`, `URL`, `SHA-256`, `MD5`, `GIF`, `1:1`, `(OP)`, `Ctrl`, `Shift`,
  key names, units, and format-only templates.
- **Names and technical identifiers:** provider ratings/categories, source tags,
  keyboard names, IDs, hash labels, and placeholders are not translated or
  normalized. Their values are part of the source/call-site contract.
- **Legitimate same-spelling local words and short UI forms:** examples include
  `Board`, `Source`, `Action`, `Version`, `Import`, `Original`, `Manual`,
  `Active`, `Status`, `Tags`, and `Media` where the selected language uses the
  same established UI term. An English-looking spelling alone is not a reason to
  invent a replacement.
- **Shared formatting fragments:** values such as `{0}: {1}`, `{0} · {1}`,
  `{0} → {1}`, `'{0} /{1}/ … SHA-256: {5}'`, and unit-bearing templates are
  intentionally reused by multiple source keys. The keys remain separate because
  their call sites, placeholders, and future translation contexts are separate.

These classifications were checked against the defining catalog source files and
call-site families, including `Desktop/Booru/BooruCodes.cs` for dynamically
prefixed watcher keys and `QuickTagSettingsCatalog.GroupKey` for its constant key.
No pack key was merged, renamed, or removed to make repeated values disappear.

## Duplicate-key findings

- Raw JSON property audit: **0 duplicate properties** in all ten embedded packs,
  including top-level metadata and `strings` objects.
- Source inventory: **0 duplicate keys** in the compiled English catalog.
- Repeated localized values: present as listed above, but each is attached to a
  distinct source key. These are not duplicate records and are retained.
- Empty values: the two source sentinels (`savedSearch.confirm.None` and
  `savedSearch.prompt.None`) remain absent from packs as required by the loader;
  they must not be replaced with the word “None”.
- CJK plural omissions: `zh-Hans`, `ja`, and `ko` intentionally omit English
  `.one` variants because their plural rule selects the translated base text for
  every count. This is not a missing or duplicated translation.

## Tests

Focused audit command:

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --no-restore \
  --filter 'FullyQualifiedName~IdenticalLocalizationStringsAuditTests|FullyQualifiedName~BundledLanguagePackCompletenessTests|FullyQualifiedName~LanguagePackAuditRegressionTests' \
  -m:1 -p:UseSharedCompilation=false --logger 'console;verbosity=normal'
```

The focused audit/regression run passed: **34/34** tests (the new audit plus the
existing localization regression tests). The broader pre-existing completeness
suite was also invoked from the same test project, but is not green on the base
branch: **10** bundled-pack cases fail because the baseline expects
`reader.media.activation.contextChanged` and `reader.media.activation.missing`,
which are absent from the current source/pack inventory. This audit does not
change product strings or unrelated completeness fixtures to mask that blocker.

The new test is deliberately limited to source-backed inventory, duplicate JSON
properties, exact-match fingerprints, and the existing catalog uniqueness count.
It does not translate strings or impose a blanket “must differ from English” rule.

## Change boundary

Only this audit document and the focused audit test are in scope. No product code,
localization pack value, lockfile, smoke fixture, generated artifact, profile data,
or forbidden file was changed. No merge was performed.
