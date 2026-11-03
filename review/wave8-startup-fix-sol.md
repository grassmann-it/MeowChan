# Wave 8 — StartupPreview-Fix (Sol)

## Abschlussstand und Scope

Der aktuelle Fixstand wurde gegen die Live-Dateien dieses Arbeitsstands
abgeglichen. Der Scope bleibt exakt auf diese fünf Dateien begrenzt:

- `Desktop/DesktopSessionHost.StartupPreview.cs`
- `tests/MeowChan.DesktopTests/StartupPreviewResponsivenessRegressionTests.cs`
- `tests/MeowChan.DesktopTests/StartupPreviewSpecialFileRegressionTests.cs`
- `tests/MeowChan.DesktopTests/StartupPreviewUnsupportedPlatformTests.cs`
- `review/wave8-startup-fix-sol.md`

Es wurden für die Finalisierung keine weiteren Scope-Erweiterungen vorgenommen.
`RUNSTATE`, Architektur-Snapshots, Produktions-Storecode außerhalb des Hosts und
UI-/Native-Abnahmepfade bleiben unverändert.

## Findings gegen Live-Dateien

### P1 — `PlatformNotSupportedException` durfte den optionalen Preview-Read nicht zum Startup-Gate machen

**Ursprünglicher Befund:** `RegularProfileFile.OpenRead` kann bei nicht
unterstützten Plattform-/Architekturvarianten `PlatformNotSupportedException`
werfen. Der Preview-Read ist aber nur eine disposable Displayprojektion und darf
laut Startup-Vertrag den autoritativen `SessionStore`-Restore nicht verhindern.

**Aktueller Fix:**

- `Desktop/DesktopSessionHost.StartupPreview.cs:19–21` enthält die ausdrücklich
  test-only markierte `StartupPreviewReadForTests`-Naht.
- `:23–50` verwendet im Produktionspfad weiterhin
  `StartupPreviewStore.Load(...)`; die Naht ersetzt nur den optionalen Reader in
  Tests.
- `:42` ergänzt ausschließlich `PlatformNotSupportedException` in der bereits
  begrenzten „Preview unavailable“-Filterung.
- Die Cancellation-Prüfung bei `:34` sowie die Prüfungen bei `:48` und `:50`
  bleiben wirksam. `OperationCanceledException` wird nicht als Preview-Miss
  verschluckt; ein unerwartetes `InvalidOperationException` bleibt ebenfalls
  ein echter Startupfehler.
- Der Previewwert kann weder Session-/Registrydaten noch Watch-Handles oder
  Provideraktivierung speisen.

**Konkrete Regressionen in `StartupPreviewUnsupportedPlatformTests.cs`:**

- `UnsupportedPreviewDoesNotActivateCacheOrBypassOrdinaryRestore(bool
  failRestore)` (`:23–106`, zwei Fälle): synthetischer Plattformfehler; der echte
  Restore wird betreten. Der erfolgreiche Fall endet mit der autoritativen
  Current-URL und dem autoritativen Titel in `Ready`; der Fehlerfall bleibt
  `RecoveryFailed`, publiziert weder `Ready` noch einen Fake-Checkpoint. Beide
  Fälle prüfen Cache-/Session-Bytes, keine Preview-Benachrichtigung, keine
  Cache-Providerpolls und Lease-Reacquire.
- `StopDuringPreviewReadPreservesCancellationAndNeverEntersRestore(bool
  platformFailure)` (`:108–175`, zwei Fälle): der synchron gehaltene optionale
  Read sieht entweder die echte Cancellation oder liefert danach einen späten
  Plattformfehler. In beiden Reihenfolgen wird der autoritative Restore nicht
  betreten; `Ready`/`RecoveryFailed` werden nicht nachträglich publiziert und
  Cache/Session bleiben unverändert.
- `DisabledRetentionDoesNotInvokePreviewReaderOrAuthoritativeRestore()`
  (`:177–201`): bei `SaveUrlsOnExit=false` werden weder der Preview-Reader noch
  der autoritative Session-Read aufgerufen.
- `UnexpectedReaderFailureIsNotHiddenByOptionalPlatformCatch()` (`:203–222`):
  ein `InvalidOperationException` bleibt `RecoveryFailed` und wird nicht durch
  den neuen Plattformcatch zu einem harmlosen Cache-Miss.

Die vier neuen Testmethoden ergeben sechs Fälle (2 + 2 + 1 + 1). Die
Plattformfehler werden über die interne Testnaht synthetisiert; das ist bewusst
kein Beleg für native Plattformunterstützung oder für einen echten
Avalonia-/Dispatcherlauf.

### P2 — Orphaned `Task.Run` im Responsiveness-Test

**Ursprünglicher Befund:** `WaitAsync(Deadline)` beendet nur das Warten, nicht
die synchronen View-/Passivitätsabfragen in `Task.Run`. Bei einem Timeout hätte
das Fixture danach Gates, Host, HTTP-Client oder Profilverzeichnis entsorgen
können, während die Probe noch lief.

**Aktueller Fix:**

- `StartupPreviewResponsivenessRegressionTests.cs:199–300` hält die echte Probe
  als lokale `Task probe` (`:204`).
- Im Fehlerpfad von `:256–268` werden zuerst `ReleasePreview` und
  `Store.Release` freigegeben (`:264–265`), danach wird genau diese Probe
  beobachtet und awaited (`:266`). Erst anschließend wird der ursprüngliche
  Timeout bzw. die ursprüngliche Assertion erneut geworfen (`:268`).
- Die Erfolgsdeadline bleibt unverändert bei 15 Sekunden. Assertions und
  Terminal-Identitätsregeln wurden nicht abgeschwächt. Es wird nicht unter einer
  noch laufenden Probe entsorgt.
- Der Drain ist absichtlich unbeschränkt: ein unabhängiger Deadlock, der durch
  die beiden Fixture-Gates nicht gelöst wird, kann weiterhin hängen. Es wurde
  keine neue Erfolgsfrist und kein „grüner“ Fallback eingeführt.

Die bestehende Abdeckung bleibt konkret:

- `HeldAuthoritativeRestoreKeepsPreviewPassiveAndCheckpointsOnlyTheRealResult`
  (`:30–104`, zwei `restoreWatches`-Fälle) prüft die passive Cacheanzeige
  während des gehaltenen echten Restores und die anschließende autoritative
  Projektion.
- `EnteredRestoreFailureOrCancellationPreservesCacheAndNeverPublishesReady`
  (`:122–197`, drei `(stopDuringLoad, failLoad)`-Fälle) prüft Fehler, Stop
  während eines bereits betretenen Restores und späten Ladefehler nach Stop.

Es gibt keinen zusätzlichen deterministischen Test, der den neuen Timeout-/Drain-
Pfad absichtlich auslöst. Daher wird nur die Race-/Cleanup-Korrektur aus der
Quellinspektion dokumentiert, nicht deren Laufzeitabnahme behauptet.

### Trivialer Nullable-Befund im Special-file-Test

`StartupPreviewSpecialFileRegressionTests.cs:28` verwendet jetzt explizit
`Task.Run<Exception?>`, passend zum `Record.Exception`-Ergebnis. Damit ist die
nullable Testgrenze eindeutig; die Testaussage ändert sich nicht.

Der Unix-Nachweis bleibt konkret:

- `UnixFifoIsRejectedWithoutPeerAndRegularAndMissingControlsStillWork()`
  (`:16–35`) prüft Missing-/Regular-Cache und die Ablehnung eines FIFO ohne
  Peer; anschließend bleibt eine normale Cachedatei wieder lesbar.
- `UnixFifoCacheFallsBackToRealRestoreThenStopsAndReleasesProfile()` (`:37–62`)
  prüft beim FIFO den normalen autoritativen Restore, `Ready`, keine Preview-
  Aktivierung und die anschließende Lease-Reacquire.

## Bewusst offene Evidence-Grenzen

Diese Punkte stammen aus dem ursprünglichen Review und wurden nicht als neue
Arbeitspakete in diesen Fix aufgenommen:

- **Windows-Special-files:** Die Special-file-Tests sind weiterhin Unix-FIFO-
  spezifisch und überspringen auf Windows. Named Pipes, Reparse-Points,
  Directories und Windows-Handlepfade sind deshalb nicht nativ belegt.
- **Echte UI-/Dispatcher-Responsiveness:** Die Responsiveness-Tests sind
  Host-/Admission-/Passivitätstests. Sie beweisen weder ein echtes Avalonia-
  Fenster noch einen Dispatcher-Callback, Panel-Anzeigezeit oder langsames
  Filesystem-I/O.
- **FIFO-Rescuepfad:** Der Rescue-/Drainmechanismus bleibt ein
  Unix-FIFO-spezifischer Testmechanismus und ist kein allgemeiner Abbruch für
  jeden Special-file- oder libc-Zustand.
- **Native Plattformabnahme:** Der neue Unsupported-Platform-Test nutzt eine
  synthetische Testnaht. Er validiert Hostfehlerbehandlung und Autoritätsgrenzen,
  nicht die Verfügbarkeit von `RegularProfileFile` auf einer konkreten
  Windows-, macOS- oder sonstigen Architektur.

## Validierungsstand

Die Live-Dateien wurden nur quellenbasiert gelesen und auf die oben genannten
Zeilen, Methoden und Assertions abgeglichen. Builds und Tests wurden in diesem
Schritt nicht ausgeführt; deshalb gibt es keinen Compile-, Laufzeit-, Native-
oder UI-PASS. Insbesondere wurden die sechs neuen Unsupported-Platform-Fälle,
der geänderte Responsiveness-Test und die Special-file-Tests nicht ausgeführt.

Historischer Hinweis: Bei der früheren Quellinspektion wurde entgegen der
Vorgabe versehentlich einmal ein rein lesendes `git diff -- <scope files>`
aufgerufen. Es gab dabei keine Git-Mutation; für diese Finalisierung wurden
keine Git-Befehle verwendet.
