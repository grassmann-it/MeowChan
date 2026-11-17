# Spacing review (Paket 6C)

Date: 2026-11-17  
Scope: Auftrag 2, Spacing/Localization only  
Base: `origin/goal/gesamtprogramm-profile-a` at `4077eb868`

## Result

The supplied Auftrag describes 33 spacing findings, but the corresponding
line-level inventory is not present in this checkout. I therefore reviewed the
repository's authoritative design-lint implementation and the current source
rather than replacing numeric values by pattern.

- `DesignLintScanner` defines the spacing grid as `0/2/4/8/12/16/24/32` and
  scans XAML `Margin`, `Padding`, `Spacing`, `ItemSpacing`, `LineSpacing`,
  `RowSpacing`, `ColumnSpacing` plus C# thickness/spacing expressions.
- The checked-in current report contains **0 `spacing-off-grid` violations**.
- `Desktop/WorkspaceStyles.axaml:739` contains `Margin="-4"`. This is a
  deliberate overlay/badge alignment value in the style system, not ordinary
  inter-control spacing. It was not blindly replaced.
- No source change is justified without the missing per-finding context
  (layout intent, neighboring spacing, and the expected visual contract).

## Classification ledger

| Classification | Count | Disposition |
|---|---:|---|
| Confirmed spacing defect | 0 | No product change |
| Intentional negative/overlay margin | 1 observed | Keep `Margin="-4"`; document as intentional |
| Other reported findings | 32 expected, inventory unavailable | No speculative edits; request source inventory |
| **Reported total** | **33** | **Reviewed conservatively** |

The count of 32 is the remainder after the one concrete negative-margin case
that is visible in this checkout. It is not a claim that the absent external
inventory has been independently reconstructed.

## Boundaries

Only spacing and the localization-audit boundary were considered. No shortcut,
contrast, CI, smoke, lockfile, or restore changes were made. No merge was
performed.
