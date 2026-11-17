# PKG6A localization plural audit

## Result

`Desktop/Localization/PluralRules.cs` already implements the relevant integer CLDR cardinal rules for `ru`, `cs`, and `pl`:

- Russian (`ru`): `one` for 1/21/101, `few` for 2–4 except 12–14, and `many` otherwise.
- Czech (`cs`): `one` for 1, `few` for 2–4, and `other` for 0 and 5+; `many` is a fractional category and is intentionally not produced because `Texts.Count` accepts whole counts.
- Polish (`pl`): `one` for 1, `few` for values ending in 2–4 except 12–14, and `many` otherwise.

The implementation also handles the existing regression contract for `pt-PT` (`0` is `other`, `1` is `one`). No source change was necessary.

## Pack blocker

The bundled packs `Desktop/Localization/Packs/ru.json`, `cs.json`, and `pl.json` contain no keys ending in any plural category suffix (`.zero`, `.one`, `.two`, `.few`, `.many`, or `.other`). Therefore there are no authoritative localized plural forms to add for these languages. `Texts.Count` correctly falls back to each pack's base key when a category-specific key is absent.

Adding guessed Russian, Czech, or Polish wording would be an unverified translation and is intentionally out of scope. A follow-up localization pass needs reviewed translations for the relevant base keys before adding category variants.

## Validation

The existing `CountAndSizeTextTests.PluralCategories` matrix covers the ru/cs/pl boundaries, including 12–14 exceptions, negative integers, and regional language tags. A focused test run could not start in this worktree because `tests/MeowChan.DesktopTests/obj/project.assets.json` is absent; restore dependencies before rerunning:

```text
dotnet restore tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --filter 'FullyQualifiedName~CountAndSizeTextTests|FullyQualifiedName~PluralCountTextTests'
```
