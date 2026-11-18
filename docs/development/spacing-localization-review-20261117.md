# Spacing review (Paket 6C)

Date: 2026-11-17  
Scope: Auftrag 2, Spacing/Localization only  
Base: `origin/goal/gesamtprogramm-profile-a` at `4077eb868`

## Result

The supplied Auftrag describes 33 spacing findings, but the corresponding
line-level inventory is not present in this checkout. Each available spacing
signal was checked against the authoritative design-lint scanner and the
surrounding layout contract; no speculative numeric replacement was made.

- `DesignLintScanner` defines the spacing grid as `0/2/4/8/12/16/24/32` and
  scans XAML `Margin`, `Padding`, `Spacing`, `ItemSpacing`, `LineSpacing`,
  `RowSpacing`, `ColumnSpacing` plus C# thickness/spacing expressions.
- The checked-in design-lint baseline has zero `spacing-off-grid` debt, and the
  scanner has no current un-baselined spacing violation.
- Negative margins are not treated as defects by pattern alone. The observed
  values are intentional overlap/overlay contracts:
  - `Workspace.FocusAdorner`: `Margin="-4"` keeps the focus ring outside the
    control bounds without changing layout.
  - `ToggleButton.segment-last` and `Button.split-menu`: `-1,0,0,0` joins
    adjacent segmented controls by overlapping their shared border.
- No source change is justified without the missing per-finding context
  (layout intent, neighboring spacing, and expected visual contract).

## Classification ledger

| Classification | Count | Disposition |
|---|---:|---|
| Confirmed spacing defect | 0 | No product change |
| Intentional focus-overlay margin | 1 observed | Keep `Margin="-4"`; covered by focus-style tests |
| Intentional segmented-control overlap | 2 observed | Keep `-1,0,0,0`; covered by segment/split style tests |
| Other reported findings | 30 expected, inventory unavailable | No speculative edits; request source inventory |
| **Reported total** | **33** | **Reviewed conservatively** |

The count of 30 is the remainder after the three concrete negative-margin
cases visible in this checkout. It is not a claim that the absent external
inventory has been independently reconstructed.

## Validation

Run the focused scanner contract with:

```text
 dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --filter FullyQualifiedName~DesignLint
```

No merge was performed.
