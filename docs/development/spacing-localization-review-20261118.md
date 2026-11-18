# Spacing/localization review (Paket 6C)

Date: 2026-11-18
Scope: localization strings whose leading/trailing whitespace affects UI composition; spacing-only UI review
Base: `origin/goal/gesamtprogramm-profile-a` at `866ad8ea2`

## Confirmed defect and fix

`TaggingModeText.Start` appends the optional group sentence after a literal
separator:

```csharp
text += " " + Texts.T("tagging.status.group", walk.GroupName);
```

The French bundled pack had `tagging.status.group` as `Groupe « {0} ». ` with
one trailing space. A French tagging start status therefore ended with an
invisible extra whitespace character whenever a group name was present. The
other bundled packs and the English/German catalog use a complete sentence
without that suffix. The pack entry now ends at the period.

The regression test `InlineTaggingGroupStatusDoesNotCarryTrailingWhitespace`
loads every bundled language through the production `Texts` loader and guards
this inline-append contract.

## Audit disposition

I compared leading/trailing whitespace across the ten bundled JSON packs and
followed each edge-whitespace key that is intentionally composed with adjacent
text. The following are deliberate fragments and were not changed:

- gallery quality fragments (`gallery.viewer6`, `gallery.viewer11`,
  `gallery.viewer12`),
- error/detail labels and archive deletion labels that are concatenated with a
  value,
- the reader-group unavailable prefix,
- shortcut issue help text, and
- archive cleanup status text that is followed by another localized fragment.

`tagging.status.group` was the only complete sentence with an inconsistent
trailing-space contract and a confirmed inline append site. No spacing-grid
value, style margin, baseline, lockfile, restore/build infrastructure, Desktop
Smoke file, or workflow was changed.

## Validation

- `python3` JSON parse plus edge-whitespace comparison across all bundled packs:
  passed.
- `git diff --check`: passed.
- Focused .NET test command:

  ```text
  dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
    --filter FullyQualifiedName~LanguagePackAuditRegressionTests.InlineTaggingGroupStatusDoesNotCarryTrailingWhitespace \
    --no-restore
  ```

  The command was started without restoring packages. It could not complete in
  this checkout because the worktree initially had no generated assets files;
  after reusing existing local `project.assets.json` files, concurrent builds
  occupied the shared compiler and no completed test result was available. No
  restore was run.
