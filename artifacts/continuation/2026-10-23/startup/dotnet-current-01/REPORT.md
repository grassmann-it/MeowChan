# Aktuelle .NET-PluginSession: beobachtete Startphasen

Read-only-Auswertung vorhandener gefrorener Linux-GUIläufe; kein neuer Build, Test oder GUIstart.
Gemessen ist PluginSession `total`, nicht Prozessstart bis zum fertigen Desktop. Jede Zeile hat fünf Imports, fünf Describe-Phasen und danach Startup `Ready`.

## Eindeutig gebundene, erfolgreiche Gesamtläufe

| Gruppe | Modus | Skalierung | PluginSession gesamt | Imports zusammen | Describe zusammen |
|---|---|---:|---:|---:|---:|
| ui-03-remaining/reader-board-1 | reader-ux | 1 | 5.507 s | 1.033 s | 4.260 s |
| ui-03-remaining/reader-board-1 | board-admission | 1 | 2.789 s | 0.362 s | 2.202 s |
| ui-03-remaining/downloads-1 | normal | 1 | 5.191 s | 1.460 s | 3.005 s |
| ui-03-remaining/reader-board-1.5 | reader-ux | 1.5 | 4.378 s | 0.719 s | 3.524 s |
| ui-03-remaining/reader-board-1.5 | board-admission | 1.5 | 2.390 s | 0.380 s | 1.834 s |
| ui-03-remaining/downloads-1.5 | normal | 1.5 | 3.963 s | 0.532 s | 3.145 s |
| ui-03-remaining/reader-board-2 | reader-ux | 2 | 2.944 s | 0.538 s | 2.215 s |
| ui-03-remaining/reader-board-2 | board-admission | 2 | 2.670 s | 0.574 s | 1.896 s |
| ui-03-remaining/downloads-2 | normal | 2 | 3.107 s | 0.547 s | 2.393 s |

9 erfolgreiche, exakt gebundene Starts: Minimum 2.390 s, Median 3.107 s, Maximum 5.507 s.

## Gesamtprüfung fehlgeschlagen, Startup separat Ready

Die folgende Zeile gehört zu einem mit Exit 1 fehlgeschlagenen refinement-Gesamtlauf. Sie wird nicht in die obigen Erfolgsstatistiken gemischt.

| Gruppe | Modus | Skalierung | PluginSession gesamt | Imports zusammen | Describe zusammen |
|---|---|---:|---:|---:|---:|
| ui-03/refinement-1 | normal | 1 | 3.231 s | 1.425 s | 1.640 s |

## Unbekannte genaue Runtimezuordnung

ui-reader-board-02 hat erfolgreiche Modeergebnisse, aber keine erhaltene eindeutige Invocation/Runtimebindung. Diese Werte stehen ausschließlich hier und sind aus dem Hauptvergleich ausgeschlossen. ui-02/source.json darf ihnen nicht nachträglich zugeschrieben werden.

| Gruppe | Modus | Skalierung | PluginSession gesamt | Imports zusammen | Describe zusammen |
|---|---|---:|---:|---:|---:|
| ui-reader-board-02 | reader-ux | 1 | 3.200 s | 0.647 s | 2.400 s |
| ui-reader-board-02 | board-admission | 1 | 2.087 s | 0.420 s | 1.523 s |

## Bindung und Reproduktion

Die exakt gebundenen Läufe verwenden `integration/ui-03/runtime`, Quellsnapshot `04e676d9c76722fd31a70d271db5d107f3212f7f`. Maßgeblich sind `artifacts/continuation/2026-10-23/integration/ui-03/source.json` einschließlich dort aufgeführtem WIP und `runtime-hashes.json` mit 1166 Einträgen. Die Hauptassemblies, Workerapphost und Toolchainmanifest wurden während der Aggregation gegen die aufgezeichneten Hashes geprüft; keine erneute Gesamtvalidierung aller 1166 Runtimeeinträge.

`run-ui-03.py`/`ui-03/checks.json` binden refinement; `run-ui-03-remaining.py`/`ui-03-remaining/checks.json` binden die übrigen ui-03-Gruppen an denselben eingefrorenen Pfad. Modebezogene Status/Exitcodes kommen aus den jeweiligen `results.json`. RESULT.json enthält exakte Referenzpfade und SHA256 für alle ausgewerteten Logs, Ergebnisdateien, Quellen-/Hashmanifeste und Runner; jede Phase trägt ihre ursprüngliche Logzeile.

```sh
python3 artifacts/continuation/2026-10-23/startup/dotnet-current-01/aggregate.py --repo /home/tomka/Desktop/oChan --inputs artifacts/continuation/2026-10-23/startup/dotnet-current-01/inputs.json --output artifacts/continuation/2026-10-23/startup/dotnet-current-01
```

`inputs.json` fixiert die ausgewerteten, fertig gemeldeten Gruppen. Neuere oder noch laufende Gruppen werden nicht automatisch in den Nachweis aufgenommen. Die in RESULT.json erfassten Hashes der wachsenden ui-03-remaining/checks.json gelten für den Aggregationszeitpunkt; bei späterem erneuten Ausführen können dort zusätzliche Prüfungen stehen. Die unveränderten Mode-Logs/Ergebnisse und die feste Eingabeauswahl bestimmen die Zahlen.

## Aussagegrenzen

Die Logs zeigen je Mode einen eigenen privaten Profilpfad; alle erfassten Pfade sind verschieden. Das sind frische Profile und keine Cache-Restarts desselben Profils. Der Dateisystemcache ist durch vorherige Builds und Läufe möglicherweise warm; er wurde nicht geleert. Systemlast und CPU-/I/O-Konkurrenz sind variabel und wurden hier nicht aufgezeichnet oder kontrolliert. Skalierungen und GUIzwecke sind keine gleichartigen Benchmarkwiederholungen.

`import` umfasst die vollständige bestehende Importarbeit einschließlich Toolchain-, Bundle- und Cachevalidierung; es ist kein isoliertes .NET-SHA256-Maß. `describe` umfasst Host-Artefaktprüfung, Ressourcen-/Startgates, Workerstart, hello/describe, SDK-Validierung und vollständiges await-Dispose. Daraus lässt sich keine einzelne Unterursache des variablen Zeitbedarfs ableiten. `PluginStartupCompleted=True` bezeichnet normalen Awaitabschluss; zusätzliches Ready und der Mode-Gesamtstatus werden separat ausgewertet.

Die älteren 8–12 Sekunden bleiben ein eigenständiger früherer Messbefund unter anderen Bedingungen. Diese neuen Werte erklären oder widerlegen ihn nicht und belegen keinen Speedup durch die reine Instrumentierung. Sie liefern keine Plattform-, Cold-Cache-, Last- oder p95/p99-Garantie und sind kein SDKfreier publizierter Apphostnachweis.
