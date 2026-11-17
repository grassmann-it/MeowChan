# Paket 9 — Test-/Verifikationsaudit

**Stand:** 17.11.2026
**Scope:** sieben UX103-/1.0.3-Arbeitspakete CX-01 bis CX-07
**Basis:** aktueller Checkout `dogfood-release` (`4077eb868`)
**Disposition:** Audit/PR-Vorbereitung בלבד; kein Merge, kein Release- oder Plattformclaim.

## Methode und Grenzen

Die Auditmatrix hält die sieben beauftragten CX-Pakete und ihre vorhandenen Tests direkt fest; die historische Paket-Spec ist in diesem Zielbranch nicht enthalten. Inventarisiert wurden vorhandene source-/managed Tests nach Dateinamen und Testklassen. Das Audit trennt Vertragstests, reproduzierbare Abläufe und native Evidenz.

`tests/MeowChan.DesktopSmoke` wurde nur gelesen. Es wurde keine Smoke-Datei, CI-Datei, Baseline oder Lockfile geändert. Ein grüner Lauf dieses Audits ist nur eine Konsistenzprüfung, keine Produktabnahme. **Keine native Abnahme behauptet.**

## Zuordnung

| Paket | Vorhandene Tests/Evidenz | Akzeptanzabgleich | Lücke / Status |
| --- | --- | --- | --- |
| CX-01 | `tests/MeowChan.DesktopTests/ApphostStartupSmokeTests.cs`, `tests/MeowChan.DesktopSmoke/StartupFirstFrameChecks.cs`, `tests/MeowChan.DesktopSmoke/TrayLifecycleChecks.cs` | Prozess-/Apphost- und First-Frame-Verträge sowie Tray-Lifecycle sind teilweise abbildbar. | Drei frische Einzelprozesse mit getrennten Profilen und Messreihe bis First-Rendered-Frame/Ready fehlen als ausgeführte Evidenz. **Offen.** |
| CX-02 | `tests/MeowChan.DesktopTests/ApphostStartupSmokeTests.cs`, `tests/MeowChan.DesktopSmoke/StartupPageNavigationChecks.cs`, `tests/MeowChan.DesktopSmoke/InterruptedStartupChecks.cs` | Frühwahl einzelner Seiten und Startup-Unterbrechungen sind vertreten. | Vollständige Starting/Ready-Matrix und native Bedienbarkeit während Startup sind nicht bewiesen. **Teilweise, offen.** |
| CX-03 | `tests/MeowChan.DesktopSmoke/DownloadSearchStartupTruthChecks.cs`, `tests/MeowChan.DesktopSmoke/SourceFilterWaveChecks.cs`, `tests/MeowChan.DesktopTests/*Startup*Tests.cs` | Filter-/Truth-Verträge und einige Startup-Sichten existieren. | End-to-end-Korrelation von Filterstatus, Runtime-/Writer-Gate und Ergebniswahrheit fehlt. **Teilweise, offen.** |
| CX-04 | `tests/MeowChan.DesktopSmoke/ReaderViewChecks.cs`, `tests/MeowChan.DesktopSmoke/ReaderUxChecks.cs`, `tests/MeowChan.DesktopSmoke/ReaderToolbarChecks.cs`, `tests/MeowChan.DesktopTests/Reader*Tests.cs` | Reader-Struktur, Toolbar und UX-Verträge sind vertreten. | Native Dark/Light-, DPI-, Fokus- und plattformübergreifende Abnahme fehlt. **Teilweise, offen.** |
| CX-05 | `tests/MeowChan.DesktopSmoke/MediaPageChecks.BooruCaptures.cs`, `tests/MeowChan.DesktopSmoke/MediaFilterSurfaceChecks.cs`, `tests/MeowChan.DesktopSmoke/MediaInteractionChecks.cs`, `tests/MeowChan.DesktopTests/Media*Tests.cs` | Ergebnisfläche, Filter und Interaktion haben Vertragsabdeckung. | Dichte/Overflow/Responsive über Größen und DPI sowie reale Navigation sind nicht vollständig belegt. **Teilweise, offen.** |
| CX-06 | `tests/MeowChan.DesktopSmoke/Cx06DirectGalleryOpenChecks.cs`, `tests/MeowChan.DesktopSmoke/MediaGalleryReturnChecks.cs`, `tests/MeowChan.DesktopSmoke/GalleryCloseChecks.cs`, `tests/MeowChan.DesktopTests/ApphostMediaSmokeTests.cs` | Direkter Gallery-Einstieg und Rückweg sind gezielte Verträge. | Vollständiger nativer Media→Thread→Media-Lauf mit Fokus-/Return-Zustand fehlt. **Teilweise, offen.** |
| CX-07 | `tests/MeowChan.DesktopSmoke/LocalMediaTagChecks.cs`, `tests/MeowChan.DesktopSmoke/GalleryMediaTagChecks.cs`, `tests/MeowChan.DesktopSmoke/PrivateTagBarChecks.cs`, `tests/MeowChan.DesktopTests/MediaTag*Tests.cs`, `tests/MeowChan.DesktopTests/Tag*Tests.cs` | Tagging-/Medienaktionsverträge und private/source Tag-Trennung sind vertreten. | Lose Medien, Persistenz/Restart und integrierter native Multi-surface-Ablauf fehlen. **Teilweise, offen.** |

## Priorisierte Lücken

1. **P0:** CX-01/CX-02 benötigen native Mess-/Bedienreihen mit getrennten Profilen; ohne diese keine First-Frame- oder Startup-Gate-Abnahme.
2. **P1:** CX-04 bis CX-07 benötigen native Journey-Läufe für DPI, Fokus, Return und Persistenz.
3. **P2:** Jedes Paket braucht ein Receipt mit Testcommand, Exitcode, Fixture-/Profil-ID und Plattform. Testnamen allein sind kein Receipt.

## Ausgeführte Prüfung

```text
python -m pytest tests/package9_audit/test_package9_inventory.py -q
```

Der Test prüft die sieben Spezifikationsabschnitte, die Berichtsmatrix und den Ausschluss von Smoke/CI/Baselines/Lockfiles. Er bestätigt keine Produktfunktion.

## Nicht ausgeführt

Kein Restore, Build, DesktopSmoke-Lauf, native Apphost-Abnahme oder CI-Lauf. Daher werden keine grünen Produkt-/Plattformtests behauptet. Der PR bleibt mit Merge-Sperre offen, bis die Evidenzlücken separat geschlossen und mit echten Receipts belegt sind.
