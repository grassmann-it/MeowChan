# Wave 8 — StartupPreviewStore / Special-file- und Responsiveness-Review (Sol)

## Scope und Vorgehen

Geprüft wurden ausschließlich:

- `Desktop/StartupPreviewStore.cs`
- `tests/MeowChan.DesktopTests/StartupPreviewSpecialFileRegressionTests.cs`
- `tests/MeowChan.DesktopTests/StartupPreviewResponsivenessRegressionTests.cs`
- die unmittelbar sichtbare Host-/UI-Anbindung (`LoadStartupPreviewAsync` und
  `MainWindow.StartupPreview.cs`), soweit dies für den Vertrag erforderlich ist.

Kein Code, kein Test, kein `RUNSTATE` und kein anderer Projektstand wurde geändert.
Es wurden keine Builds, Tests oder Commits ausgeführt.

## Befunde

### P1 — `PlatformNotSupportedException` kann die optionale Vorschau zur Startup-Abhängigkeit machen

`RegularProfileFile.OpenRead` wirft auf Unix-Plattformen außerhalb von Linux/macOS
mit x64/arm64 `PlatformNotSupportedException`. Das ist ausdrücklich der Fall in
`libmeowchan/Persistence/RegularProfileFile.cs`. `LoadStartupPreviewAsync` fängt
aber nur `InvalidDataException`, `IOException`, `UnauthorizedAccessException`,
`JsonException` und `ArgumentException`.

Damit kann ein fehlender/ungültiger **Displaycache** auf einer ansonsten vom
Desktop unterstützten Unix-Architektur aus dem optionalen Preview-Lesen heraus
bis `StartCoreAsync` propagieren und `RecoveryFailed` auslösen. Das widerspricht
dem Startup-Vertrag, nach dem Cache-Miss, Defekt oder nicht unterstützte
Cache-Inspektion die normale autoritative Wiederherstellung nicht verhindern darf.
Die vorhandenen Special-file-Tests helfen hier nicht: `SupportedUnix` überspringt
andere Architekturen.

**Minimaler Fix:** `PlatformNotSupportedException` in der eng begrenzten
Catch-Klausel von `LoadStartupPreviewAsync` als „Preview unavailable“ behandeln.
Alternativ muss `RegularProfileFile` einen nachweislich sicheren Fallback für jede
unterstützte Desktop-Plattform anbieten; ein blindes Zurückfallen auf den normalen
Pfad-`FileStream` würde den FIFO-Schutz wieder aufgeben und ist daher nicht die
kleinere/sichere Reparatur.

### P2 — Windows-Special-file-Verhalten ist implementiert, aber nicht belegt

Der Store verwendet nun `RegularProfileFile.OpenRead`; dessen Windows-Pfad prüft
Handle-Typ, Reparse-Point, Directory und Device. Die Regressionstests decken
jedoch nur Unix-FIFOs ab und geben auf Windows vollständig `return` zurück. Die
allgemeinen Storetests überspringen zusätzlich die Symlinkfälle auf Windows.

Das ist kein nachgewiesener Produktfehler, aber eine echte Abnahme-/Regression-
Lücke: Named pipe, reparse leaf und directory können auf Windows anders durch
`CreateFileW`/`GetFileInformationByHandleEx` laufen als die Unix-Fälle. Der
Vertrag „Special file darf Startup nicht blockieren und darf nicht als Cache
akzeptiert werden“ ist damit nur für den getesteten Unix-Pfad belegt.

**Minimaler Fix:** eine Windows-spezifische Regression für einen lokalen Named
Pipe-Server (ohne wartenden Peer beim Store-Lesen) und für Directory/reparse
leaf ergänzen, oder den Windows-Handlecheck über eine kleine testbare
Plattformnaht mit deterministischen Ergebnissen prüfen. Die Tests sollten dabei
auch den anschließenden normalen Restore und die Lease-Reacquire prüfen.

### P2 — Der Responsiveness-Test prüft keine UI-/Dispatcher-Responsiveness

`StartupPreviewResponsivenessRegressionTests` hält die autoritative
`SessionStore.LoadAsync`-Wiederherstellung an und prüft `GetView`, Commands,
Cachebytes und Provideraktivierung aus `Task.Run`. Das ist ein sinnvoller Host-
Passivitäts-/Admissiontest, aber kein Nachweis dafür, dass das Avalonia-Fenster
oder der UI-Dispatcher während einer echten Wiederherstellung responsive bleibt.
Der Test selbst dokumentiert diese Grenze. Insbesondere werden weder ein echter
Dispatcher-Aufruf noch die Zeit bis zur Anzeige des Panels noch ein langsames
reguläres Cache-Read auf einer problematischen Storage-Schicht gemessen.

Die aktuelle Store-Leseoperation ist synchron und darf bis zu 1 MiB aus einer
regulären Datei konsumieren. FIFO wird verhindert, aber langsames Filesystem,
Netzlaufwerk oder I/O-Stall ist nicht durch diesen Test begrenzt.

**Minimaler Fix:** den bestehenden Hosttest als Passivitätsregression benennen/
behalten und einen separaten UI-Smoke-/Timingtest ergänzen: Fenster auf dem
Dispatcher öffnen, Preview-Panel vor dem Restore beobachten und während eines
gehaltenen Restores einen Dispatcher-gebundenen Probe-Callback ausführen. Für
Storage-Latenz entweder eine explizite, testbare Store-Read-Naht mit Timeout-
Vertrag schaffen oder die fehlende Messung klar als Nichtziel dokumentieren;
keinen beliebigen `Task.Run`-Watchdog als UI-Beleg werten.

### P2 — Timeout-Pfad des Responsiveness-Tests kann eine orphaned `Task.Run` hinterlassen

`AssertPassiveReadsCompleteAsync` startet die Prüfungen in `Task.Run` und wartet
mit `WaitAsync(Deadline)`. Bei einem Timeout wird die laufende Prüf-Task nicht
abgebrochen und nicht mehr awaited. Danach lösen die `finally`-Blöcke die
Preview-/Store-Gates, und `Fixture.DisposeAsync` kann Host, `HttpClient` und das
Profilverzeichnis freigeben, während die alte Prüf-Task noch
`host.GetView()`, `fixture.AssertNoCacheActivation()` oder Dateilesen ausführt.

Das kann bei einem echten Regressionstreffer zu Folgefehlern, Profil-/Dateiraces
oder flakey Tests führen und den ursprünglichen Timeout verschleiern. Der
`Task.Run`-Body ist synchron und hat keinen CancellationToken, daher lässt er
sich durch `WaitAsync` nicht stoppen.

**Minimaler Fix:** die Prüf-Task in der Testmethode behalten und im Fehler-/finally-
Pfad zuerst alle Gates freigeben, dann dieselbe Task mit einer zweiten begrenzten
Drain-Frist abwarten, bevor Fixture/Profil entsorgt werden. Noch besser ist eine
cancellable Testnaht für potenziell blockierende Reads. Wenn der Drain selbst
fehlschlägt, sollte der Test den ursprünglichen Timeout plus den Cleanup-Fehler
sichtbar ausgeben, statt nur den späteren Disposefehler.

### P3 — Synchroner Special-file-Watchdog ist absichtlich test-only, aber sein Rescue-Pfad bleibt plattformeng

`Guard` rettet einen hängenden Unix-FIFO-Reader nach fünf Sekunden über einen
nonblocking `open` auf `O_WRONLY`. Das ist für den isolierten Unix-Fall plausibel
und verhindert, dass ein alter fehlerhafter Build den Testprozess dauerhaft
blockiert. Es ist jedoch kein allgemeiner Abbruchmechanismus für jeden möglichen
Unix-Special-File- oder libc-Zustand; bei einem Fehler zwischen Reader-Open und
Rescue kann die Operation bis zur zweiten Frist weiterleben.

**Minimaler Fix:** den Rescue-/Drainpfad als Unix-FIFO-spezifischen Testmechanismus
klar benennen und bei Timeout immer den Task-Status/Exception zusammen mit dem
ursprünglichen Watchdogfehler ausgeben. Keine Produktionslogik daraus ableiten.

## Positiv bestätigt

- `StartupPreviewStore` kopiert und validiert Snapshots vor dem Encoding und
  liefert read-only, neu materialisierte Row-Listen zurück.
- Schema-, Feld-, Größen-, Zähler-, UTC-, Kontrollzeichen- und Byte-Limits sind
  fail-closed; ein fehlgeschlagener Save ersetzt die bestehende Cachedatei nicht.
- `RegularProfileFile` validiert auf Unix nach dem `open` nochmals den realen
  Descriptor (`fstat`) und öffnet mit Nonblocking/No-follow-Flags; das schließt
  den offensichtlichen FIFO-Block beim Store-Read.
- Der Previewkanal wird nicht in die autoritative Projektion oder die Handlemaps
  übernommen. Die UI retire’t die historische Anzeige nach dem ersten
  autoritativen Ready-Frame; ein interner retained Snapshot ist daher nicht
  automatisch eine sichtbare Cache-Wiederbelebung.
- Die Responsiveness-Regressionen prüfen sinnvoll, dass cache-only rows während
  gehaltenem Restore keine Providerpolls, HTTP-Requests, Medienbytes oder
  interaktiven Commands aktivieren und dass ein bereits akzeptierter Restore
  beim Stop nicht nachträglich `Ready` publiziert.

## Gesamturteil

Der Unix-FIFO-Fix und die Host-Passivitätsregression sind im engen Scope
plausibel. **Vor einer Startup-Preview-Abnahme muss mindestens der
`PlatformNotSupportedException`-Pfad geschlossen oder die Plattform explizit aus
Desktop-Support ausgeschlossen werden.** Windows-Special-files und echte
Dispatcher-Responsiveness bleiben anschließend getrennte Evidenzlücken. Der
Timeout-Drain der Responsiveness-Tests sollte vor weiterer Nutzung als harte
Regression-Gates repariert werden, damit Fehler nicht durch nachlaufende Tasks
maskiert werden.
