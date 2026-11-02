# Bundled Spanish, French and Russian Untested previews

These **AI-authored translation previews** ship as embedded assembly resources and
are available in the normal language chooser without profile-file installation.
They have not received qualified human linguistic review. English and German
remain supported built-ins. The saved language is not changed automatically.

| File | Language / UI choice | Scope |
| --- | --- | --- |
| `es.json` | `es` · Español (Untested) | Spanish, no regional review claimed |
| `fr.json` | `fr` · Français (Untested) | French, no regional review claimed |
| `ru.json` | `ru` · Русский (Untested) | Russian, no linguistic/native-UI review claimed |

The current target is all **1,353 nonempty translatable catalog strings** in this
working copy (1,355 keys total, including two startup-preview strings). Two
intentionally empty saved-search sentinels are omitted because schema v1 rejects
empty translations; their English fallback stays empty. Tests compare each pack
with the compiled English catalog and validate exact formatting contracts through
the production loader. Key coverage is **not** language-quality or complete-UI QA.
Future key additions require matching pack updates. Proper names and technical
terms may legitimately match English.

## Select and customize

Select the desired language in Settings → Interface and save normally. Merely
loading these bundles does not save settings, restart downloads, write a pack or
change the process numeric culture. `Texts.Initialize` reads their manifest
resources, not executable-relative files; embedding is intended to work for normal
and single-file builds. Actual published-platform verification remains separate.

Optional custom JSON files still load from the active profile's `LanguagePacks`
directory, next to `settings.json` (including custom `MEOWCHAN_PROFILE` selection).
A fully valid same-code custom pack overrides the bundle **in memory**, without
rewriting either file. Missing custom keys fall back to English. An invalid custom
pack warns and leaves the bundled preview usable. Multiple valid profile files of
the same code remain a duplicate error (first valid file wins); EN/DE are protected.
Remove only your own override after closing MeowChan to restore its bundled
version at next startup. Renaming a file does not change its language code. Existing
custom files must never be overwritten just to offer these bundled previews.

The previous manual-copy/opt-in-only delivery policy was explicitly superseded by
the user on 01.11.2026. Historical test results for that policy do not establish
runtime correctness of the new bundle implementation. No publication or
installation into the user's actual profile was performed by this change.

## Known English surfaces

- Several helpers still select EN/DE directly, including gallery/playback labels,
  rating/cleanup strings, command labels (including footer navigation), some
  thread-favorite and thread-row text. These bypass
  the shared catalog. They remain English for ES/FR/RU even with full key coverage.
- Semi/Ursa controls currently use German for DE and English for other languages.
  Native OS dialogs, engine/plugin messages and source/user content are separate.
- Startup failures before profile initialization use EN/DE OS-language guidance;
  a profile pack is not available at that point.

Do not translate source URLs, file paths, identifiers, keyboard letters, numbers,
format tokens or plugin permission semantics. Destructive and security messages
need particular human review: negations, pending versus completed work, permission
and ownership boundaries must retain their original meaning.

## Review status and later supported locales

Russian is included at the user’s explicit request as an **unreviewed preview**.
Mechanical JSON/key/placeholder/production-loader tests are not a Russian language
review. No Russian native-window, layout, accessibility or input QA is claimed;
ES/FR smoke results do not extend to RU. Select it only as a deliberate preview,
and report unclear translations with their visible text and catalog key if known.

### Simplified Chinese

Simplified Chinese (`zh-Hans`) is a separate candidate, **not included here**.
Inter does not establish CJK glyph coverage. Before offering it we need platform
font-fallback/rendering checks, wrapping/input tests and qualified Chinese review.
Traditional Chinese (`zh-Hant`) is distinct; do not label all Chinese locales as
Simplified. Full built-in ES/FR support also requires helper/vendor localization,
layout/accessibility QA and an explicit upgrade/collision policy for existing packs.

See [language-pack format and contracts](../README.md).
