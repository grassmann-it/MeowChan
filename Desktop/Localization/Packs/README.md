# Bundled Untested language previews

These **AI-authored translation previews** ship as embedded assembly resources and
are available in the normal language chooser without profile-file installation.
They have not received qualified human linguistic review. English and German
remain supported built-ins. The saved language is not changed automatically.

Every `Packs/<code>.json` is embedded by one wildcard in `MeowChan.csproj` and
discovered by `Texts.BundledPackCodes()` from its resource name: **the file name is
the language code**, so adding a pack needs no code change. The file's `"code"` must
be exactly that name in .NET's canonical spelling (`pt-BR`, not `pt-br` or `pt_BR`;
`zh-Hans`, not `zh-CN`), and `"nativeName"` must be exactly the chooser name below
(`BundledLanguagePackCompletenessTests.ExpectedNativeNames`). The chooser lists
English, German, then the bundled packs in ordinal code order.

| File | Code · UI choice | Plural keys besides the base key | Scope |
| --- | --- | --- | --- |
| `es.json` | `es` · Español (Untested) | `.one` | Spanish, no regional review claimed |
| `fr.json` | `fr` · Français (Untested) | `.one` (0 and 1) | French, no regional review claimed |
| `ru.json` | `ru` · Русский (Untested) | `.one`, `.few`; base = many | Russian, no linguistic/native-UI review claimed |
| `zh-Hans.json` | `zh-Hans` · 简体中文 (Untested) | none (no `.one` keys) | Simplified Chinese (mainland usage) |
| `ja.json` | `ja` · 日本語 (Untested) | none (no `.one` keys) | Japanese |
| `ko.json` | `ko` · 한국어 (Untested) | none (no `.one` keys) | Korean |
| `pt-BR.json` | `pt-BR` · Português (Brasil) (Untested) | `.one` (0 and 1) | Brazilian Portuguese |
| `cs.json` | `cs` · Čeština (Untested) | `.one` (1), `.few` (2–4); base = other | Czech |
| `it.json` | `it` · Italiano (Untested) | `.one` (1) | Italian |
| `pl.json` | `pl` · Polski (Untested) | `.one` (1), `.few` (2–4, 22–24, … not 12–14); base = many | Polish |

The plural column follows the CLDR cardinal rules for whole counts in
`PluralRules`. A pack may only contain variant keys its language can select: the
completeness test rejects any `.one` in zh-Hans/ja/ko, a `.many` in cs (its CLDR
"many" is for fractions only) or a `.few` in pt-BR/it. zh-Hans, ja and ko therefore
cover the English keys minus the 25 `<key>.one` texts of `CountCatalog` and use
the base text for every number.

### CJK fonts (system dependency, nothing bundled)

Inter (UI) and the bundled Noto Sans subset (post bodies) have no CJK glyphs, and
the full Noto Sans CJK fonts (about 20 MB per weight) are too large to bundle. Both
font chains therefore end with the platform's installed CJK families
(`Desktop/ReaderFonts.cs`), and the **UI language decides which regional family
answers first**, because the same Han character is drawn differently in each:

| UI language | Linux / BSD | Windows | macOS |
| --- | --- | --- | --- |
| zh-Hans (SC) | Noto Sans CJK SC | Microsoft YaHei UI, Microsoft YaHei | PingFang SC |
| ja (JP) | Noto Sans CJK JP | Yu Gothic UI, Yu Gothic, Meiryo | Hiragino Sans |
| ko (KR) | Noto Sans CJK KR | Malgun Gothic | Apple SD Gothic Neo |

The other regions follow (a Korean name in a Chinese post still renders), as do
Traditional Chinese (Noto Sans CJK TC, Microsoft JhengHei, PingFang TC) and, on
Linux, the same design under the name "Source Han Sans". A non-CJK UI language
follows the OS language, else Japanese first. Windows and macOS ship these fonts.
**Linux needs Noto Sans CJK installed** (`noto-fonts-cjk` on Arch/Manjaro,
`fonts-noto-cjk` on Debian/Ubuntu, `google-noto-sans-cjk-fonts` on Fedora);
without any CJK font the last resort is fontconfig's per-character match, which
shows boxes when no installed font has the glyph. The order is refreshed live on
a language switch (`ReaderFonts.Apply`), both in the UI/reading families and in the
FontManager's global CJK fallback list, which Avalonia consults first.

The current packs cover all **nonempty translatable catalog strings** in this
working copy, including the command, rating and startup-preview catalogs. Two
intentionally empty saved-search sentinels are omitted because schema v1 rejects
empty translations; their English fallback stays empty. Tests compare each pack
with the compiled English catalog and validate exact formatting contracts through
the production loader. Key coverage is **not** language-quality or complete-UI QA.
Future key additions require matching pack updates. The singular keys `<key>.one` of `CountCatalog` are part of that coverage; the Russian pack adds `<key>.few` (2-4, 22-24, ...) where its text needs it, and also `<key>.one` where English has no singular form (the `limit.*` texts). Texts of the form "Label: {0}" read the same for every number and need no variants; a text that puts a noun after the number does. Such `.few` keys are optional extras that exist only in the pack, not in the English catalog; user packs may omit all plural variants. The keys of `InlineTextCatalog` (formerly hard-coded English/German pairs in product code) were machine-translated into es/fr/ru and have had no human proofreading yet. Proper names and technical
terms may legitimately match English. The About-window photo credit and logo
description (`about.photoCredit`, `about.photoCreditFlickr`, `about.logoDescription`,
`about.logoSimba`, `about.logoMila`)
are machine translations in es/fr/ru too; the photographer's and business name are
passed in as a placeholder and are never translated.

The es/fr/ru entries of `BooruStatusCatalog` (keys `booru.failure.*`,
`booru.deferred.*`, `booru.watcher.*`, `booru.query.*`, `booru.post.*`,
`booru.original.*`, `booru.rating.*`, `booru.tags.*`, `booru.tagCategory.*`,
`booru.mediaRole.*`, `booru.finding.*`, `booru.field.*`, `booru.post.deleted`,
`booru.post.banned`) and `plugins.capability.postSearch` are likewise **unreviewed
machine translations**. They name Danbooru provider states, wait times after a rate
limit and the fact that a missing original is not replaced by a smaller version, so
they need qualified human review before these languages count as reviewed for the
booru feature. Rating names (General, Sensitive, Questionable, Explicit) and tag
categories follow Danbooru's own vocabulary and may legitimately resemble English.
The English diagnostic messages that the host and the Danbooru plugin put in a
failure are not shown to the user; `BooruMessages` maps their stable codes to these
catalog texts. The plugin's own information text (`access`) is plugin-provided English
and is not part of the catalogs. Texts of the booru screens (B6) use `booru.ui.*`.
The scheduler, port and window codes of the Danbooru window (`BooruCodeCatalog`, further
`booru.watcher.problem.*` keys) and the watcher "Restart from now" texts
(`booru.ui.watcher.restart*`, `booru.ui.watcher.msg.restarted`) are unreviewed machine
translations in es/fr/ru as well. So are the texts of the post link actions
(`booru.ui.post.open*`, `booru.ui.post.copy*`, `booru.ui.link.*`,
`booru.ui.watcher.latest*`) and of the watcher notification (`booru.ui.notify.*`).

The es/fr/ru entries of `BooruMediaCatalog` (keys `booru.media.*`: source badge, post
details of downloaded Danbooru media, provider data and source tags shown apart from
the user's own tags, finding labels and the statement that own tags are not available
for provider media yet) are likewise **unreviewed machine translations**. Provider
names and the word Danbooru legitimately match English.

The es/fr/ru texts of the bundled repair/reselection review dialog's batch
approval are likewise **unreviewed machine translations of consent texts**:
`plugins.approval.submit`, `plugins.approval.submitOne`, `plugins.approval.reload`,
`plugins.approval.policy`, `plugins.approval.saving`, `plugins.approval.stopping`,
`plugins.approval.stopped`, `plugins.approval.reviewAgain`,
`plugins.approval.needsReview`, `plugins.approval.failed`,
`plugins.approval.checkStore`, `plugins.approval.refreshFailed`,
`plugins.approval.receipt` and `plugins.approval.state.*` (`Saved`, `NotExecuted`,
`NeedsReview`, `Failed`, `CheckStore`). They state what is and is not saved,
stopped or activated, so they need qualified human review before these languages
can be considered reviewed for this dialog. The Russian `plugins.approval.submit`
uses a count-agnostic "label: number" form because one singular key cannot cover
Russian's plural forms.

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

Command labels, including footer navigation and the four compact command labels,
now use the shared catalog. The footer mode selector also uses catalog strings and
refreshes its selected-item template on language changes. Catalog coverage and
source-level refresh wiring do not establish native rendering, layout,
accessibility or linguistic acceptance for these surfaces.

- Several other helpers still select EN/DE directly, including remaining playback
  labels, cleanup strings, and some thread-favorite and thread-row text.
  These bypass the shared catalog and remain English for ES/FR/RU even with full
  key coverage.
- Semi/Ursa controls use German for DE, their own locale for zh/ja/ko/it/pl/cs
  where the vendor ships one, and English for the other languages.
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

### CJK and the newer previews

The zh-Hans, ja, ko, pt-BR, cs, it and pl packs are Untested AI-authored previews
like es/fr/ru. For CJK, the Linux smoke checks the font order, shaping without
.notdef glyphs and Light/Dark captures for line breaks; it is no Windows/macOS
rendering, IME input or qualified native review. Traditional Chinese (`zh-Hant`)
is distinct and not offered; do not label all Chinese locales as Simplified.
Semi/Ursa vendor controls use their own zh-CN, ja-JP, ko-KR, it-IT, pl-PL (Semi)
and zh-CN, cs-CZ, pl-PL (Ursa) strings where they ship them, English otherwise.
Full built-in support also requires helper/vendor localization, layout and
accessibility QA and an explicit upgrade/collision policy for existing packs.

See [language-pack format and contracts](../README.md).

The es/fr/ru entries of `MediaMenuCatalog` (keys `media.menu.*`: the reasons the shared
media menu gives for actions it cannot offer, the summary item, the selection note and the
posts line of the details) are **unreviewed machine translations**. They word the same
causes on every surface (archive, media page, gallery); en and de are the built-in
reference.

### Japanese glossary (`ja.json`)

`ja.json` (`ja` · 日本語 (Untested)) is an **unreviewed AI-authored translation** with
no native or layout review. Sentences and messages use polite です/ます form; labels,
buttons and menus use short noun style. Japanese has no plural variants, so the pack
carries no `.one`/`.few` keys. Fixed terms:

| English | Japanese | Note |
| --- | --- | --- |
| post | 投稿 | Imageboard and Danbooru alike; レス is not used |
| thread / board | スレッド / 板 | |
| watch, watcher | 監視 | ウォッチ is not used |
| archive / media / gallery / reader | アーカイブ / メディア / ギャラリー / リーダー | |
| download / import / export | ダウンロード / インポート / エクスポート | Pasted URLs: 取り込み |
| tag / own tag / source tag | タグ / 独自タグ / ソースタグ | |
| plugin / settings / profile | プラグイン / 設定 / プロファイル | |
| favorite / rating / bookmark | お気に入り / 評価 / ブックマーク | Danbooru rating names stay English |
| spoiler | ネタバレ | |
| Storage and limits | 保存容量と上限 | "Raise X under Y" → `Y の「X」を引き上げてください` |
| free-space reserve | 空き容量の予約 | |
| batch (import) | バッチ | |

Booru, Danbooru, MeowChan, keyboard keys and units stay unchanged. Japanese sentences
use full-width 、。「」？！ and （）; label colons stay half-width (`ラベル: {0}`).

### Korean (`ko`) glossary

`ko.json` (`한국어 (Untested)`) is an **unreviewed AI-authored preview** like es/fr/ru;
no native Korean review, CJK font, wrapping or IME QA is claimed by the pack itself.
Korean has no plural forms, so the pack carries no `.one`/`.few` variants. Messages use
the polite 합니다/습니다 style (instructions as ~하세요); labels, buttons and menus stay
short (저장, 설정, 삭제). Fixed terms:

| English | Korean |
| --- | --- |
| post | 게시물 |
| thread | 스레드 |
| board | 게시판 |
| archive (local archive) | 보관함 (archived: 보관됨) |
| media | 미디어 |
| gallery | 갤러리 |
| reader | 리더 (a media viewer is 뷰어) |
| watch / watcher | 감시 / 감시자 |
| download | 다운로드 |
| tag / own tags | 태그 / 내 태그 |
| plugin | 플러그인 |
| source / provider | 소스 / 제공자 |
| settings | 설정 |
| favorite | 즐겨찾기 |
| rating (own) / provider rating | 평가 / 등급 |
| spoiler | 스포일러 |
| Storage and limits | 저장 공간 및 한도 |
| capture | 캡처 |
| Booru, Danbooru | unchanged |

Particles after a placeholder or other variable text use the neutral software notation
`을(를)`, `이(가)`, `은(는)`, `(으)로`; particles that do not change (`의`, `에`, `에서`) are
written plainly. Keyboard keys, units and placeholders are kept unchanged.

## Glossary: Brazilian Portuguese (pt-BR) and Czech (cs)

`pt-BR.json` (Português (Brasil) (Untested)) and `cs.json` (Čeština (Untested)) are
**unreviewed AI translations** like the other previews. pt-BR addresses the user with
informal "você"; cs uses the polite "vy" form and „…“ quotes. Booru, Danbooru, board,
plugin, worker and URL stay as they are. One term per concept:

| English | pt-BR | cs |
| --- | --- | --- |
| post | postagem | příspěvek |
| thread | tópico | vlákno |
| archive (page / state) | Acervo / arquivado | Archiv / archivováno |
| file | arquivo | soubor |
| media | mídia | média, médium |
| gallery | galeria | galerie |
| reader | leitor | čtečka |
| watch (thread/board) | acompanhar, acompanhamento | sledovat, sledování |
| watcher (Danbooru search) | monitor | sledování |
| download | download (noun), baixar (verb) | stahování, stáhnout |
| tag / own tag | tag / tag própria | štítek / vlastní štítek |
| plugin | plugin | plugin |
| settings | Configurações | Nastavení |
| favorite | favorito | oblíbené |
| rating | avaliação (Danbooru: classificação) | hodnocení |
| Storage and limits | Armazenamento e limites | Úložiště a limity |
| provider | provedor | poskytovatel |

Plural variants: cs adds `<key>.few` (2–4; base = other) where a number directly governs a
noun and `<key>.one`/`<key>.few` for the `limit.*` texts; other counted cs texts use the
count-agnostic "Label: {0}" form. pt-BR adds `<key>.one` (0 and 1) for the `limit.*` texts.
Size units in rules and budget texts are MiB/KiB, as in English. Both packs need qualified
native review before they can count as reviewed.
