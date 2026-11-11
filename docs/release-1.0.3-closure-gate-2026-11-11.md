# MeowChan 1.0.3 — Abschluss-Gate (11.11.2026)

Branch `goal/gesamtprogramm-profile-a`, Gate-Head `aaea381`. Nichts veröffentlicht,
gepusht, getaggt oder hochgeladen. Evidence ausschließlich unter `/tmp/mc-evidence`.

## Ergebnis

**Kein freigebbarer 1.0.3-Kandidat. Ein echter, lokal nicht lösbarer Blocker bleibt: Plattformbelege (Blocker 2).**

Stand `40eba83`/`e257079`: Der verbindliche 1.0.3-Umfang ist lokal integriert und belegt (Abschnitte unten).
Blocker 2 ist innerhalb dieses Goals nicht erfüllbar, nicht bloß vertagt: Windows-, macOS- und Hardwareläufe
brauchen Zielmaschinen, die hier fehlen (nur Manjaro-Linux); die Distributionspaketierung braucht Ubuntu-CI mit
`sudo apt-get`; das Goal verbietet Push, Upload und Remote-CI; ein Docker-Ersatz wurde vom Nutzer am 11.11.
abgelehnt. Auflösung nur außerhalb dieses Goals durch die GitHub-Actions-Läufe des Nutzers.
Blocker 1 (Recovery unterbrochener Transfers) ist seit `aaea381` lokal gelöst und belegt.

### Blocker 1 — Recovery unterbrochener nativer Transfers: **gelöst** (Nutzerfreigabe 11.11.)

Befund vorher: echter Platzmangel (tmpfs) und SIGKILL vor `SqlCommitted` bei Import und Merge
blockierten den Profilstart dauerhaft (`ef116e2`, `56e9a3c`, `c0dfcd7`).

Umsetzung:
- `24aec15` Core: `ReadInterruptedNativeMediaOperationsAsync` listet nur `Preparing`/`Prepared` ohne
  Receipt mit kanonischem Operationsverzeichnis; `DiscardInterruptedNativeMediaOperationsAdmittedAsync`
  (nur Startup-/Exclusive-Admission) parkt das ganze Verzeichnis als `.discard-*`, löscht es und
  entfernt danach nur diese unfertige Zeile. Committed, Receiptfehler, nicht kanonische Pfade und
  Ordinary-Admission werden abgelehnt; kein Schemawechsel.
- `aaea381` Desktop: Beim Start Zweischritt-Dialog über der Shell (zweiter Druck nennt Anzahl und
  Größe); Behalten/Escape/Schließen behalten den strengen Recoveryfehler. Texte EN/DE/ES/FR/RU.

Belege auf Gate-Head `aaea381` (`/tmp/mc-evidence/final4-1794363138`):
- echter Platzmangel 4/4: 64-KiB-Fall jetzt „blockiert → verworfen → Start frei → Neuimport committet“;
- echter SIGKILL 16/16 (8 Import-, 8 Merge-Checkpoints): Import wie oben, Merge-Ziel unverändert;
- `NativeInterruptedTransferDiscardTests` 7/7 (Committed unberührt, fremder Pfad bleibt, Ordinary
  abgelehnt, Wiederholung idempotent); `InterruptedTransferStartupTests` 3/3 (Host: Verwerfen → Ready,
  Behalten/keine Entscheidung → RecoveryFailed, Dateien erhalten);
- Dialog-Smoke im privaten Xvfb PASS (Zweischritt, Escape bricht Bestätigung ab, Behalten/Escape → behalten).

- Echter App-Start (`MEOWCHAN_INTERRUPTED_STARTUP_ONLY`, privates Xvfb, `/tmp/mc-evidence/discard-2026-11-11/smoke-interrupted.log`):
  Profil mit unterbrochenem Import → Shell zeigt den Dialog, Start wartet in `Starting` → zweistufiges
  Verwerfen → `Ready`, Operationsverzeichnis entfernt, Log „Discarded 1 interrupted native transfer
  operation(s)“; SMOKE PASS.

Nicht belegt: Windows-Umbenennen mit offenen Handles (nur im Windows-Lauf prüfbar).

### Blocker 2 — Plattformbelege (unter Linux nicht ersetzbar)

Keine Windows-, macOS- oder Hardwarebelege. Nach `START-GOAL-1.0.3.md` blockiert das den Kandidaten.

Lokal geprüft am 11.11. (`/tmp/mc-evidence/package-linux-1794363581`, saubere `git archive`-Kopie):
- **Gefundene und behobene Regression:** Der Release-Restore mit den CI-Parametern
  (`-p:RuntimeIdentifier=linux-x64 -p:Configuration=Release --locked-mode -p:SelfContained=true
  -p:PublishSingleFile=true`) scheiterte mit `NU1004` an den sieben experimentellen Quellen aus
  `851ae88` (fehlende bzw. unvollständige Locks je Zielplattform); `v1.0.2` restored mit demselben
  Befehl sauber. Behoben in `fd294b8` und `a804293` (Locks wie beim gleich gebauten Dvach-Plugin).
- Danach auf `a804293`: Restore Exit 0, `dotnet publish` linux-x64 self-contained single-file Exit 0
  (298 MiB), `check-desktop-package.py` **PASS**, `package-plugins.py --verify` **PASS**,
  Paketierungs-Vertragstests `test_linux*` 41/41, `test_desktop_package` 10/10, `test_apphost*runner` 55/55.
- Nicht lokal möglich: die Linux-Distributionspaketierung (`package-linux.py`) und damit
  `check-video-native.py` brauchen die Ubuntu-Paketquellen der CI, `sudo apt-get install` (LibVLC,
  patchelf, squashfs) und eine passende Ubuntu-Basis; dieser Rechner ist Manjaro, Remote-CI ist
  ausgeschlossen. `check-video-native.py` auf dem rohen Publish verlangt erwartungsgemäß
  `linux-video-runtime.json`, das erst die Distributionspaketierung schreibt.
- Eine lokale Ersatzpaketierung in Docker (Ubuntu-Image plus apt-Pakete) wäre technisch möglich.
  Der Nutzer hat sie am 11.11. ausdrücklich abgelehnt: Paketierung und Plattformläufe macht er
  selbst in GitHub Actions. Bis diese Läufe vorliegen, bleibt Blocker 2 offen.

## Belegt auf einem Head

Neuer Gate-Lauf auf `aaea381` (`/tmp/mc-evidence/final4-1794363138`, frischer `--no-incremental`-Build):
Core 9421 bestanden/0 fehlgeschlagen/10 übersprungen, Desktop 3098/0/11, Plugins 360/0/0,
Platzmangel 4/4, SIGKILL 16/16, Dialog-Smoke PASS.

Vorheriger Gate-Lauf auf `4bc4d01` (`/tmp/mc-evidence/final3-latest`, frischer
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
| Gerätezeitzone / „assembly not found“ (`DeviceTimeZone…`-Regressionen aus `b14f4b1`, `ArchiveSearchSpecTests`) | 15 / 0 / 0 |
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

## Einzelnachweise je Anforderung (11.11.)

- Archivfilter, je Anforderung benannt (`/tmp/mc-evidence/archive-filter-requirements-1794364177.txt`): Up/Down + Mehrfachnavigation + Enter
  (`keyboard-multi-navigation-then-enter`), Escape → erneute Navigation (`escape-then-navigation-reopens-list`),
  Board-Kandidat → Add → Search (`board-candidate-add-then-explicit-search`), stale Vorschläge
  (`stale-async-suggestions-never-resurface` + Tests `SupersededInputAndProfileEndCannotPublishLateSnapshot`,
  `LateScopedSourceResponseCannotReplaceNewerExactSource`, `CandidateScopeAndFreeTextRemainDistinctIncludingStaleSelection`),
  Scopewechsel, freie Eingabe, Board-Source-Kopplung: jeweils `pass`.
- Gerätezeitzone, Diagnoseprotokoll (`/tmp/mc-evidence/timezone-diag-1794364164/diagnosis.log`): `MeowChan.deps.json`
  nennt `NodaTime/3.3.6`; `NodaTime.dll` in App-, Smoke- und Test-Bins hashgleich; im Single-File-Publish
  eingebettet; System-tz `Europe/Berlin` über `/etc/localtime` → tzdb; keine installierten/stale MeowChan-Pfade.
  Regressionen `DeviceTimeZoneResolvesToABundledIanaZoneAndNodaTimeIsDeployed`,
  `DeviceTimeZoneFailureNamesTheFactsInsteadOfAGenericAssemblyMessage` bestanden. Meldung nicht reproduziert.

## Was das Gate auflöst

1. Erledigt (`aaea381`): Recovery unterbrochener Transfers.
2. Nutzerentscheidung 11.11.: Paketierung und Plattformläufe übernimmt der Nutzer am Ende per GitHub
   Actions (Codex). Dort zu prüfen: Windows-/macOS-/Hardwareläufe sowie Linux-Distributionspaketierung und `check-video-native.py` auf der Ubuntu-CI-Basis bzw. den Zielplattformen.

## Nachtrag 11.11.: Medienseite (OR-01 bis OR-05) auf goal

Lokal integriert bis `b73eff4`, frisch gebaut, nichts gepusht:
- OR-01 Medienindex/-seite (`f911c8f`, `2222915`), frische Abnahme Core 9433/0/10, Desktop 3098/0/11, Smoke PASS;
  dabei Clean-Checkout-Kompilierfehler im DesktopSmoke behoben (`1dcfb3c`).
- OR-03 Filter (`ed5d975`), OR-04 Bytegruppen (`1bb08b2`), OR-05 Auswahl/Tag/Galerie/Export (`252c02f`),
  Tonfilter und „Alle Treffer exportieren“ (`08c6688`, `c467977`, `879b59d`) aus der Parallelsitzung.
- Port (`d8f8c02`): Video-Poster auf Medienkacheln (`f76c9cf`), Favoritenstatus alle/einige/keine/unbekannt und
  Byteschlüssel SHA-256+Länge (`82cc517`), OR-02 eigener Zustand/Tagbearbeitung (`bec4a0b`), Exporttests (`e7351a9`).
- Kachelhöhe 248 px ohne Überlappung (`49c5bd0`); echte Hauptfenster-Smokes für Tagdialog und Galerie-Schließen
  samt Fokus-Fix, Export-Abbruch/Platzmangel (synthetisch) (`66dcf88`); Galerie-Fit-Smoke an aktuellen Viewer
  angepasst (`b73eff4`, vorher seit spätestens `2222915` rot).

Belege: `/home/tomka/mc-evidence/hostpaths-on-goal2-1794398890` (Core 9508/0/10, Desktop 3121/0/11, Smokes
MEDIA_PAGE, GALLERY_MEDIA_TAGS, MEDIA_RATINGS, PRIVATE_TAGS, MEDIA_PANEL PASS) und
`/home/tomka/mc-evidence/galleryfit-on-goal-1794399663` (Gallery 452/0, GALLERY_FIT, MEDIA_PERFORMANCE PASS).
Echter ENOSPC beim Medienexport (`bf6a87b`, auf goal `ff8200e`): tmpfs-Fixture `/tmp/mc-fs` (64 MiB) mit 0/64 KiB/400 KiB
Restplatz, `MediaLibraryExportTests` 13/0 inkl. drei Platzmangelfällen (`/home/tomka/mc-evidence/media-export-enospc-1794399941`);
ohne Fixture übersprungen, Core 9508/0/11. Blocker 2 unverändert.

## Nachtrag 11.11.: frisches Gesamtgate und Folgefixes (goal `40eba83`)

Frisches Gate auf `2e21d39` in eigenem Worktree mit Locked-Restore (`/home/tomka/mc-evidence/gate-2e21d39-1794400318`):
Core 9508/0/11, Desktop 3121/0/11, PluginFilters 103/0, PluginHost.Configuration 192/0; PluginCompiler 20/0 und
PluginRuntime 38/0 erst mit vorher gebauten Release-Voraussetzungen (PluginCompiler, PluginWorker,
PluginRuntime.Fixture); Smokes suggestions, native-transfer-preview, interrupted-startup, MEDIA_PAGE,
GALLERY_MEDIA_TAGS, MEDIA_RATINGS, PRIVATE_TAGS, MEDIA_PANEL, GALLERY_FIT, MEDIA_PERFORMANCE PASS. Zwei echte Befunde:
- ExamplePlugins.Tests: NU1004 beim sauberen Locked-Restore, weil die sieben experimentellen Quellprojekte im
  Release-Lock fehlten (`3c1e822`); danach 360/0.
- Default- und Exact-IDs-Smoke rot (seit spätestens `2222915`): ein während des Herunterfahrens angenommener
  Settings-Commit blieb ungeschrieben. Ursache: `Registry` nutzte den zugelassenen Speicherpfad nur für ein konkretes
  `SessionStore`, Wrapper fielen auf das nach Shutdown-Beginn abgewiesene `SaveAsync` zurück (eingeführt `59def9d`).
  Produktivpfad nicht betroffen; Fix über `IAdmittedSessionStore` (`f093cb1`). Dahinter verdeckt: veraltete
  Sources-Kartenerwartung (`255c9b6`) und ein Timing-Rennen im Reader-Live-Smoke (`04c705e`).

Abnahme des integrierten Stands `40eba83` (inkl. `ed72624` Medien-Rücksprung), frischer Build
(`/home/tomka/mc-evidence/accept-settings-40eba83-1794410721`): Core 9513/0/11, Desktop 3121/0/11; Smokes Default,
SETTINGS_LIFETIME, MEDIA_PAGE, UI_REFINEMENT PASS. Korrektur: Die dort und im Gate `2e21d39` als „exact-ids“
geführten Läufe setzten nur `MEOWCHAN_ARCHIVE_COMPARISON_EXACT_IDS_ONLY` ohne `MEOWCHAN_ARCHIVE_COMPARISON=1` und
waren damit ein zweiter Default-Smoke. Der echte Archivfilter-Lauf auf `40eba83`
(`/home/tomka/mc-evidence/archive-filter-40eba83-1794412314`, `scenarios.txt`) besteht 12/12, u. a.
`keyboard-multi-navigation-then-enter`, `escape-then-navigation-reopens-list`, `board-candidate-add-then-explicit-search`,
`stale-async-suggestions-never-resurface`, `explicit-scope-change-revokes-previous-board-qualification`,
`new-free-input-trims-without-normalizing-existing-identities`. Lastbedingte Einzelausreißer
(MediaLibraryIndex-Performance, FIFO-Watchdogs, Plugin-Session-Start bei Load ~65) liefen gezielt wiederholt grün
(`/home/tomka/mc-evidence/reader-race-1794410058`, `settings-*-rerun.log`). Blocker 2 unverändert.

Abschlussstand des Goals auf `goal`: Alle verbindlichen 1.0.3-Anforderungen sind lokal integriert und durch die
Volltests auf `40eba83` sowie die Bereichs-, Einzel- und Smokebelege oben gedeckt. Der verbleibende echte
Blocker ist Blocker 2 (Windows-/macOS-/Hardwareläufe, Linux-Distributionspaketierung): unter diesem Manjaro-Rechner
nicht herstellbar, Remote-CI durch den Auftrag ausgeschlossen, Ersatz per Docker vom Nutzer abgelehnt; die Läufe
übernimmt der Nutzer in GitHub Actions. Bis dahin kein freigegebener 1.0.3-Kandidat.

### Bereichsläufe auf `40eba83` (`/home/tomka/mc-evidence/areas-40eba83-1794412407`)

Gezielte `dotnet test --no-build`-Filter auf den frischen Bins der Abnahme oben:
Transfer/v2-Merge/Private Apply/Consent/Konflikte Core 6276/0/9; Importdialog/Konflikt Desktop 117/0;
Bestandsschutz Favoriten/Bookmarks/Bewertungen/Notizen 892/0; Registry/Startup/Admission 783/0;
FirstRun/Settings 277/0; Galerie/Video/Playback/Mute 592/0/1; HTML-/Export 176/0/3; Lokalisierung 432/0;
Kontextmenü/Clipboard 154/0; passive GP-39 14/0; Archivfilter/Vorschläge 35/0; Quellenwording/Experimental
Desktop 105/0; Beispiel-/Experimental-Plugins frisch Locked-Restore 360/0 mit Treffern für alle sieben Quellen
(anonimageboards, bbwchan, capybarachan, eightchanmoe, fchan, gurocx, sevenchan).
Gerätezeitzone: `DeviceTimeZoneResolvesToABundledIanaZoneAndNodaTimeIsDeployed` und
`DeviceTimeZoneFailureNamesTheFactsInsteadOfAGenericAssemblyMessage` bestanden (Datumsbereich/Zeitzone 15/0).
Diagnose (`timezone-diagnosis.log`): `MeowChan.deps.json` nennt `NodaTime/3.3.6`, `NodaTime.dll` in App-, Smoke-,
Desktop- und Core-Bins hashgleich (`1421cc81…`), System-tz `Europe/Berlin` über `/etc/localtime`, keine
installierten oder veralteten MeowChan-Pfade. „assembly not found“ damit weiterhin nicht reproduziert.

### Recovery, Reexport und Offline auf `40eba83` (`/home/tomka/mc-evidence/recovery-40eba83-1794412954`)

Reexport/Offline/Passive: Core 522/0/1, Desktop (inkl. Neustart) 91/0/6. Cancel/Recovery/Unterbrechung:
Core 1019/0, Desktop 133/0. Echter SIGKILL (`MEOWCHAN_CRASH_RUN=1`): Import 8 Checkpoints + Kandidatenverzeichnis-
Recovery 9/0 (1 Child-Helfer erwartungsgemäß übersprungen), Merge 8/0. Echter Platzmangel auf privatem 64-MiB-tmpfs
(User-Namespace-Mount, kein Root): `DiskFull`- und Medienexporttests 17/0, nichts übersprungen.
Archivfilter-Regressionen wurden in diesem Goal ergänzt (`5193d38` Escape→Pfeil-Reopen und Tastaturfälle,
`d28283d` Klickübernahme, `c45a972` Fokus nach Tag-Entfernen) und laufen im Smoke oben 12/12 grün.
Neu ergänzt (Mehrfachauswahl): Smoke-Fall `multiple-accepted-candidates-collect-in-draft-then-one-search`
(zwei Quellen und ein Board nacheinander per Tastatur übernommen, kein Suchstart bis zur expliziten Suche, eine Suche
mit allen drei Identitäten). Archivfilter-Smoke damit 13/13 (`/home/tomka/mc-evidence/archive-multi-1794413413`).
