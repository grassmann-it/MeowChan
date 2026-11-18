# Paket 9 — Verification Audit

**Stand:** 18.11.2026
**Worktree:** `/home/tomka/.worktrees/ochan-ledger/worker-9`
**Audit-Branch:** `codex/package-9-verification-audit-20261118`
**Zielbranch:** `origin/goal/gesamtprogramm-profile-a`
**Ziel-Head beim Preflight:** `866ad8ea24a09d57e9e8e5e3452d4de6beaa100a`
**Scope:** CX-01 bis CX-07, ausschließlich Verification/Audit; kein Merge- oder Release-Claim.

## Ergebnis / Gate

**Audit-Artefakt und Scope-Guard sind reproduzierbar grün. Produktabnahme bleibt offen.**

Der Audit-Branch wurde in einem eigenen Worktree direkt von `origin/goal/gesamtprogramm-profile-a`
angelegt. Die Prüfung inventarisiert die vorhandenen Verträge und Testquellen für die sieben
CX-Pakete, vergleicht die bekannten Package-9-PRs mit dem aktuellen Ziel-Head und trennt
managed/contract evidence von nativer Desktop-/Plattform-Evidenz.

**Keine native Abnahme behauptet.** Testnamen, Testquellen oder ein grüner Audit-Guard sind
kein Beleg für einen erfolgreichen DesktopSmoke-, Apphost-, Plattform- oder Release-Lauf.

## Paketdefinition und Auditgrenze

Eine kanonische Datei mit der Paketdefinition `UX103-CODEX-ARBEITSPAKETE-2026-11-11.md`
ist im Ziel-Head nicht vorhanden. Die Bezeichnungen CX-01 bis CX-07 werden deshalb nur als
Audit-Scope aus den beiden bestehenden Package-9-PRs (#72 und #74) sowie deren bereits
versionierten Audit-Matrizen übernommen. Das ist eine **Dokumentationsgrenze**, keine
nachträgliche Rekonstruktion einer fehlenden Spezifikation.

Die vorhandenen Quellen werden nur auf Existenz und Zuordenbarkeit geprüft. Aussagen zu
Abdeckung sind konservativ: Ein vorhandener Vertrags- oder Smoke-Test beweist nicht den
vollständigen nativen Journey, kein bestimmtes DPI-/Theme-/OS-Verhalten und keine CI-
Reproduzierbarkeit.

## Zuordnung gegen den aktuellen Ziel-Head

| Paket | Vorhandene Quellen im Ziel-Head | Was daraus belastbar folgt | Offene Verifikation |
| --- | --- | --- | --- |
| CX-01 | `ApphostStartupSmokeTests.cs`; `StartupFirstFrameChecks.cs`; `TrayLifecycleChecks.cs` | Argument-/Startup-/First-frame- und Tray-Verträge sind als Quellen vorhanden. | Keine ausgeführte Drei-Profil-Messreihe mit Receipt bis First-Rendered-Frame/Ready; kein nativer OS-Tray-Nachweis. |
| CX-02 | `ApphostSourceActivationStartupTests.cs`; `StartupPreviewChecks.cs`; `InterruptedStartupChecks.cs`; `Cx02GalleryExportClosureChecks.cs` | Frühwahl/Preview, Unterbrechungszustände und Export-Closure besitzen gezielte Verträge. | Keine vollständige Starting/Ready-/Recovery-Matrix auf frischen Profilen und kein nativer Bediennachweis während Startup. |
| CX-03 | `DownloadSearchStartupTruthChecks.cs`; `SourceFilterWaveChecks.cs`; `DownloadSearchReconciliationTests.cs` | Filter-/Download-/Search-Truth und Reconciliation sind als managed/Smoke-Quellen vertreten. | Kein Receipt für die End-to-end-Korrelation von Filterstatus, Runtime-/Writer-Gate und Ergebniswahrheit auf einer nativen Sitzung. |
| CX-04 | `ReaderViewChecks.cs`; `ReaderUxChecks.cs`; `ReaderToolbarChecks.cs`; `ReaderNavigationLayoutTests.cs` | Reader-Struktur, Toolbar und UX-Verträge sind statisch bzw. managed abbildbar. | Keine vollständige native Dark/Light-, DPI-, Fokus- und plattformübergreifende Abnahme. |
| CX-05 | `MediaPageChecks.BooruCaptures.cs`; `MediaFilterSurfaceChecks.cs`; `MediaInteractionChecks.cs`; `MediaPageSelectionLayoutTests.cs` | Medienfläche, Filter, Interaktion und Auswahl-Layout haben gezielte Quellen. | Dichte/Overflow/Responsive über Größen/DPI und reale Navigation nicht als vollständige native Journey belegt. |
| CX-06 | `Cx06DirectGalleryOpenChecks.cs`; `MediaGalleryReturnChecks.cs`; `GalleryCloseChecks.cs`; `ApphostMediaSmokeTests.cs` | Direkter Galerie-Einstieg sowie Return-/Close-Verträge sind vorhanden. | Kein aktuelles Receipt für den vollständigen nativen Media→Thread→Media-Lauf mit Fokus-/Return-Zustand. |
| CX-07 | `LocalMediaTagChecks.cs`; `GalleryMediaTagChecks.cs`; `PrivateTagBarChecks.cs`; `GalleryMediaTagTests.cs` | Medien-/Private-Tag- und UI-Aktionsverträge sind vorhanden. | Lose Medien, Restart/Persistenz und integrierter Multi-Surface-Nativlauf fehlen als aktuelle, gebundene Evidenz. |

## Relevante Änderungen und PRs

Die Zielbasis enthält die bereits integrierten UX-/Media-/Reader-/Tagging-Änderungen. Für
Package 9 wurde keine Produktänderung neu bewertet oder umgeschrieben; geprüft wurden
Zuordnung, Testbarkeit und Evidenzstatus gegen den aktuellen Ziel-Head.

### Package-9-spezifische PRs

| PR | Zustand am Audit | Befund |
| --- | --- | --- |
| #72 `codex/package-9-test-verification-audit-20261117` | offen | Duplicate Audit-PR, basiert auf altem Base-Head `4077eb868`; auf dem Package-9-Commit sind keine Checks gemeldet. |
| #74 `codex/pkg9-verification-20261117` | offen | Inhaltlich zweiter Duplicate Audit-PR, ebenfalls Base-Head `4077eb868`; auf dem Package-9-Commit sind keine Checks gemeldet. |
| aktueller Audit-PR | neu aus `866ad8e…` | Dieser PR enthält nur Audit-Report und standard-library-fähigen Scope-Guard; kein Merge. |

### Weitere offene, für die Matrix relevante PRs

- #68 Review-Fixture/Profile-Matrix: hilfreiches Test-/Capture-Tooling, aber kein nativer
  Plattformbeleg; am Auditzeitpunkt ohne Checks.
- #69 Media-Kontext-/Click-Contracts: CX-05/CX-06-nahe Änderungsfläche; nicht in den
  aktuellen Ziel-Head gemergt; am Auditzeitpunkt ohne Checks.
- #75 Reader-/Gallery-Shortcuts: CX-04/CX-06-nahe Änderungsfläche; nicht in den Ziel-Head
  gemergt; am Auditzeitpunkt ohne Checks.
- #76 Tag-Queue-Scope und #77 Localization-Audit: angrenzende offene PRs, aber kein
  Ersatz für die fehlenden CX-Native-/CI-Receipts.

Die bereits gemergten PRs #14–#67 wurden als Zielbranch-Historie berücksichtigt; einzelne
PRs können keine fehlende End-to-end- oder Plattform-Evidenz ersetzen. Die Auditmatrix
führt bewusst Quellen statt einer unprüfbaren Behauptung „alles grün“.

## Reproduzierbarer lokaler Preflight

Ausgeführt im angegebenen Worktree:

```text
git status --short --branch
# codex/package-9-verification-audit-20261118...origin/goal/gesamtprogramm-profile-a

git rev-parse HEAD
# 866ad8ea24a09d57e9e8e5e3452d4de6beaa100a

git diff --check
# PASS

python tests/package9_audit/test_package9_inventory.py
# PASS für alle vier Audit-Guards
```

Der Guard verwendet ausschließlich die Python-Standardbibliothek. Dadurch bleibt der
Audit-Teil auch in einer Minimalumgebung ausführbar:

```text
python -m pytest tests/package9_audit/test_package9_inventory.py -q
# lokal nicht ausführbar: pytest ist nicht installiert
```

Zusätzlich wurde die Testumgebung für den managed Preflight restauriert:

```text
dotnet restore tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj
# Exit 0; nur ignorierte obj/bin-Artefakte erzeugt, keine Lockfile-/Quelländerung
```

Ein anschließender gezielter `dotnet test`-Build wurde mit 600 Sekunden Zeitlimit
angestoßen, lief auf dieser Manjaro-Workstation unter konkurrierender lokaler .NET-
Auslastung in den Timeout (`exit 124`), bevor die DesktopTests-DLL erzeugt war. Der erste
`--no-restore`-Versuch war vor Restore erwartungsgemäß nicht lauffähig (`NETSDK1004`,
fehlendes `project.assets.json`); ein direkter `dotnet vstest`-Versuch bestätigte danach,
dass die DLL nicht vorhanden war. Es wird deshalb kein grüner managed Testlauf behauptet.

## Bewusste lokale Grenzen

- **Kein DesktopSmoke-Lauf:** Der Harness wurde nur gelesen/inventarisiert. Keine Datei unter
  `tests/MeowChan.DesktopSmoke/` wurde geändert.
- **Kein vollständiger Apphost-/GUI-Lauf:** Xvfb/OS-Eingabe/Tray/Focus-/DPI- und reale
  Profiljourneys sind nicht durch den Audit-Guard abgedeckt.
- **Nur Manjaro Linux x64:** Windows-, macOS- und Hardwareläufe sind lokal nicht verfügbar.
- **Keine Release-/Paketabnahme:** Keine Distributionspaketierung, kein Installationslauf und
  kein Single-file-/Video-native-Plattformclaim.
- **Keine Baseline-/Lockfile-/Build-Infra-Arbeit:** Es wurde weder `.github/workflows/`, noch
  DesktopSmoke, Baselines, Lockfiles oder Build-Infrastruktur geändert.

## CI-Nachweis / fehlende Receipts

Zum Zeitpunkt dieses Audits meldeten die Package-9-Commits `0ed025e…` (PR #72) und
`ba67d83…` (PR #74) **keine Check-Runs**. Der aktuelle Ziel-Head `866ad8e…` hatte bei der Abfrage 26 `cancelled`, 31 `skipped`,
1 `success`, 20 `in_progress` und 22 `queued` Check-Runs; mehrere Plattform-/Smoke-/
Package-Jobs waren `skipped`. Das ist kein abgeschlossenes grünes Package-9-Receipt und
wird nicht als solches ausgegeben.

Fehlende oder nicht gebundene Nachweise:

1. PR-gebundener grüner CI-Lauf des aktuellen Audit-Commits.
2. Testcommand, Exitcode, Fixture-/Profil-ID, OS/Runner und Artefakt-URI je CX-Paket.
3. Native Windows/macOS-/Hardware-Journeys für Startup, Reader, Media, Gallery und Tags.
4. CI-Receipts für die offenen relevanten PRs #68, #69, #75–#77.
5. Ein kanonischer Package-9-Spezifikationsbeleg im Zielbranch.

## Änderungsgrenze dieses PRs

Erlaubt sind nur:

- `review/package9/test-verification-audit-2026-11-18.md`
- `tests/package9_audit/test_package9_inventory.py`

Nicht geändert werden: `tests/MeowChan.DesktopSmoke/`, `.github/workflows/`, Baselines,
Lockfiles, Build-Infra und Produktcode. Kein Merge, kein Release- oder Plattformclaim.

## Final canonical-set retest — 18 November 2026

After all 20 worker lanes completed, the canonical current package PR set was rechecked
against `origin/goal/gesamtprogramm-profile-a` at `866ad8ea2`:

```text
Package 1: #90 #91 #97
Package 2: #93 #86
Package 3: #76 #81
Package 4: #87 #95 #88
Package 5: #89 #92
Package 6: #94 #84 #80 #96
Package 7: #78 #82 #85
Package 8: #79
Package 9: #83
```

The local central preflight found all 20 worker worktrees clean, all worker diffs free of
the forbidden DesktopSmoke/workflow/baseline/lockfile/build-infrastructure paths, and all
current PRs reported GitHub merge state `CLEAN`. A targeted CI retest was then attempted
with `gh pr checks` for PRs #86–#97 and #79. Every query returned **`no checks reported`**;
there is therefore no green CI receipt to claim. Duplicate historical Package-6A and
Package-9 PRs (#71/#73 and #72/#74) remain open and must be canonicalized separately.

**Final verification outcome:** audit artifacts and local package evidence are complete;
native/platform acceptance and PR-bound CI acceptance remain open. This is a gate record,
not a merge or release approval.
