# R10-08 — Filterstage und ImageDimensionsFilter

Stand: 22.10.2026. Rolle `build_filter`, Worktree
`/home/tomka/Desktop/oChan-1.0-workers/filter`, Branch `impl/1.0-wave2-filter`.
Code und Tests dieses Berichts: `5347b38` auf der unten genannten Abhängigkeitsbasis.

Die Core-Regeln, der Originalfilterpfad und das SDK-only-Beispiel sind implementiert.
30 tatsächliche Filtertestfälle und 137 bestehende Coretestfälle bestanden auf
Linux x64. Dies ist der Liefernachweis dieser Rolle; Desktop-GUI, integrierte
Releasepakete und andere native Plattformen sind gesonderte Root-Abnahmen.

## Eigene Liefercommits

In dieser Reihenfolge übernehmen; bereits integrierte Commits nicht wiederholen:

| Commit | Inhalt |
| --- | --- |
| `d01a843` | Vererbte Filterregeln, Settingspersistenz, dauerhafte Auslassungen und bestehender Transfervertrag |
| `fb5d7d4` | Hoststage, Chunkreader, Byte-/Hash-/Revisionsbindung und SDK-only ImageDimensionsFilter |
| `a183f39` | Tatsächlicher Worker-/Ressourcenhelperpfad, FilterRuleSettings und Retry-/Statusanschlüsse |
| `e5706cf` | Additives DownloadItem.Variant bei erhaltenem bisherigen Konstruktor |
| `92e5062` | Raw-Varianten in Content/Store/Reader, autorisierter Bypasscommit und Recoveryanschluss für Zielkopien |
| `5347b38` | Slotfreigabe, native begrenzte Journal-Recovery, Stage-IO, Crash-/Queue-/Restart-/JPEGtests |

Der nachfolgende Berichtcommit enthält ausschließlich diesen Bericht und die
beiden Testlogs. Keine fremden Abhängigkeitscommits werden als eigene Lieferung
ausgegeben. Kein Push, Release oder Merge wurde vorgenommen.

## Verhalten und Integrationsanschlüsse

- Die effektive Regel enthält Plugin-ID, Filterkey und eigene Regelparameter.
  Günstige Metadatenregeln entscheiden vorher. Filterresultate ändern weder
  Postcapture noch bestehende Archivdateien. NoMatch und technische Auslassung
  sind dauerhafte, unterschiedliche Entscheidungen; nur Storeerfolg zählt als
  gespeichertes Medium.
- Der bestehende DownloadWorker beschafft ein Original genau einmal über die
  bisherige HTTP-/Bandbreitenpolicy. Die Filterstage reicht nach Match dasselbe
  geöffnete Artefakt an den Store. Dieser prüft den SHA-256 erneut und autorisiert
  vor Veröffentlichung erneut. Zielkopie und Manifest verwenden den bisherigen
  Storevertrag, auch über Dateisystemgrenzen hinweg.
- Startgrenzen: zwei Prüfungen, 32 MiB pro Bild, 512 MiB eigene temporäre Daten,
  16.777.216 Pixel laut SDK und maximal 64 KiB pro ReadImage-Aufruf. Stage,
  Workerarbeitsdaten und eigene Zielkopie gehen in die Tempbetrachtung ein.
  Admission bleibt abbrechbar. Der Queueowner hält Kapazität für normale
  Downloads frei.
- Assettoken sind an Plugin, Operation, Regel-/Konfigurations-/Grantrevision,
  Watchgeneration und Lebensdauer gebunden. Widerruf und Revisionwechsel
  entwerten die Antwort. Stage-/Header-/Integritätsfehler werden technische
  Auslassungen; tatsächliche Ziel-IO bleibt normale Zielwartebehandlung.
- Profilgeschützte Journale ordnen eigene Stagebytes und Ziel-.part zu.
  Journalupdates sind atomar; Neustart behandelt auch alleinstehende `.json.next`.
  Journalreads sind auf 64 KiB begrenzt und öffnen unter Unix nichtblockierend
  ohne Linkfolge; `fstat` lässt nur reguläre Dateien zu. Recovery entfernt nur
  zugeordnete eigene temporäre Dateien und erhält fertige/fremde Medien.
- `FilterStageCoordinator.Attach(content, resolver)` verbindet Content und
  `FilterBindingRequest`/`FilterPluginBinding`. Workergebundene Hostservices
  routen ReadImage auf `ReadImageAsync(pluginId, token, offset, count, operation, ct)`.
- Runtime startet mit `filterWorker: true`.
  `PluginWorkerOptions.ExternalTemporaryBytes = () => stage.Snapshot.StagedBytes`
  und `FilterStageOptions.WorkerTemporaryBytes = manager.GetTemporaryBytes`
  verbinden die Budgets. Der Runtime-Tempcounter ist für eine Sekunde gecacht;
  Timeouttests prüfen deshalb auch die tatsächlich verbliebenen Dateien.
- Activation bleibt einziger Grantstore. `PluginGrantAuthorization.Resolve`
  erhält Source/Board/Rule; `FilterRuleIdentity.ForScope` bildet die kanonische
  JSON-Tupelidentität. Regelparameter bleiben ausschließlich im Core-MediaRuleStore.
- Varianten sind ein eigener Teil der Identität, kein verändertes MediaId.
  Content benutzt Tupel, Manifeste enthalten raw MediaId und Variant, der Reader
  verwendet beide. Bisherige Originalpfade und AssetIDs bleiben erhalten;
  Variantenpfade verwenden `mediaId~SHA256(variant).ext`.
- BaseThread gehört inzwischen dem Adapterowner; Queue/WorkState dem Budgetowner.
  Deren wiederhergestelltes Excluded ist kein Medienfehler. NoMatch wird beim
  expliziten Retry übersprungen; technische Auslassung darf einmal als Reparatur
  nach Quellenende erneut geprüft werden, ohne die ursprüngliche Frist zu ändern.

## Mitgeliefertes Beispiel

`plugins/Examples/ImageDimensionsFilter/ImageDimensionsFilter.cs` ist eine
einzelne editierbare Datei, ausschließlich gegen das öffentliche SDK gebaut.
Plugin-ID: `meowchan.example.image-dimensions`; Filterkey: `dimensions`;
Settingsschema: `1`. Parameter: `minimumWidth`, `minimumHeight`, `orientation`
(`any`, `landscape`, `portrait`, `square`), `aspectRatio` und
`aspectRatioTolerance` (Default `0.01`).

JPEG-/PNG-Maße stammen aus Originalchunks. PNG-IHDR-CRC wird geprüft; es gibt
keinen Bilddecoder und kein Vertrauen in Providermaße. Unlesbare/ungeeignete
Header sind technische Fehler. Das Beispiel benötigt keine nativen Ressourcen.
Vorbau/Editierbarkeit im Releasepaket integriert Root/Activation; Beispieldoku
liegt beim Examplesowner.

Ein separates Testplugin kopiert Chunks in seine Workerarbeitsmappe und startet
einen tatsächlich gebündelten eigenen .NET-Helper. Dessen Hashprüfung, Timeout,
Kindprozessende und Tempgrenze belegen den allgemeinen Ressourcenweg. OCR wurde
nicht implementiert.

## Ausgeführte Prüfungen

Alle dotnet-Läufe unter der globalen Sperre
`/tmp/meowchan-ui-1.0-build.lock`, mit `MSBUILDDISABLENODEREUSE=1` und
`-nodeReuse:false`, Release, isolierten Testprofilen und eigenen Childprozessen.

```sh
dotnet test tests/MeowChan.PluginFilters.Tests/MeowChan.PluginFilters.Tests.csproj \
  -c Release --no-restore -nodeReuse:false
```

**30 bestanden, 0 fehlgeschlagen, 0 übersprungen**, Dauer 12 s, ohne Buildwarnung.
Rohlog: [filter-tests.log](filter-tests.log).

| Tatsächlich geprüfter Pfad | Fälle |
| --- | ---: |
| Echter Worker: ein Originaltransfer, gleiche Bytes/SHA, Store und Offline-Restore | 1 |
| Mindestmaß/Ausrichtung/Seitenverhältnis: NoMatch erhält Capture, speichert kein Original | 3 |
| Widerruf beim Chunkread, Rule-/Watchwechsel und unzulässiger Token/Plugin/Operation/Chunk/Offset | 4 |
| Bekannte Bytegrenze, Text-only-Metadatenentscheidung, Pixelgrenze und beschädigter Header | 2 |
| Bindungswechsel erhält bestehende Datei; Store-IO meldet keinen Erfolg | 2 |
| Eigene Journal-Recovery, FIFO und pending-Journal | 2 |
| Regelparameter-/Scopepersistenz | 1 |
| Echter Ressourcenhelper, Timeout mit eigenen Nachkommen, Workercrash und Tempkopiebudget | 4 |
| Zwei Slots, abbrechbares Warten und normaler Download daneben | 1 |
| Echter Hostprozessabbruch vor Commit sowie nach Zielkopie, Recovery unter neuer Profillease | 2 |
| Fehlgeschlagene oder abgebrochene Auslassungspersistenz gibt Slot frei | 2 |
| Raw-Variantenmanifeste und unabhängiger Restore kollidierender zusammengesetzter Namen | 1 |
| Content/Queue: NoMatch nach Neustart und ausdrücklicher technischer Retry nach Quellenende | 2 |
| Echte JPEGdateien: quer/hoch, Match und NoMatch über Workerchunks | 3 |

Zusätzliche vorhandene Coreprüfungen:

```sh
dotnet test tests/MeowChan.Tests/MeowChan.Tests.csproj -c Release -nodeReuse:false \
  --filter 'FullyQualifiedName~MediaStoreTests|FullyQualifiedName~MediaAssetReaderTests|FullyQualifiedName~MediaRuleStoreTests|FullyQualifiedName~MediaRuleResolverTests|FullyQualifiedName~MediaRuleDecisionTests'
```

**137 bestanden, 0 fehlgeschlagen, 0 übersprungen**, Dauer 3 s.
Rohlog: [core-tests.log](core-tests.log). Der erste Versuch mit `--no-restore`
endete wegen fehlender Assets dieses Testprojekts; der dokumentierte Lauf führte
Restore durch. Ein vorhandener xUnit2031-Hinweis in `BoardAdmissionTests.cs:25`
betrifft eine nicht von dieser Rolle geänderte Assertion.

## Getestete Abhängigkeitsbasis — von den jeweiligen Ownern integrieren

| Owner | Originalcommits | Lokale Entsprechungen |
| --- | --- | --- |
| Runtime | `205c049`, `829d9db`, `4a1b961`, `8612d72` | `0514a92`, `8a67a63`, `7d8b763`, `a735359` |
| Activation | `77179a9`, `5e5b8b1` | `a822e92`, `1d9c13e` |
| Budgets | `a773c43`, `1d24074`, `5f9622b`, `302aa71`, `67acdde`, `2a8f571` | `a2166d7`, `7261a00`, `db15a9c`, `c66c2aa`, `2138640`, `0ac9b15` |
| Adapter | `936960e` | `ca31e27` |

PublicAPI.Unshipped-Übernahmekonflikte wurden durch Vereinigung und Deduplizierung
gelöst. Neuere Abschlusscommits der Nachbarrollen gehören in Roots Integration;
die obige Tabelle beschreibt exakt die hier geprüfte Basis.

## Verbleibende Abnahmegrenzen

- Linux x64 ist nativ getestet. Windows/macOS/ARM-Pakete und die dortige
  Recovery-ABI wurden hier nicht ausgeführt.
- Die 30 Fälle ersetzen keine Desktop-GUI-Prüfung der Regelwahl, Grantdialoge,
  Offlinegalerie oder gesamten R10-08-Kette. Text-only wird in dieser Suite als
  Metadatenentscheidung geprüft; der vollständige UI-Transferpfad ist separat.
- DesktopSession-Binding, Sourceadmission, Regel-UI und Paketlieferung besitzen
  Nachbarrollen/Root. Root integriert die Projekte im zentralen Buildgraph und
  schließt Pluginquellen aus dem Desktop-Compileglob aus.
- Temp-/Tokenkontrollen sind keine OS-Sandbox. Das Beispiel liest Bildheader;
  es behauptet keine vollständige JPEG-/PNG-Decodierbarkeitsprüfung.

