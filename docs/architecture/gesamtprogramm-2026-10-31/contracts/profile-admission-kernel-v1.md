# P08b — leaseeigener In-memory-Admissionkern v1

Stand 02.11.2026; technischer Umsetzungsschnitt innerhalb GP12/13, keine neue
Fachwahl und keine produktive Import-/Applyfreigabe. P08a prüft unabhängig davon
reale Storebytes; dieser Kern koordiniert ausschließlich freiwillig teilnehmende
In-process-Operationen.

## Zustände und Ownership

Jede erfolgreiche `ProfileLease.Acquire` erzeugt genau eine interne `Admission`.
Unabhängige Profile und jede erneute Akquisition erhalten eigene Instanzen. Kein
statischer/globaler Koordinator, keine öffentliche APIerweiterung, keine durable
Profilinkarnation. `Starting` ist der Initialzustand; hostlos bedeutet nicht Ready.

- `Starting`: nur Startupwriter; `CompleteStartup` erst nach deren vollständigem
  Release. `FailStartup` führt nach `RecoveryFailed`, ohne aktive Tokens zu entziehen.
- `Ready`: Ordinarywriter oder genau eine wartende/aktive Exclusive-Reservation.
  Wartendes Exclusive verhindert neue Ordinarywriter, lässt akzeptierte auslaufen.
- `RecoveryFailed`: keine neuen Writer oder Rückkehr nach Ready.
- `Stopping`: neue Writer abgewiesen, akzeptierte Tokens bleiben gezählt.
- `Stopped`: nur nach expliziter Shutdowncompletion ohne aktive Tokens.

Tokens sind instanzgebunden und idempotent zu disposen. Nur exakt ausgegebene,
aktive Referenzen zählen; fremde oder nur allozierte Tokens lösen nichts aus.

## Cancellation und Konkurrenz

Cancellation vor Annahme erzeugt kein Token. Ein wartendes Exclusive wird vor
Grant entfernt; danach dürfen wieder Ordinarywriter eintreten. Cancellation nach
Grant widerruft kein akzeptiertes Token. Letzter Writerrelease, Cancel und Shutdown
werden unter demselben internen Monitor linearisiert. Taskcompletions laufen nach
Verlassen dieses Monitors mit `RunContinuationsAsynchronously`; kein Await darunter.

**Consumerpflicht:** jeden zurückgegebenen Exclusive-`ValueTask` genau einmal
beobachten und jedes gewährte Token auch bei Fehler/Shutdown disposen. Aufgeben
nur über den mitgegebenen CancellationToken plus Beobachtung der Completion.
Wegwerfen eines nicht cancellierten Waiters bricht die Reservation nicht ab.
Das ist ein interner Acquirevertrag, kein automatisch erkennbares Verlassen.

## Shutdown und Lease-Dispose

`BeginShutdown` sperrt neue Eintritte und weist einen noch wartenden Exclusive ab.
`DrainAsync` wartet separat auf akzeptierte Tokens; Cancellation betrifft nur den
jeweiligen Drainbeobachter. `CompleteShutdown` verlangt leere aktive Menge.

`ProfileLease.Dispose` ruft synchron `Admission.Close()` **vor** Erwerb seines
Lease-Monitors und vor Freigabe des OSlocks auf. Es wartet selbst nicht auf Drain.
Dies ist absichtlich keine Behauptung, dass damit bereits alle aktuellen Writer
oder akzeptierte Tokens vor Leasefreigabe beendet würden. Der spätere integrierte
Hostowner muss seal → Producer stoppen → drain → Owner schließen → Lease freigeben
ordnen. Bis dahin ist der Kern nicht die Importautorität.

## Explizite Schnittgrenzen

Noch keine Consumerverdrahtung für Store-, SQL-, Catalog-, Registry-, Queue-,
Media-, Cleanup-, Startup- oder Hostwriter. Kein Apply, Consent, Konfliktworkflow,
Markerretirement oder Desktopimport. Keine ACID-/Rollback-/hostile-filesystem- oder
native Windows/macOS-Abnahme aus diesen In-memory-Tests ableiten.

## Prüfung

Fokussierte Zustands-/Lifetime-/Leasefälle sowie begrenzte echte Konkurrenzfälle
für Cancel/Grant/Shutdown, Doppelrelease und konkurrierende Exclusives. Belege
werden auf festem Source-Freeze seriell im isolierten Runner erzeugt; Rohlogs/TRX,
DLLbindung und unabhängiger Astrareview bleiben von diesem Vertrag getrennt.
