# Budgets / Zielwarten — Implementierungsbericht

Stand: 22.10.2026. Arbeitsbaum `impl/1.0-wave2-budgets`, Basis `dc053f8`.
Nur diese Rolle; kein Plugin-/Desktop-/Release-Gesamt-PASS.

## Eigene Liefercommits

- `a773c43` — öffentliches opt-in HTTP-Deferred und tatsächliche gemeinsame RetryAt-Deadline; 30 HTTPpolicytests bestanden.
- `1d24074` — Metadatenadmission: global vier, pro Quelle eins, Rotation der Quellen und alternierende manuelle/Boardarbeit.
- `5f9622b` — Zielreserve, Zielwarte-/Prüfpfade, Filterqueueanschluss, Excluded-Persistenz und deterministische Fixtures.
- `302aa71` — Filter bei Restore/ausdrücklichem Retry wieder anbinden, Threadstatus für Auslassungen, Root-Volumeprüfung und weiterlaufende terminale 404-Frist.

Zusätzlich ausschließlich als Voraussetzungen übernommen, nicht erneut liefern:
`d01a843` (hier `b9838d5`), `fb5d7d4` (hier `ea7be96`), `a183f39` (hier `ed8a3a3`), alle vom Filterowner.
Für `5f9622b` zuerst `d01a843`; für `302aa71` außerdem `fb5d7d4` und `a183f39` übernehmen.
PublicAPI.Unshipped-Appendkonflikte wurden als Union aufgelöst.

## Verhalten

- `RequestPolicyHandler.DeferWhenWaitingKey` und `RequestPolicyDeferredException.RetryAt/ReasonCode` liefern Admission/Cooldown ohne HTTP-Warteschleife im Plugin. Die vorhandene Policy und ihre Deadline bleiben maßgeblich. ObserverKey ist öffentlich; Observerfehler werden weiterhin nur protokolliert.
- `DownloadQueue.AcquireMetadataAsync` liefert ein freizugebendes Lease. Vier aktive Slots, einer je Quellen-ID, maximal 256 registrierte wartende Aufrufe. Zusätzliche Watchaufrufe warten nur an einem gemeinsamen Kapazitätssignal, ohne zweite interne Auftragsliste. Registrierte Arbeit rotiert nach Quelle und manueller/Boardpriorität. Pause sperrt neue Leasezuteilung, Stop/Cancellation beendet Wartende. Der Adapterowner legt die tatsächlichen IPC-Aufrufe in diese Leases und gibt sie vor Deferred-/Medienwartezeiten frei.
- Zielreserve standardmäßig 1 GiB, über `UpdateMinimumFreeSpaceBytes` änderbar. Produktionsadmission über `EnqueueAsync` prüft vor der Medienqueue; jeder Worker prüft erneut vor dem Transfer. Die kompatiblen synchronen TryEnqueue/EnqueueDownload-Einstiege prüfen vor dem Transfer.
- Disk-full und relevante Laufwerks-/NAS-IOfehler blockieren das Ziel, ohne Netzwerkversuche, 404-Zähler oder DueAt umzuschreiben. Andere Ziele können weiterlaufen. Die echte Dateisystemprüfung nutzt Laufwerksbereitschaft und verfügbaren Platz einschließlich bekannter Dateigröße; unbekannte Größe bleibt eine Vorsorgeprüfung.
- Gemeinsame Zielprüfung nach 60 Sekunden und ausdrücklich über `CheckTargetsAsync`. `TargetWaitStates` liefert Pfad, Reasoncode und nächste Prüfung. Prüfungen laufen außerhalb UI-/Queuelocks, physisch höchstens vier gleichzeitig, Statuscache maximal 1024 Ziele mit Eviction gesunder Einträge. Während der Session erkannte Mountwechsel blockieren, damit ein verschwundener Mount nicht still auf das Elternfilesystem ausweicht.
- Unterbrochene/Zielblockierte Medienreservierungen bleiben innerhalb der Gesamtgrenze Pending + aktive Kapazität. Blockierte Ziele/Filter werden beim Dispatch übersprungen. Filter reservieren höchstens zwei Transfers, bei Parallelität > 1 bleibt mindestens ein Platz für normale Medien frei. CapacityChanged weckt die Queue.
- NoMatch wird Excluded, technische Filterauslassung NeedsIntervention; beide sind kein Download-Erfolg und erzeugen keine automatische Retryschleife. NoMatch wird durch pauschalen Medienretry nicht neu geplant. Technischer ausdrücklicher Retry bindet die aktuelle Filterentscheidung erneut an.
- Terminale Medien-404 enden weiterhin exakt zwölf Stunden nach dem ersten Quellenende, auch wenn das Ziel in dieser Zeit blockiert bleibt. Restart plant aus dem Workjournal neu; Quelle/Endzeit/Generation werden nicht zurückgesetzt.

## Ausgeführte Prüfung

Alle dotnet-Aufrufe seriell unter:

```sh
flock --close /tmp/meowchan-ui-1.0-build.lock env MSBUILDDISABLENODEREUSE=1 \
  dotnet test tests/MeowChan.Tests/MeowChan.Tests.csproj -c Release -nodeReuse:false --verbosity quiet
```

Finaler vollständiger Coretestlauf auf Codebasis von `302aa71`: **666 bestanden, 0 fehlgeschlagen, 0 übersprungen**, Dauer 5 s. Ein bestehender xUnit2031-Hinweis in BoardAdmissionTests, keine Buildfehler. Vorher gezielte Läufe (HTTP30, Metadata3, Queue/Target/Workstate/HTTP108; nach Anschlüssen81) bestanden. `git diff --check` sauber.

Neue Belege umfassen:

- tatsächliche dreistündige Retry-After-Deadline ohne Clockadvance/zusätzlichen Transport; Pacingdeferred ohne zurückgelassenen FIFOplatz;
- 500 parallele gemischte Watches vollständig abgearbeitet innerhalb global4/proQuelle1; 500 wartende Aufrufe mit exakt256 registrierten Waitern vollständig abbrechbar; manuelle/Board-Fairness, Pause und Stop;
- simuliertes volles/offline Ziel mit echten Medienbytes in unabhängigem gesundem Ziel, danach explizite Wiederaufnahme und identische Bytes;
- kontrollierte Uhr: keine Prüfung nach59s, Wiederprüfung nach60s; tatsächlicher Transfer bezahlt separat seinen Bandbreitentimer;
- echte DriveInfo-Platzprüfung und Reserve-/Dateigrößengrenzen;
- realer MediaStore-Publikationspfad mit kontrolliertem Disk-full-IOException bei File.Move, unverändertes SQLite-Journal, neue Queue nach Neustart, genau eine veröffentlichte Mediendatei;
- weiterhin wirksame terminale 404-Endfrist trotz offline Ziel, ohne neuen HTTPabruf;
- Filterkapazität blockiert normale Medien nicht, NoMatch/technische Auslassung werden im Journal unterschiedlich und ohne Erfolg gezählt.

## Anschlüsse und Grenzen

- Adapterowner: echte Catalog-/Threadaufrufe, DesktopSessionHost/Configanschluss, Registry-Abschluss für Excluded und Reihenfolge ContentBind vor QueueBind. Updatesowner: Downloads-Reservefeld. Managementowner: lokalisierte Diagnose/Details und Jetzt prüfen. Diese UI-/Hostanschlüsse sind nicht durch den Coretestlauf abgenommen.
- Runtimeowner: Prozesszahl/Heap/Leerlauf/Restart/Kindprozesse. Kein Anspruch dieses Berichts auf deren Prüfung.
- Ein OS-Dateisystemaufruf ist selbst nicht kooperativ abbrechbar. Abbruch beendet die wartenden Medien-/Admissionpfade; maximal vier bereits im Betriebssystem hängende Prüfungen können bis zu dessen Rückkehr weiterbestehen. Es werden keine Ersatzprozesse oder unbeschränkten Ersatzprüfungen gestartet.
- Volumezuordnung ist ein Sessioncache. Ein schon vor dem ersten Zugriff nach Neustart nicht eingehängter Unix-Mount ohne im Dateisystem erkennbaren Fehlzustand ist nicht von einem absichtlich neuen lokalen Zielordner unterscheidbar. Persistentes Mountidentitätsjournal gehört noch nicht zum bisherigen Workstorevertrag.
- Fairness ist für registrierte Metadatenaufrufe und die Quellen-/Prioritätsrotation garantiert; Überlaufcaller warten am gemeinsamen Kapazitätssignal. Die 500-Watch-Abnahme belegt Fortschritt und Grenzen, keine FIFO-Zusage für beliebig viele gleichzeitig neu eintreffende Überlaufcaller.
- Ein vom Filterowner gemeldeter Folgefix muss RestoreDurableMedia daran hindern, Excluded/NoMatch als technische MediaFailure wiederherzustellen; an ihn und Root gemeldet.

## Folgeauftrag: stabile Medienvarianten (22.10.2026)

Eigene weitere Liefercommits: `67acdde` und `2a8f571`.
Erforderliche fremde APIvoraussetzungen: Filter `e5706cf` (hier `48cc33f`), Adapter `936960e` (hier `21c8424`).

`MediaWork.Variant` ist ein eigener Identitätsteil mit Default `original`. SQLite führt eine separate Variantspalte im Primärschlüssel; die alte Tabelle wird in einer einzigen Transaktion migriert. Der alte JSONpayload bleibt dabei bytegleich, ebenso Original-IDs, Pfade, Fälligkeiten und erste Endzeit. Zweifaches Öffnen ist idempotent. Weder Prefixreservierung noch Flattening ändern gespeicherte Quellidentitäten.

Queue-Jobidentität, sämtliche Journal-/Schedule-/Revisionslookups und RestoreItem führen raw MediaId und Variant getrennt. Benachrichtigungen gehen an die variantenfähigen BaseThread-Overloads. Aus `(x--preview, original)` und `(x, preview)` werden zwei getrennte Jobs und Erfolge; dieselbe raw ID mit anderer Variante ist auch bei identischem angefordertem Ziel ein anderer Queue-/Journalkey. Die UI-/Host-/Content-/Manifestweiterleitung besitzt weiterhin Adapter bzw. Filter.

Der parallel gemeldete RequestMediaRetry-Race ist korrigiert: Auch der innere atomare Recheck schließt Excluded aus. Eine kontrollierte Storefixture ersetzt den zunächst gelesenen technischen Fehler vor dem zweiten Read durch NoMatch und belegt, dass kein Ready-/Retryjob entsteht.

Zusätzliche Nachweise:

- echte alte SQLite-Tabellendefinition ohne Variantspalte, unveränderter Legacy-JSONpayload nach Migration, zweimaliges Öffnen und unveränderte Retry-/Endzeiten;
- unabhängige rawId-/Variantkeyteile und Erkennung eines manipulierten Variantpayloads;
- zwei ehemalige Flattening-Kollisionspaare durch echte DownloadWorker-/Dateibytes und Workjournal, neue Queue nach Neustart, zwei korrekte Completion-Identitäten ohne weitere HTTPrequests oder Dateien;
- gleiche raw ID und gleiches Ziel bei zwei Varianten bleiben getrennte Reservierungen/NoMatch-Journaleinträge;
- unveränderte Original-Queue-/Lifecyclefälle.

Gezielt **82/82 PASS**. Ein erster vollständiger Lauf zeigte **671/672** mit dem vorhandenen 32-Produzenten-Test als Fehler; Einzelrepro und vollständige Wiederholung bestanden. Dessen erster HTTP-Startanker synchronisierte die nun asynchrone Dateisystemprüfung vor der nächsten Producer-Admisson nicht. Der Test wartet jetzt ausdrücklich auf dieselbe Pending==8-Bedingung; keine Assertion oder Frist wurde gelockert. Finaler vollständiger Lauf nach dieser Synchronisation: **672/672 PASS**, 4 s, TRX unter `/tmp/meowchan-budgets-variant-tests/variant-core-synchronized.trx`.

Der neue DownloadItem-Overload meldete beim vollständigen Core-Neubau RS0027; dies ist an seinen Filterowner zur lokalen, begründeten APIbehandlung übergeben. Bestehendes xUnit2031 bleibt. Der zuvor genannte NoMatch-RestoreFailure ist in Adaptervoraussetzung `936960e` bereits ausgeschlossen.
