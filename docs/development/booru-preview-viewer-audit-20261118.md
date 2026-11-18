# Package 5B — Booru Preview/Viewer audit

**Datum:** 2026-11-18
**Basis:** `origin/goal/gesamtprogramm-profile-a` (`866ad8ea24a09d57e9e8e5e3452d4de6beaa100a`)
**Arbeitszweig:** `worker-5b`
**Scope:** Booru Preview/Viewer außerhalb von `DesktopSmoke`.

## Boundary

Diese Arbeit ändert, baut und startet keine `DesktopSmoke`-Tests. Die Prüfung bleibt auf Viewer-/Preview-Produktionscode und fokussierte Nicht-Smoke-Tests begrenzt.

## Audit: bestehende korrekte Arbeit

Die folgenden Verträge sind im Produktionscode bereits erfüllt und wurden bei der Prüfung nicht verändert:

- `BooruPreviewDecoder` dekodiert begrenzt und asynchron; Größen-, Pixel- und Abbruchgrenzen werden vor der Anzeige geprüft.
- Der Viewer arbeitet mit einem Snapshot der aktuellen Suchseite, navigiert nur innerhalb dieser Seite und beendet Navigation an den Rändern ohne Wrap-around.
- Navigation und Auswahl zeigen keinen Original-Download an.
- Die Auswahl verwendet den gemeinsamen Booru-Auswahlbestand; Viewer-Toggle und Karten-Checkbox bleiben synchron.
- Fehlende, gesperrte oder ungültige Preview-Daten leeren die Anzeige, statt das vorherige Bild stehen zu lassen.
- `Load original`/`Ctrl+E` bezieht sich auf das aktuell angezeigte Ergebnis; Retry und Ladezustände werden über das Model koordiniert.
- Template-Chrome wird nach dem Anwenden des Templates unterdrückt; der Viewer wird beim Schließen asynchron und idempotent entsorgt.
- Enter/Doppelklick auf eine nicht gespeicherte Trefferkarte öffnet den Preview-Viewer; gespeicherte Treffer behalten den Vorrang von `Show in Media`.

## Reproduzierbarer Defekt und Fix

`BooruResultViewerWindow` erzeugte die synthetische `GalleryEntry` für eine Booru-Preview mit einer fest verdrahteten `ResourceId("danbooru", ...)`. Die SDK-Identität eines Booru-Posts besteht jedoch aus `SourceId` plus Post-ID. Bei einem anderen Provider wurde dadurch die Quelle im synthetischen Preview-Eintrag falsch als `danbooru` gemeldet.

Reproduktion: ein `BooruPostView` mit `SourceId = "fixture-provider"` und Post-ID `42` erzeugte vor dem Fix einen Preview-Eintrag mit `entry.Key.Thread.Source == "danbooru"`.

Fix: Die Entry-Erzeugung ist in `CreatePreviewEntry` zentralisiert und übernimmt `row.Post.SourceId`. Der fokussierte Nicht-Smoke-Test `BooruResultViewerContractTests.SyntheticPreviewEntryRetainsThePostProviderIdentity` schützt diesen Vertrag.

## Verifikation

Ausgeführt wurden ausschließlich fokussierte Nicht-Smoke-Schritte:

- `dotnet restore tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --locked-mode` — erfolgreich.
- `dotnet build tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore -m:1 /p:BuildProjectReferences=false /p:UseSharedCompilation=false` — erfolgreich, `0 Error(s)` (bestehende Analyzer-/Nullability-Warnungen bleiben unverändert).
- Fokussierter Booru-Testlauf (`BooruResultViewerContractTests`, `BooruPreviewDecoderTests`, `BooruHitKeyboardTests`, `BooruWorkbenchViewContractTests`, `BooruPageHostContractTests`, `BooruSearchModelTests`) — **101/101 erfolgreich**.
- `git diff --check` — ohne Befund.

`DesktopSmoke` wurde weder geändert noch ausgeführt.
