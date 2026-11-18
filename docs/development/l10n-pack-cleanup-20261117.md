# Localization pack cleanup (2026-11-17)

Base: `origin/goal/gesamtprogramm-profile-a` at `3c7ee6635`.

## 1. `Post filters…`: investigation before edits

**Verdict: false positive in the supplied audit, not a literal-key call-site bug.**
The only apparent tuple with that first element is the prose comment
`// ("Post filters…", "Hide post", "Show post again")` in `PostFilterCatalog.cs:5`.
It is not catalog data. The supplied line-anchored `parse_catalogs.py` does
not reproduce this alleged key on the current base either; a less restrictive
mechanical scan would mistake that comment for a tuple.

The actual path is `ArchivePage.PostFilter.cs`: `PostFiltersCommand` is
`reader.post-filters`, passed to `RegisterViewOption`. `CommandCatalog.cs` maps
that command to **`command.reader.post-filters`**, already defined in
`ReaderViewCatalog.cs` and translated in all ten packs. The English label in
`CommandCatalog` is its intentional English fallback, not an argument to
`Texts.T`. No call site or real key needs changing. This report precedes pack edits.

The fetched base has advanced beyond the supplied snapshot: a verified,
line-anchored tuple scan finds 4,926 actual keys, not the reported 4,922;
CJK lacks 95 selectable-in-English `.one` keys, not 98. Empty sentinel
keys and locale-specific plural variants need semantic handling, not deletion
or insertion based solely on set difference.

## 2. Allegedly stale keys: retain all; no deletion is safe or needed

The supplied audit omitted **real catalogs outside `Desktop/Localization`**:
`Desktop/Booru/BooruUiCatalog.cs` and `BooruCodes.cs` (`BooruCodeCatalog`,
constant-prefixed keys). It also omitted the `QuickTagSettingsCatalog.GroupKey`
constant and misclassified locale-specific number forms. Including these yields
**5,182 built-in keys** (including two empty sentinels), with **zero unknown
pack keys** once supported number variants of real base keys are recognized.

Evidence from code-name searches:

- `booru.ui.add.*`: `Desktop/Booru/MainWindow.BooruAdd.cs:40-66` and
  `BooruAddUrl.cs:68` request them; defined in `BooruUiCatalog.cs:133-138`.
- `booru.ui.cancel`: `Views/Booru/BooruWorkbenchView.cs:529,815,818,1345`.
- Remaining `booru.ui.*`: `BooruUiCatalog` is concatenated into `BuiltInCatalog`;
  `BooruText.RequiredKeys` lists UI contracts; `BooruText` also constructs keys
  for query issues and watcher rejections, so literal-call grep alone is insufficient.
- `booru.watcher.problem.*`: defined with `P + code` in `BooruCodes.cs:116-188`,
  dynamically requested by `Localization/BooruMessages.cs:81-86` and
  `BooruProductionPorts.cs:542`.
- `budgets.group.quickTag`: defined by `GroupKey`, used in settings.
- `.few` and extra `.one`: `Texts.Count` selects these dynamically through
  `PluralRules.Category/Variant`. For example `archive.postsCount.few` is reached
  by `ArchivePresentation.cs:25` for Czech/Polish/Russian counts 2–4, and
  `limit.savedSearches.one` by `StoreLimitText.cs:24` for selecting languages.
  A key need not have an English variant to be valid (`Texts.cs:365-368`).

The complete 312-name retained candidate inventory is in
[`l10n-pack-retained-keys-20261117.tsv`](l10n-pack-retained-keys-20261117.tsv).
No keys removed; all ten pack entry counts stay unchanged. Existing coverage
assertions already reject truly unknown or unselectable variants; none weakened.

## 3. `savedSearch.*.None`: intentionally empty sentinels, not untranslated text

Do **not** insert nonempty translations of “None”. Both English and German
values in `ArchiveCatalog.cs:72,80` are `""`. The dialog constructs these keys
from `EditKind.None` while hiding its editor (`SavedArchiveSearchesDialog.axaml.cs:340-347`).
The correct translation in every language is no text. Missing pack entries
already return that exact empty built-in value. Adding empty strings instead
would reject the **entire pack** (`Texts.cs:362-363`), and adding the word “None”
would change the sentinel's meaning. Therefore no pack entries were added and
no loader/schema/assertion was weakened. Added ten regression cases in
`LanguagePackAuditRegressionTests.NoEditSentinelsRemainEmptyInEveryLanguageWithoutInvalidPackEntries`
to verify the actual localized lookup, with and without the unused editor-name
argument, plus absence of invalid empty entries and loader warnings.

## 4. CJK number forms: intentionally absent; keep packs unchanged

`PluralRules.cs:42-49` always returns `other` for ja/ko/zh.
`Texts.Count` first selects the current catalog when the base key exists
(`Texts.cs:214-216`), then returns its base text for `other` (`219-220`).
All 95 English singulars on this base have translated CJK base texts. Thus
count 1 does **not** silently fall back to English. No `.one` entries added.
Updated the pack README's outdated “25 forms in CountCatalog” statement and
added three regression theories checking **every** English singular's localized
base at counts 0, 1, 2, 4, 21, 100 (1,710 count comparisons).

## 5. Genuine English leftovers

Translated **50 values** in all ten packs, without changing key sets or metadata:

- The common six-key gap in es/fr/ru/cs/pl/it/pt-BR: hours, the Danbooru
  Retry-After maximum and its hint, reset-cooldown button/hint/completion notice
  (**42 translations**). CJK already had these translated. The text preserves
  the 60-second floor, 1–168-hour range, 24-hour default, Danbooru-only scope,
  and the promise not to start a check/download or remove the user's pause.
- Three CJK post-number labels (`reader.postNumber`), four remaining English
  theme-preview chip labels (es/it/pl/pt-BR), and the Polish transfer column label.
- Added ten common-core regression cases that require actual localized values
  while preserving the exact product and identifier names.

**Deliberately unchanged:** Booru, Danbooru, MeowChan, URL, hash identifiers
(SHA-256/MD5), GIF and binary units; physical keycap names (Ctrl/Shift/Home/etc.);
format-only strings; protocol name `Retry-After`; provider-owned rating/category
vocabulary (see the existing pack README); established community/computing loans
such as board/thread/tag/plugin, Italian media/file/video/layout, Portuguese
downloads/status/tags, and the existing French focus-mode name. A same-spelling
local word is not untranslated English: e.g. Spanish Error/No/Manual/Original,
French Source/Action/Version/Volume/Notification, Czech Text/Disk/Export, Polish
Folder/Format. No brands were replaced, and no attempt was made to force these
legitimate matches below an arbitrary “untranslated” threshold. Previews remain
Untested and require qualified human review.

### Counts before → after (same current-base key set)

| Pack | Entries before → after | Exact English matches before → after | Valid extra plural forms | Raw missing keys |
| --- | ---: | ---: | ---: | ---: |
| cs | 5,229 → 5,229 | 99 → 93 | 49 | 2 |
| es | 5,180 → 5,180 | 91 → 84 | 0 | 2 |
| fr | 5,180 → 5,180 | 126 → 120 | 0 | 2 |
| it | 5,186 → 5,186 | 134 → 127 | 6 | 2 |
| ja | 5,085 → 5,085 | 59 → 58 | 0 | 97 |
| ko | 5,085 → 5,085 | 54 → 53 | 0 | 97 |
| pl | 5,232 → 5,232 | 101 → 93 | 52 | 2 |
| pt-BR | 5,186 → 5,186 | 148 → 141 | 6 | 2 |
| ru | 5,206 → 5,206 | 66 → 60 | 26 | 2 |
| zh-Hans | 5,085 → 5,085 | 49 → 48 | 0 | 97 |

Exact matches total **927 → 877** (all entries, including format-only strings).
The two raw missing keys are the empty saved-search sentinels; CJK additionally
omits 95 intentionally unselectable singulars. **Actionable missing keys: 0 → 0;
unknown/stale keys: 0 → 0; duplicate keys: 0 → 0; placeholder mismatches: 0 → 0.**
The audit's “256–308 stale keys” and “three universally missing keys” are not
valid descriptions of the runtime catalogs. Deleting/inserting them mechanically
would break localization rather than clean it up.

## Validation and delivery

Only localization/pack coverage tests were run; no full-solution build command
and no GUI smoke. The test project built its normal references. Existing compiler
CS0419 and Avalonia private-API/resource-constructor warnings were nonfatal.

Broad localization run:

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --filter 'FullyQualifiedName~LocalizationTests|FullyQualifiedName~BundledLanguagePack|FullyQualifiedName~PluralCountTextTests|FullyQualifiedName~PreviewLanguagePackTests|FullyQualifiedName~LocalSearchPackTranslationContractTests' \
  -m:1 -p:UseSharedCompilation=false --logger 'console;verbosity=normal'
```

```text
Test Run Successful.
Total tests: 508
     Passed: 508
 Total time: 4.5712 Minutes
```

Final focused run against translation commit `d35a0ceb5`, including all 23 new
regression cases and all ten pack completeness cases:

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore \
  --filter 'FullyQualifiedName~LanguagePackAuditRegressionTests|FullyQualifiedName~BundledLanguagePackCompletenessTests|FullyQualifiedName~BundledLanguagePackTests|FullyQualifiedName~PluralCountTextTests|FullyQualifiedName~LocalSearchPackTranslationContractTests' \
  -m:1 -p:UseSharedCompilation=false --logger 'console;verbosity=normal'
```

```text
Test Run Successful.
Total tests: 120
     Passed: 120
 Total time: 43.8493 Seconds
```

Logs: session scratchpad `l10n-tests-initial.log` and `l10n-tests-final.log`.
Independent before/after JSON checks also found no duplicate/unknown keys or
placeholder-contract differences; every one of the 50 changed values was
previously byte-identical to its English source. `git diff --check` passed.

Steps 1–5 were committed and pushed individually in order (`65356c835`,
`fe48ca051`, `9e44b219e`, `9860cb6ff`, `d35a0ceb5`).
[PR #37](https://github.com/grassmann-it/MeowChan/pull/37) was opened after step 2
against `goal/gesamtprogramm-profile-a`. No force push or assertion weakening.

## 6. Current-goal follow-up: true missing keys stay explicit

The current `origin/goal/gesamtprogramm-profile-a` audit base is `866ad8ea2`. Since the
key-set counts recorded above, the reader activation contract added two non-empty source
keys in `Desktop/Localization/ArchiveCatalog.cs`:

- `reader.media.activation.contextChanged` — refusal when the reader context changed or is
  no longer available; the user is told to focus the post and retry.
- `reader.media.activation.missing` — refusal when the saved original is no longer
  available locally.

Both are ordinary non-counted strings with no format placeholders. Their source role and
terminology are clear in the English/German catalog, but no reviewed translations exist in
any of the ten preview packs. The packs therefore omit exactly these two keys each
(**20 true missing pack entries total**) and deliberately fall back to English at runtime.
This is a known, bounded gap—not a stale key, not a `savedSearch.*.None` sentinel, and not
a reason to copy English into a non-English resource.

The pack parity tests now enforce this distinction: every pack must have exactly the two
allowlisted missing keys, no other selectable source key may be absent, no pack may add a
value for either key without updating the audit, and the resolver must return the English
fallback for both. The same tests continue to reject duplicate/unknown keys, invalid
plural variants, whitespace-only values, and placeholder mismatches.

The saved-search no-edit sentinels remain separate: `savedSearch.prompt.None` and
`savedSearch.confirm.None` are empty in the built-in catalog, absent from every pack, and
resolve to the empty string. They must not be filled with a translated “None” label.

No resource translation was invented in this follow-up. A future translation change needs
qualified review against the source role, the glossary terms *reader context*, *focus the
post*, *saved original*, and *available locally*, plus the no-placeholder contract before
removing either allowlist entry.
