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
