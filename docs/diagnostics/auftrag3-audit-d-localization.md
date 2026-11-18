# Auftrag 3 audit D — localization

## Scope and result

Audit base: `origin/goal/gesamtprogramm-profile-a` (`89021dd7e`, 2026-11-18).
The audit covers only `Desktop/Localization/Packs/*.json` and the shared
localization source/contract files. No pack or source translation was changed.

The two non-empty reader activation keys are absent from all ten bundled packs:

- `reader.media.activation.contextChanged`
- `reader.media.activation.missing`

That is **20 real missing pack keys**: two per pack in `cs`, `es`, `fr`, `it`,
`ja`, `ko`, `pl`, `pt-BR`, `ru`, and `zh-Hans`. The English and German source
entries are present in `Desktop/Localization/ArchiveCatalog.cs:38-39`, so these
are translation-pack gaps rather than missing source keys. `Texts.T` consequently
shows the English source text when one of these keys is requested
(`Desktop/Localization/Texts.cs:182-188`).

| Pack | Missing reader keys | `savedSearch.*.None` entries | Absent `.few` siblings* |
| --- | ---: | ---: | ---: |
| `cs.json` | 2 | 0 | 66 |
| `es.json` | 2 | 0 | — |
| `fr.json` | 2 | 0 | — |
| `it.json` | 2 | 0 | — |
| `ja.json` | 2 | 0 | — |
| `ko.json` | 2 | 0 | — |
| `pl.json` | 2 | 0 | 65 |
| `pt-BR.json` | 2 | 0 | — |
| `ru.json` | 2 | 0 | 88 |
| `zh-Hans.json` | 2 | 0 | — |
| **Total** | **20** | **0** | **219** |

\* The `.few` number is the deterministic sibling-gap count for the existing
`.one` families in `cs`, `pl`, and `ru`, not a request to machine-fill 219
strings. The pack contract makes `.few` optional where the translated wording
is count-neutral and required only where the number directly governs a noun.

## Intentional empty sentinels

`savedSearch.prompt.None` and `savedSearch.confirm.None` are intentionally empty
English/German source values (`Desktop/Localization/ArchiveCatalog.cs:74,82`).
They are not missing translations:

- every pack omits both keys;
- schema v1 rejects empty or whitespace pack values
  (`Desktop/Localization/Texts.cs:362-366`);
- the production and regression contracts require the omitted lookup to remain
  the empty string (`tests/MeowChan.DesktopTests/LanguagePackAuditRegressionTests.cs:61-72`);
- the pack README records these two omissions as intentional
  (`Desktop/Localization/Packs/README.md:66-71`).

Therefore the 20 reader keys must not be discounted as another sentinel class,
and the two `savedSearch.*.None` keys must not be added to any pack.

## Source and role contract

1. **Canonical source:** `BuiltInCatalog` is the compiled English/German
   key-value source. The reader keys are declared with both source languages in
   `Desktop/Localization/ArchiveCatalog.cs:38-39`; the blank saved-search
   sentinels are also source data, but intentionally empty.
2. **Pack role:** the ten JSON files are embedded, AI-authored, untested
   language previews, not reviewed translations. This role and the chooser
   behavior are documented in `Desktop/Localization/Packs/README.md:1-10` and
   `README.md:174-190`.
3. **Lookup behavior:** a non-plural missing key falls back from the active pack
   to English (`Desktop/Localization/Texts.cs:182-188`). A missing plural variant
   falls back to that language's base form before the English fallback
   (`Desktop/Localization/Texts.cs:210-223`).
4. **Formatting contract:** pack entries must be non-empty strings, must refer to
   known source keys (or a valid plural base), and must preserve source
   placeholders (`Desktop/Localization/Texts.cs:360-372`). Any human follow-up
   must preserve placeholders and formatting, not just the visible words.

## Glossary contract

`Desktop/Localization/Packs/README.md` is the editorial glossary for the preview
packs. It defines the language-specific choices and explicitly labels the
previews as unreviewed. Relevant sections are:

- CJK glossary and no-plural guidance: `README.md:202-284`;
- Brazilian Portuguese and Czech glossary: `README.md:285-321`;
- Italian glossary and its one/other policy: `README.md:323-356`;
- Polish glossary and terminology/plural notes: `README.md:358-400`.

The glossary separates terms such as `reader`, `gallery`, `media`, `post`,
`thread`, `source`, `saved search`, and `rating`; it also preserves technical
names and search syntax where required. A follow-up translation of the reader
activation messages should use those entries rather than introducing a new
wording per pack.

## Plural contract

Plural selection is for whole counts and follows the code in
`Desktop/Localization/PluralRules.cs:6-61`:

- `ru`: `one` for 1/21/…, `few` for 2–4 except 12–14, and `many` otherwise;
  the base key is the `many` form, so a missing `.many` sibling is not a defect.
- `pl`: `one` for 1, `few` for 2–4/22–24/…, except 12–14, and `many`
  otherwise; the base key is again the `many` form.
- `cs`: `one` for 1, `few` for 2–4, and base `other` otherwise; Czech does
  not select a `.many` form for the whole-count path.

The README states the same pack-level policy and says that `.few` extras are
only needed where a number directly governs a noun
(`Desktop/Localization/Packs/README.md:16-39,66-72`). Accordingly, the 219
`.few` sibling gaps are an audit finding for human linguistic review, not a
deterministic instruction to add identical keys to every pack. Existing `.few`
coverage is 42 in `cs`, 43 in `pl`, and 20 in `ru`; the remaining sibling gaps
are 66, 65, and 88 respectively.

## Recommendation

- Keep `BuiltInCatalog`, `ArchiveCatalog`, `Texts`, and `PluralRules` unchanged;
  no deterministic source fix is necessary.
- Do not add guessed translations from this audit model. Have a qualified
  reviewer supply the two reader messages in all ten packs, using the glossary
  and preserving the source meaning/placeholders.
- In the same human review, classify the 219 `.few` candidates by whether the
  localized text genuinely changes with 2–4. Add only the reviewed variants;
  keep count-neutral base wording where the glossary/grammar allows it.
- Add a future completeness check for the two non-empty reader keys, while
  explicitly excluding the two empty `savedSearch.*.None` sentinels.

## Evidence method

The inventory was computed over the parsed `.strings` object in all ten pack
files. For the plural row, each existing `<base>.one` was paired with
`<base>.few`; absent siblings were counted by locale. The resulting totals were
`20` reader-key omissions, `0` sentinel entries in packs, and `66 + 65 + 88 =
219` absent `.few` siblings. This is an audit-only result; no pack file was
rewritten.

## Validation

- The deterministic inventory passed with `20` reader-key omissions, `0`
  sentinel entries, and `219` absent `.few` siblings.
- `git diff --check` passes for this doc-only change.
- Locked solution restore passed. The direct Release smoke build was attempted
  with `dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj
  -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false`.
- Smoke build is blocked by two unrelated target-base errors: `CS0103` for
  `_contentHeader` in `Desktop/Views/Media/MediaPage.cs:220` and `CA2000` in
  `Desktop/Views/Media/MediaPage.Selection.cs:395`. Neither is in this audit's
  localization scope, so neither was changed.
