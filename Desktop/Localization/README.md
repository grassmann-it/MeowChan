# Desktop language packs

The production desktop includes English (`en`) and German (`de`). English is the
initial language and the fallback for missing translations. Display text is
formatted with the selected language's culture; the download engine's culture is
not changed.

Additional languages are optional, data-only JSON files in the active profile's
`LanguagePacks` directory. Use the same profile as `settings.json` (including when
`OCHAN_PROFILE` selects a custom profile). The desktop reads these files at startup;
it does not create or write language packs. Select an accepted language in Settings.

## Format, schema version 1

```json
{
  "schemaVersion": 1,
  "code": "fr",
  "nativeName": "Français",
  "strings": {
    "settings.title": "Préférences",
    "action.add": "Ajouter",
    "admission.summary": "{0} ajoutées · {1} présentes · {2} refusées"
  }
}
```

The built-in key list and English source strings are in
`Desktop/Localization/BuiltInCatalog.cs`, included in the matching release's
source archive. Use the source archive for your desktop version when preparing a pack.
Metadata names are case-insensitive; translation keys are case-sensitive. Codes
must identify a .NET culture: an initial two- or three-letter ASCII segment,
optionally followed by hyphen-separated ASCII letter/digit segments of two to
eight characters, at most 35 characters total. `nativeName` must contain 1–64
characters, without control characters. The displayed name is trimmed.

Missing keys fall back to English. Unknown translation keys are ignored, allowing
a pack to include text introduced by a newer desktop version. A pack must contain
at least one recognized key. Files are loaded in filename order; duplicate
language codes, including attempts to replace built-in English or German, are
rejected. Duplicate metadata or translation keys are also rejected.

Composite placeholders must retain the source indices and numeric formats. They
may be reordered or repeated. For example, `{0}` cannot become `{1}` or `{0:N2}`;
the source's `{2:0.##}` must remain `{2:0.##}`. Literal braces use `{{` and `}}`.
Strings are plain text: no XAML, HTML or executable expressions are interpreted.

A file may be at most 2 MB, contain at most 2,000 string entries, and use at most
10,000 characters per translation. Empty values, unsupported schemas, malformed
JSON, invalid placeholders and unknown metadata reject that file. Other valid
packs and built-in languages remain usable. Warnings are exposed through
`Texts.Warnings` for the desktop to display. If a previously selected pack is no
longer available, the desktop uses English and reports the fallback.

## Desktop integration

Call `Texts.Initialize(languagePackDirectory)` and then
`Texts.SetLanguage(config.Language)` during profile initialization. SetLanguage
does not write configuration. Only persist a preference after the desktop's
normal settings commit succeeds.

`Texts.T(key, args)` and the language/catalog getters are safe for concurrent
readers. `Changed` fires on the calling thread; UI consumers must marshal updates
to their dispatcher. Dispose subscribers when their windows or projections are
removed. Changing languages must only refresh presentation, preserving the live
session, row identities, input and selection.
