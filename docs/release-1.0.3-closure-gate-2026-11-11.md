# MeowChan 1.0.3 — Abschluss-Gate (11.11.2026)

Branch `goal/gesamtprogramm-profile-a`, Gate-Head `e70c69c`. Nichts veröffentlicht,
gepusht, getaggt oder hochgeladen. Evidence ausschließlich unter `/tmp/mc-evidence`.

## Ergebnis

**Kein freigebbarer 1.0.3-Kandidat. Das Goal endet mit zwei dokumentierten echten Blockern.**

### Blocker 1 — Recovery unterbrochener nativer Transfers (lokal lösbar, Freigabe offen)

Releaseplan §2.1 verlangt für Cancel/Crash/Platzmangel einen ehrlichen Recoveryzustand
und schließt einen Downgrade aus. Real gemessen:

| Fall | Beleg | Ergebnis |
| --- | --- | --- |
| ENOSPC 0/4 KiB frei | `ef116e2`, tmpfs 64 MiB, 3×4/4 (`diskfull-2026-11-11`) | Fehler vor Claim; Plan committet nach Freigabe |
| ENOSPC 64 KiB frei | dito | `ReconciliationRequired`, **Profilstart dauerhaft blockiert** |
| SIGKILL vor `SqlCommitted` (7 Checkpoints) | `56e9a3c`, 2×8/8 (`crash-2026-11-11`) | **Profilstart dauerhaft blockiert** |
| SIGKILL nach `SqlCommitted` | dito | Start frei, Bytes veröffentlicht |

Nichts wird teilweise veröffentlicht, der Katalog öffnet immer, aber es gibt keinen
Produktweg aus dem blockierten Zustand. Lösungsentwurf (bestätigtes Verwerfen, kein
Schemawechsel): `docs/architecture/gesamtprogramm-2026-10-31/NATIVE-TRANSFER-INTERRUPTED-RESOLUTION.md`.
Der Nutzer hat „erst Entwurf, Umsetzung nach Freigabe“ entschieden; die Freigabe steht aus.
Merge-Operationen sind nicht real getestet.

### Blocker 2 — Plattformbelege (unter Linux nicht ersetzbar)

Keine Windows-, macOS- oder Hardwarebelege; die Linux-Paketprüfung
(`check-desktop-package.py`) wurde nicht ausgeführt. Nach `START-GOAL-1.0.3.md` blockiert
das den Kandidaten unabhängig von Blocker 1.

## Belegt auf einem Head

Gate-Lauf auf `4bc4d01` (`/tmp/mc-evidence/final3-latest`, frischer
`--no-restore --no-incremental`-Build, `dotnet test --no-build`):

| Suite | Ergebnis |
| --- | --- |
| Core `MeowChan.Tests` | 9399 bestanden, 0 fehlgeschlagen, 5 übersprungen |
| Desktop `MeowChan.DesktopTests` | 3079 / 0 / 11 |
| Beispiel-/Experimental-Plugins | 360 / 0 / 0 |
| Build `MeowChan.DesktopSmoke` | 0 Fehler |

Nach `4bc4d01` nur: `9948b8f` (Startup-Probes in zwei Spuren; Desktop-Filter `Plugin`
155/0/1), `ef116e2` und `56e9a3c` (opt-in, ohne Variable übersprungen), Dokumente.

## Belegt durch einzelne Slices (Commits im Branch)

- Archivfilter-Vorschläge: `5193d38` (Escape → Pfeil öffnet erneut, Tastaturregression in
  `ArchiveExactIdentityChecks`), `d28283d` (Klick übernimmt, Auswahlwechsel); stale/late
  Antworten in `IdentitySuggestionTests` (`SupersededInputAndProfileEndCannotPublishLateSnapshot`,
  `LateScopedSourceResponseCannotReplaceNewerExactSource`,
  `CandidateScopeAndFreeTextRemainDistinctIncludingStaleSelection`); Board-Kandidat→Add
  (`ArchiveAddBoard`/`ArchiveAddSource`) im Smoke; Archivfilter-Smoke 12/12 Szenarien.
- „assembly not found“ beim Gerätezeitzonen-Button: nicht reproduzierbar; benannte
  Diagnose und Deployment-Regression `b14f4b1`.
- Transfer/Merge/Recovery/Reexport/Private-Apply: im Core-Volllauf (Filter `Transfer`
  zuletzt 6242 grün); Konfliktledger je Slot `53e0c6c`; bestätigter Reclaim verworfener
  Alternativen `08efb53`.
- UX103: SF-03/SF-05 `db28407`; Startkosten `15e469d` (Wiederöffnen Pluginphase
  6,65 → 1,28 s) und `9948b8f` (Erststart ~halbiert).
- Weitere Belege und Grenzen: `docs/release-1.0.3-local-status-2026-11-11.md`.

## Bereichsläufe mit eigenem Log (11.11., `/tmp/mc-evidence/scope-areas-1794361495`)

Gezielte `dotnet test --no-build`-Filter auf Head `3c516f8`, Bins aus den frischen Builds oben:

| Bereich | Ergebnis |
| --- | --- |
| v4-/v2-Apply, nativer Medienimport | 359 bestanden, 0 fehlgeschlagen, 4 übersprungen (opt-in Platzmangel/Absturz) |
| Private Consent / Private Apply / private Metadaten | 1134 / 0 / 1 |
| 404-Merge, Medienmerge | 123 / 0 / 0 |
| Konfliktalternativen (Ledger, Domain, Inventar) | 26 / 0 / 0 |
| Reexport, Offline, passive Medien | 149 / 0 / 1 |
| Bestandsschutz (Favoriten, Bookmarks, Bewertungen, Notizen) | 884 / 0 / 0 |
| Sieben experimentelle Quellen + Metadaten | 234 / 0 / 0 |
| Desktop Importdialog / Konfliktdialog | 31 / 0 / 0 |
| Desktop FirstRun / Settings | 277 / 0 / 0 |
| Desktop Galerie / Video / Playback / Mute | 585 / 0 / 1 |
| Desktop HTML-/Export | 176 / 0 / 3 |
| Desktop Lokalisierung ES/FR/RU | 615 / 0 / 0 |
| Desktop Kontextmenü / Clipboard | 154 / 0 / 0 |
| Desktop Archivfilter / Vorschläge | 32 / 0 / 0 |
| Desktop passive GP-39 | 14 / 0 / 0 |
| Registry-Startup / Profiladmission / W26-W27-Admission / Startup-Recoveryguard | 202 / 0 / 0 |
| Additiver 404-Merge mit neueren Downloads (`NativeV2MergeRoundtripTests`, 404-Roundtrip) | 5 / 0 / 0 |
| Desktop Importzustände Reading/Ready/Applying/Completed/Failed/Canceled/RecoveryRequired (`NativeTransferImportHostTests`, `…LifetimeTests`) | 22 / 0 / 0 |

GUI-Smokes im privaten Xvfb (nice 19):
- `MEOWCHAN_NATIVE_TRANSFER_PREVIEW_ONLY`: Importdialog-Lebenszyklus PASS.
- `MEOWCHAN_SUGGESTIONS_ONLY`: Vorschlagsdarstellung PASS, 4 echte Popup-Aufnahmen.
- `MEOWCHAN_ARCHIVE_COMPARISON_EXACT_IDS_ONLY`: alle Szenarien `pass`, u. a.
  `keyboard-multi-navigation-then-enter`, `escape-then-navigation-reopens-list`,
  `board-candidate-add-then-explicit-search`, `stale-async-suggestions-never-resurface`,
  `explicit-scope-change-revokes-previous-board-qualification`,
  `new-free-input-trims-without-normalizing-existing-identities`. Eingabe:
  in-process materialisierte Controls und Popup-Tastaturereignisse, nicht OS-Eingabe.

## Was das Gate auflöst

1. Freigabe des Recovery-Entwurfs → Umsetzung für Import und Merge, tmpfs-/SIGKILL-Läufe
   müssen danach „Verwerfen → Start frei → Neuimport committet“ zeigen, dann neuer
   Gate-Lauf auf einem Head.
2. Windows-/macOS-/Hardwareläufe und Paketprüfung auf den Zielplattformen.
