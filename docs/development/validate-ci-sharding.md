# Laufzeitbasierte Validate-CI

Der Validate-Plan verteilt vollständige Testklassen, registrierte GUI-Szenen und
die bestehenden dedizierten Prüfungen. Pushes, Pull Requests und Kandidaten
führen weiterhin den vollständigen Umfang aus. Der Plan liegt mit Checkout-SHA,
Szenario und Worker-Pflichten im Laufartefakt `validate-plan`.

## Referenzlauf

Die 28 Validate-Jobs in Lauf 44930153815 auf `1.1.1` verbrauchten zusammen rund
927 aktive Runner-Minuten. Auf Builds entfielen rund 497 Minuten und auf
Offline-Tests 217 Minuten. Die API listet 44 Jobs einschließlich der neun
übersprungenen. Der gesamte Lauf dauerte rund 128 Minuten. Intel `native-07`
startete erst etwa 88 Minuten nach Beginn des Laufs. Runner-Wartezeit und
Paketjobs beeinflussen die Laufzeit des ganzen Workflows.
[GitHub Actions Run 44930153815](https://github.com/grassmann-it/MeowChan/actions/runs/44930153815)

| Referenzworker | Aktiv | Setup¹ | Restore² | Builds | Offline | GUI-Szenen³ | Upgrade 1.0.0/1.0.1 | Upgrade 1.0.2 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Ubuntu x64 A | 37:17 | 1:08 | 0:06 | 12:50 | 8:02 | 5:14 | 3:55 | 2:55 |
| Ubuntu ARM64 A | 48:07 | 1:06 | 0:06 | 17:21 | 8:45 | 6:31 | 6:01 | 4:39 |
| Windows x64 A | 56:17 | 0:43 | 0:11 | 23:03 | 9:27 | 7:24 | 6:15 | 5:45 |
| macOS ARM64 native/1 | 27:48 | 0:28 | 0:07 | 13:18 | 5:23 | 1:35 | 3:12 | 2:43 |
| macOS Intel native/1 | 62:42 | 0:39 | 0:30 | 31:07 | 12:16 | 2:34 | 7:39 | 5:49 |

- ¹ Setup enthält Checkout, SDK und gegebenenfalls Linux-GUI-Setup.
- ² Restore enthält die separat ausgewiesenen Release- und Harness-Restores.
  Implizite Restores bleiben im jeweiligen Build oder Replay enthalten.
- ³ GUI-Szenen bezeichnen den Sammlerschritt. Dedizierte Smokes, Fixtures,
  Diagnostik und Upload erklären den übrigen Anteil.

Auf dem Intel-Primärworker entfielen 13:24 allein auf den Build des
Smoke-Harnesses. Portable Lane B auf Windows verbrachte 15:43 im ungeteilten
Core-Projekt. Die höchsten aufsummierten TRX-Zeiten je Klasse lagen bei
`StabilityRunnerHostTests` (244 s), `NativeTransferArchiveBindingBudgetTests`
(122 s), `FolderSourceScanBench` (97 s), `ArchiveCleanupAcceptanceHostTests`
(78 s) und `YieldingDefaultsTests` (74 s). Unter den einzelnen GUI-Szenen lagen
Intel `normal-core` (153 s), Intel `settings-core` (145 s) und Intel
`reader-stability` (112 s) vorne. Klassenzeiten können sich durch parallele
Testausführung überlappen.
[Intel-Primärjob](https://github.com/grassmann-it/MeowChan/actions/runs/44930153815/job/136924797427) ·
[Windows Lane B](https://github.com/grassmann-it/MeowChan/actions/runs/44930153815/job/136924797369)

Die zwölf fehlgeschlagenen portablen Desktop-Zellen und vierzehn
fehlgeschlagenen nativen Desktop-Suiten meldeten Exitcode 1 in den
Worker-Receipts. Der generische Text „failed/timed out“ im Manifest umfasst
mehrere Fehlerarten. In den TRX-Dateien stehen unter anderem die
Serialisierungsprüfung in `SourceScanGuardTests`, eine
`NullReferenceException` in `GalleryAutoHideCleanTests` und die
Schriftgrößenprüfung in `TypographyStyleTests`. Die GUI-Szenen
`gif-playback-core`, `gallery-errors-layout-roots` und `reader-navigation`
scheiterten auf allen fünf Plattformen an ihren Szenenprüfungen. Dazu zählen
kurze Wartefristen im Harness bei einer ausbleibenden UI-Reaktion. Der äußere
Worker-Watchdog lief bei diesen Fehlern nicht ab.
[Linux Lane A](https://github.com/grassmann-it/MeowChan/actions/runs/44930153815/job/136924797168) ·
[Native Coverage-Gate](https://github.com/grassmann-it/MeowChan/actions/runs/44930153815/job/136957840959)

## Verteilung und Belege

- `portable_shards` steuert 4 bis 12 Worker je Windows- oder Ubuntu-Runner.
  Standard sind **5**. Die zusätzlichen Lanes erhalten eigene Desktop-Klassen.
  Die übrigen Projekte durchlaufen weiterhin ihre vorhandenen Projektgruppen
  genau einmal. `offline-retest` mit demselben Wert für `portable_shards`
  reproduziert die Klassenzuordnung einer Lane des Volllaufs.
- `macos_shards` steuert 8 bis 12 Worker je nativer Architektur. Standard sind
  **9**. Die Core- und Desktop-Klassen werden je Suite auf alle Worker
  verteilt. Die Upgrade-Replays bilden zwei eigenständige Schritte. Beim
  Standardplan gehen sie auf Intel an `native-02` und `native-03`.
- `scripts/balance-validation.py` platziert zuerst die längsten Aufgaben auf
  dem Worker mit der aktuell geringsten geschätzten Last. Die Grundlage sind
  `build/validate-ci-timings.json` für Szenen, dedizierte Checks und feste
  Worker-Kosten und `scripts/macos-test-timings.json` für Klassen. Worker
  berechnen ihre Testfilter aus der vollständigen VSTest-Discovery. Die
  Klasse ist die kleinste Planungseinheit. Neue Klassen erhalten ein
  Gewicht pro entdecktem Testfall, neue Szenen ein Standardgewicht.
- Der Plan weist die 33 Validate-Szenen und alle vorgesehenen dedizierten
  Checks pro Plattform genau einmal zu. Jeder Worker prüft seine
  Szenenliste gegen die Registry der gebauten Assembly und die tatsächlichen
  Ergebnisse gegen seine Zuteilung. Der separate Registry-Nachtlauf und
  die vorhandenen Einzelretests behalten ihre eigene Reichweite. Die neun
  zusätzlichen Registry-Szenen auf Linux x64 bleiben für Kandidaten Pflicht.
  Der Kandidaten-Schritt verlangt einen erfolgreichen Beleg je ID.
- Beide Offline-Gates erwarten die vollständige Workerzahl, Quelle, Plattform,
  vorhandenen Belege und konsistente Pläne. Sie lesen die tatsächlichen TRX erneut,
  prüfen die Fallidentitäten und verwerfen fehlende, doppelte oder fremde
  Resultate. Die bisherigen Fristen und Regeln für explizite Skips und
  dynamische Theories gelten weiter. Der Abschluss verlangt den Planner,
  die Validate-Matrix und die zur Auswahl gehörenden Offline-Gates.
- NuGet speichert nur die Pakete. Die Locked-Mode-Restores und
  `NU1900`-Prüfungen laufen weiter. Der Build-Cache enthält den Release-Output
  des Smoke-Harnesses und ist an Checkout-SHA, Runner-Label, Architektur,
  Image-Version und SDK-Pin gebunden. Es gibt keinen Teiltreffer. Nur ein
  erfolgreich gebautes und auf Plugin-Lieferung geprüftes Harness wird
  gespeichert. Ein Treffer muss die Harness-Assembly enthalten und durchläuft
  erneut die Plugin-Prüfung. Der übrige Solution-Build wird regulär ausgeführt.

Der Planer separiert Umfang, Zuteilung und Ausführungsbelege. Eine spätere
Auswahl mit Stichprobe kann den erwarteten Umfang vor der Zuteilung bereitstellen.
Die Vollabdeckung für Kandidaten bleibt ein eigener Vertrag.

## Zeitmodell und Probelauf

Mit den Median-Buildzeiten von Lauf 44930153815, den beobachteten
Szenen-/Replay-Gewichten und dem festen Windows-Core-Besitzer schätzt das
Modell für den längsten aktiven Worker pro Plattform rund **31 min Ubuntu
x64**, **32 min Ubuntu ARM64**, **38 min Windows**, **25 min macOS ARM64**
und **35 min macOS Intel**. Diese Modellpunkte setzen einen kalten Build voraus.
Ein Harness-Cachetreffer spart modelliert weitere 6–8 Minuten auf dem
betreffenden Worker. Stärkere Runner-Streuung und Queue-Zeit sind offen.
Der Standard erhöht die Validate-Matrix von 28 auf 33 Worker plus Planner.
Der Gewinn an aktiver Zeit kann auf ausgelasteten Intel-Runnern durch
zusätzliche Wartezeit schrumpfen.

Plan und Testinventare lassen sich ohne Testausführung prüfen.

```bash
python3 scripts/balance-validation.py matrix --event workflow_dispatch \
  --mode validate --platform macos-15-intel --macos-shards 10

nice -n 10 dotnet test tests/MeowChan.Tests/MeowChan.Tests.csproj -c Release \
  --list-tests --diag /tmp/core-vstest.log
nice -n 10 python3 scripts/balance-validation.py classes \
  --discovery /tmp/core-vstest.log --shards 9

nice -n 10 python3 scripts/balance-validation.py classes \
  --inventory /path/to/discovery-inventory.json --shards 5
```

Beim lokalen Trockenlauf ergaben sich 4.020 aktuelle Core-Methoden und
7.123 Desktop-Methoden aus dem historischen Discovery-Inventar. Die aktuelle
Desktop-Discovery scheiterte vorher am Compilerfehler CS0104 für
`TagSuggestion` in `TagSuggestionTests.cs:22`. Im gebauten lokalen
Smoke-Harness fanden sich alle 33 Validate-Szenen und alle neun zusätzlichen
Kandidaten-Szenen unter 228 Registry-Einträgen.

Die Referenzdaten in `build/validate-ci-timings.json` verwenden die
Schrittzeiten aus der Jobs-API und die `duration_seconds` aus den
GUI-Collections. Wo möglich stammen die Szenengewichte aus abgeschlossenen,
erfolgreichen Proben. Die Kandidaten-Registry-Gruppe hat ein vorläufiges
Gewicht von 300 s. Das Klassenprofil übernimmt konservativ ältere Maxima
und ergänzt aus dem Referenzlauf Core-Klassen ab 5 s und Desktop-Messungen
ab 20 s. Für die Aufbereitung neuer TRX-Daten steht
`scripts/update-macos-test-timings.py` zur Verfügung. Profiländerungen immer
mit unveränderter Testinventur und einer Trockenverteilung prüfen.

Ein echter CI-Probelauf muss beide Architekturen je nach Runnerangebot,
den Default und mindestens einen benutzerdefinierten Count bestätigen.
Die Coverage-Artefakte und der vollständige Prüfsatz müssen auch auf den neuen
Lanes erscheinen. Inhaltliche Fehler und Timeouts separat erfassen. Bei einem
zweiten Lauf desselben SHA die NuGet- und Harness-Cachetreffer,
Buildzeiten, Transferzeiten, Runner-Wartezeit und den Gesamtabschluss messen.
Ein Kandidatenlauf muss die ursprünglichen Upgrade- und Registry-Belege sowie
die nativen Kandidatenfristen bestätigen.
