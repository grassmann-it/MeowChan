# Pluginstart: Phasen, Baseline und Messanschluss

Stand 23.10.2026. Quellprüfung auf `77e091b113fb5b94f68058ee5e375ae93e64def7`,
Instrumentierung `348431d`. Kein eigener .NET-Build/-Test, keine GUI gestartet.
Keine ursächlich belegte Geschwindigkeitskorrektur; keine Zeitgrenzen-, Cache-,
Grant-, Aktivierungs- oder Integritätsänderung. Root baut und prüft den aktuellen
Stand im Originalordner.

## Codepfad und Sicherheitsgrenzen

`Desktop/PluginSession.cs:StartAsync` liest Profil und zwei Inventare. Fünf
ausgelieferte Prebuilt-Manifeste laufen seriell durch `ImportBundleAsync`, danach
`DescribeTestAsync`. Erst nach allen Kandidaten folgt `ApplyStartupAsync` mit
erwarteter Profilrevision; anschließend entstehen Adapter und die Routingprüfung.
Dieser Ablauf ist durch die Instrumentierung unverändert.

`PluginCompilationService.Bundles.cs:ImportBundleCoreAsync` besitzt das BuildGate,
liest das Bundlemanifest und validiert über `CompilerWire.ReadToolchainAsync` bei
jedem Import erneut alle Toolchaindateien. Es prüft Bundle-DLL und Ressourcen und
auch bei Cachetreffern den Artefaktinhalt. Im geprüften Paket: 171 Dateien und
16.145.320 Bytes pro Toolchainvalidierung, also 855 vollständige Dateihashprüfungen
und 80.726.600 Bytes bei fünf Imports. Der Byteumfang beweist keine 8–12 Sekunden.
`ReadBoundedAsync` nutzt sichere no-follow/nonblocking Opens, bounded Reads und
Inhalts-SHA256; dies wurde nicht verändert.

`PluginWorkerManager.DescribeAsync` besitzt Test- und Startgates. Der Testworker
hat standardmäßig genau einen Slot. `WorkerConnection.StartAsync` verifiziert das
Artefakt erneut, erstellt den Arbeitsordner, startet den Worker mit Heaplimit und
abgeschalteter Diagnostik, führt hello/describe und SDK-Validierung aus.
`DescribeAsync` wartet durch `await using` auch auf Dispose: Cancellation,
Beenden des eigenen Prozessbaums, Exit, Reader/Logs/Monitor und Workspaceabbau.
Die Fünfsekundenwerte dort sind obere Wartegrenzen, kein bewiesener Normaldelay.
Der einsekündige Temporary-Monitor verwendet einen abbrechbaren Timer.

Eine parallele Schleife allein überlappt fünf Describe-Aufrufe nicht: der
Testslot bleibt seriell. Eine Änderung dieses Ressourcenvertrags, ein dauerhafter
Toolchain-Metadatencache oder ein Überspringen von Describe auf Cachetreffern wäre
keine belegte, sichere Lösung dieses Befunds.

## Ausgeführte Helperbaseline

Explizit eingefrorenes Linuxpaket aus
`artifacts/plugin-implementation/2026-10-22/integration/final-01/candidate/linux-publish`.
Quellbindung: dortiges `candidate/source.json` nennt
`08e8711bcb3ffcf8df6e6e189e95e05cf6eed841`. Dateihashes von Toolchainmanifest,
Workerapphost und fünf Manifesten stehen in `helper-baseline-01/RESULT.json`.
Diese ältere Helperprobe ist kein aktueller Desktop-77e091b-Lauf.

Ausgeführt unter dem gemeinsamen Lock, nach Root-Freigabe des freien Slots:

```sh
flock /tmp/meowchan-ui-1.0-build.lock python3 /home/tomka/Desktop/oChan-sol61-workers/startup/artifacts/continuation/2026-10-23/startup/measure.py --repo /home/tomka/Desktop/oChan --payload /home/tomka/Desktop/oChan/artifacts/plugin-implementation/2026-10-22/integration/final-01/candidate/linux-publish --output /home/tomka/Desktop/oChan-sol61-workers/startup/artifacts/continuation/2026-10-23/startup/helper-baseline-01 --repetitions 3
```

PASS: 15/15 echte ausgelieferte Worker führen hello/describe und sauberen Exit
aus. Bubblewrap: kein Netz, SDK verdeckt, Installation read-only, je Kandidat
eigener Arbeitsordner. Python hasht davor für jeden Kandidaten sämtliche
Toolchaindateien gegen das Manifest und die Bundle-DLL gegen deren Manifest.

| Messung | Minimum | Median | Maximum |
|---|---:|---:|---:|
| Python: komplette Toolchain lesen/hashen | 11,80 ms | 13,76 ms | 73,00 ms |
| Spawn bis hello abgeschlossen | 66,88 ms | 74,11 ms | 99,09 ms |
| Describe-IPC nach hello | 67,36 ms | 83,97 ms | 148,30 ms |
| stdin schließen bis sauberer Exit | 3,14 ms | 7,25 ms | 31,40 ms |
| Worker einschließlich obiger drei Phasen | 143,40 ms | 160,90 ms | 247,52 ms |

Fünfergruppen: Python-Vollhash insgesamt 135,97 / 67,92 / 75,08 ms;
Worker insgesamt 946,81 / 782,07 / 826,92 ms. Loadavg während der kurzen Probe
etwa 3,97 / 5,79 / 13,93 auf 16 logischen CPUs. Kein künstlicher Stress oder
Pagecacheflush. Die erste Probe kann teilweise warme Dateiseiten geerbt haben;
es ist kein garantierter Cold-Cache-Benchmark.

Grenzen: Python-Datei-I/O/SHA256 misst nicht die .NET-Validierung mit deren
Path-/Symlink-/Bounded-/JSON-/Fingerprintprüfungen und Asyncplanung. Der
Workerzugriff erfolgt direkt auf die ausgelieferte DLL, ohne Desktopimport und
Host-Artefaktprüfung. Graceful stdin/exit entspricht nicht dem Host-Dispose mit
Prozessbaum und Hintergrundtasks. Niedrige Levelzeiten grenzen Ursachen ein,
beweisen aber keine Höchstzeit für den echten Desktop oder andere Lastzustände.
Das ursprüngliche Roh-JSON enthält zusätzlich `workerCpuMs`; dieser Wert erfasst
wegen Bubblewrap-Prozessnamespace nur den Launcher und ist als Worker-CPUzeit
ungeeignet. Die endgültige Probedatei erhebt diesen Wert daher nicht mehr; er
wird in dieser Auswertung nicht verwendet.

## Aktuellen Desktop messen

Commit `348431d` ergänzt ausschließlich Debug-Logging mit monotoner Stopwatch.
Der vorhandene Logger muss Debug zulassen; keine neue Umgebungseinstellung und
keine neue UI. Properties:

- `PluginStartupPhase`: `profile-read`, `inventory-shipped`, `inventory-local`,
  `import` oder `build`, `describe`, `activation`, `adapters-routing`, `total`.
- `PluginStartupCandidate`: bei Import/Build/Describe ausschließlich Dateiname.
- `PluginStartupElapsedMs`: Dauer der jeweiligen Phase; `total` enthält alle.
- `PluginStartupCompleted`: Await/Operation hat normal zurückgegeben. Ein Import
  kann mit regulärem Fehlerresultat zurückkehren; `true` bedeutet keinen
  erfolgreichen PluginBuildResult. Fehler/Abbruch erzeugt im finally `false`.

`adapters-routing` meldet nur normalen Abschluss, `total` wird auch im Fehlerfall
gemeldet. Keine Profil-, Ressourcen-, Grant- oder Konfigurationsinhalte im Log.
Das PluginSession-Gesamtmaß ist im Desktop-Gesamtstart enthalten und darf nicht
zusätzlich dazu summiert werden. Kein Instrumentierungslog für Compiler-Unterphasen;
`import` umfasst absichtlich die gesamte unveränderte Importvalidierung.

Minimaler echter Lauf: vorhandenen DesktopPluginSessionIntegration-Testfall
`DeliveredSourcesAndFilterUseTheRealDesktopSessionThenRestoreTwiceOffline` mit
Debuglogger ausführen, unter demselben globalen Lock und neuem privaten Profil.
Frischimport und zwei Cache-Restarts ergeben drei Gesamtdauern plus pro Kandidat
fünf Import-/Describe-Zeiten. Das bestehende `DESKTOP_PLUGIN_STARTUP`-Output misst
zusätzlich den ganzen Desktopstart; derzeit injiziert der Test keinen Debugsink,
weshalb der Rootrunner für Phasenausgabe den Logger entsprechend konfigurieren
muss. Ein im Product-Harness konfigurierter globaler Debuglogger reicht ebenfalls.
Die vorhandenen funktionalen Assertions prüfen unverändert fünf aktivierte
Plugins, Bundle-/Cache-DLL-Hashes, Quellen, persistierte Grants und Offline-Restarts.
Ausgeführter .NET-/Instrumentierungsnachweis bleibt bis zum Rootlauf offen.

## Hypothesen und nächster Entscheidungspunkt

1. Dominiert `import`, ist zuerst .NET-Dateiplanung/Pathprüfung/Toolchainlesen
   unter identischer Last zu trennen. Der Pythonbefund widerlegt keine
   Threadpool-/I/O-Planungsverzögerung; CPU-Zeit und .NET-I/O-Stapel fehlen noch.
2. Dominiert `describe`, liegen Host-Artefaktprüfung, Test-/Startgate,
   Threadpoolplanung oder Host-Dispose vor der niedrigen reinen Worker-IPC-Latenz.
   Dann gezielt WorkerConnection-Phasen messen, statt Timeout oder Slotzahl zu ändern.
3. Liegt Desktopgesamtzeit deutlich über `PluginSession total`, liegt die
   Mehrzeit außerhalb dieses Starts (z. B. Profil-/Archiv-/Registry-Wiederherstellung).
4. Eine erhöhte Last oder ein erstmaliger JIT-/Dateiseiteneffekt kann beide
   Teilpfade treffen. Drei kurze Proben liefern keine p95/p99 oder Lastgarantie.

Ergebnis: Die 8–12 Sekunden sind hier nicht reproduziert und bleiben ohne echte
aktuelle Desktopphasen ungeklärt. Eine Beschleunigung wurde bewusst nicht als
bewiesen ausgegeben. Vorhandene Sicherheitsprüfungen bleiben vollständig erhalten.
