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
