# Wave 7 — WorkState Core Fix Review (Astra)

**Scope:** aktuelles Root-W6-02-Delta in `WorkStateAccess.cs` und
`WorkStateStore.cs`: `TryCommitMediaCore`, `CommitIntentCore`, `CommitStateCore`
und W6-01-Ende. Read-only Review, Stand 2026-11-03. Keine Laufzeitprüfung,
kein Build/Test/State/Git.

## Urteil

Der aktuelle W6-02-Schnitt behebt strukturell den zuvor bekannten nested-before-
lease/use-pin-Race: Die WorkStateAccess-SQL-Pfade betreten genau einmal den
äußeren `RunAdmitted`-Use und rufen danach die Core-Transaktion direkt auf. Die
Cores versuchen nicht erneut, den bereits akzeptierten Admission-Token zu
verwenden. Damit bleibt der Token auch dann gültig, wenn zwischen äußerer
Akzeptanz und `RunExclusive` `Dispose`/Release angefordert wird.

**Wichtig:** Das ist eine Quell-/Pfadanalyse, kein Laufzeitbeleg. Der echte alte
W6-01-negative Befund bleibt gültig. Für den alten W6-02-nested-before-lease-Race
existiert weiterhin **kein negativer Runtimebeleg**; aus dem aktuellen Code darf
kein „reproduziert“ oder „grün verifiziert“ abgeleitet werden.

## Geprüfte Pfade und Lock-/Pin-Verhalten

### `TryCommitMedia`

`WorkStateAccess.TryCommitMedia`:

- bei SQLite mit vorhandenem Token: `sqlite.RunAdmitted(admission, () =>
  TryCommitMediaCore(...))`;
- ohne Token: genau ein `EnterStoreWriter`, danach dasselbe `RunAdmitted`;
- `TryCommitMediaCore` öffnet nur noch `sqlite.TryCommitMediaCore(next,
  expected)` bzw. bei Fremdstores die dafür vorgesehene API;
- `WorkStateStore.TryCommitMediaCore` führt Validierung, Owner-/Generation-/
  Suppressed-Prüfung, SQL-Read/Write und `transaction.Commit()` innerhalb eines
  `_lease.RunExclusive`-Callbacks aus.

Der live OS-Lease-Lock bleibt dadurch bis zum Ende des SQLite-Callbacks gehalten;
der äußere Admission-Use-Pin bleibt bis zur Rückkehr aus `RunAdmitted` aktiv.
Die vollständige erwartete Media-Zeile wird innerhalb derselben SQL-Transaktion
verglichen. Ein Medien-Key-Wechsel bei `expected != null` wird vor der Mutation
abgewiesen.

### `CommitIntentCore`

`CommitIntentUnderFence` nimmt bei SQLite den äußeren `RunAdmitted`-Pfad und
ruft `CommitIntentCore` direkt auf. Der Core validiert, öffnet die Verbindung,
liest den bisherigen Owner, erzwingt die Generationssteigerung und schreibt/
committet innerhalb von `_lease.RunExclusive`.

Die öffentliche `CommitIntent`-API nimmt dagegen selbst einen neuen
`EnterStoreWriter`-Token und delegiert an `CommitIntentAdmitted`; diese API ist
nicht auf den borrowed-token-Core umgebogen und bleibt damit eine eigenständige
Direct-Admission.

### `CommitStateCore`

Der Pfad ist analog: outer `RunAdmitted` → `CommitStateCore` → ein
`_lease.RunExclusive`-Callback mit SQL-Transaktion. Der Core bewahrt die
Suppressed-Sperre, Generationbindung und die Unveränderlichkeit des ersten
`EndObservedAt`. Ein abweichender Endzeitwert wird nicht still überschrieben.

## Direct-/admitted-API-Grenzen

Die Rollen-/Disposed-Grenzen sind im aktuellen Delta konsistent:

- `ProfileAdmission.RunWithToken` prüft exakte Token-Mitgliedschaft, nicht
  freigegebenen Use-Zustand und erlaubte Rollen, bevor der Delegate läuft.
- `ProfileAdmissionToken.Dispose` setzt `ReleaseRequested`; Admission-`Close`
  seal’t neue Entries, setzt aber nicht selbst dieses Tokenflag. Beide widerrufen
  keinen bereits gestarteten Use. Neue Uses mit demselben Token werden abgewiesen.
- `RunStoreWrite` erlaubt nur `Startup`/`Ordinary`; ein `Exclusive`-Token wird
  dort nicht akzeptiert.
- Die direkten **Store**-Overloads `CommitIntentUnderFence`,
  `CommitStateUnderFence` und `CommitEndUnderFence` behalten `RunStoreWrite`
  als eigene Eintrittsprüfung. Die **Access**-Fortsetzungen benutzen dagegen
  nach ihrem äußeren `RunAdmitted` unmittelbar die Store-Cores. Nur dieser
  bereits gepinnte Fortsetzungspfad vermeidet eine zweite Token-Akzeptanz.
- Fremde oder nicht ausgestellte Tokens können weder einen anderen Admission-
  Bereich freigeben noch einen Core autorisieren.
- `_lease.RunExclusive` prüft zusätzlich den live `_lockStream`; ein noch
  vorhandener Admission-Token ist kein Ersatz für den OS-Lease.

Damit ist insbesondere der wichtige Unterschied erhalten: Admission autorisiert
keine losgelöste spätere Arbeit, und ein Core ohne gültigen äußeren admitted
Aufruf ist intern zwar aufrufbar, aber nicht über die normalen
`WorkStateAccess`-/Store-Verträge erreichbar.

## Dirty-Markierung

Für SQLite wird Dirty-Markierung nach erfolgreicher Rückkehr aus der Core- oder
admitted Operation in den Access-Wrappers vorgenommen:

- `TryCommitMediaCore`: nur bei `committed == true`;
- `CommitIntentUnderFenceCore`: nach erfolgreichem Ergebnis;
- `CommitStateUnderFenceCore`: nur bei `result != null`;
- `CommitEndUnderFenceCore`: nur bei `result != null`.

Das passt zu den SQL-Ergebnissen: ein rejected Owner-/Generation-/Suppressed-
Vergleich markiert nicht dirty. Eine erfolgreiche SQL-Transaktion markiert
dirty, auch wenn ein nachgelagerter Fehlerpfad eine Bestätigung nach Exception
benötigt. Die `MarkDirty`-Operation läuft unter `_sync`; sie kann das Dirty-Set
nicht zwischen Prüfung und Eintrag verlieren. Bei voller Dirty-Kapazität wird
bewusst auf `_rebuild=true` umgeschaltet, statt Änderungen zu verwerfen.

Die öffentlichen Direct-Store-Methoden (`CommitMedia`, `CommitIntent`,
`CommitState`, `CommitEnd`) delegieren an ihre admitted Wrapper; **diese markieren
selbst dirty**, noch innerhalb des accepted Use. Auch `TryCommitMediaAdmitted`
markiert bei Erfolg. Die direkten Store-`UnderFence`-Overloads markieren dagegen
nicht selbst: ihre bisherige interne Konvention ist Caller-owned Invalidation.
Die aktuellen produktiven Access-Aufrufe erledigen dies nach ihrer Core-Rückkehr.
Die Caller-Suche ergab keinen anderen produktiven Aufruf der neuen sichtbaren
Store-Cores außerhalb dieser Access-/Store-Wrapper. Die Cores selbst prüfen
keinen Use-Pin; die Garantie gilt daher für diese untersuchten Callpaths, nicht
für beliebigen zukünftigen assembly-internen Direktaufruf.

Präzisierung zur Lease-Lebensdauer: Ein Use-Pin hält die Admission-Reservation,
nicht automatisch den OS-Lock. Ein verfrühtes `ProfileLease.Dispose` kann vor dem
Core-Monitoreintritt gewinnen; dann lehnt `RunExclusive` ab, bevor SQL geöffnet
wird. Hat der Core den Monitor bereits betreten, kann Dispose den OS-Lock nicht
während der Transaktion freigeben. Erfolgreiche Fortsetzung während Shutdown
setzt weiterhin korrektes ownerseitiges Seal/Drain/Dispose voraus.

## Kritische Bewertung des vorgeschlagenen Test-Seams

Der vorgeschlagene Callback **nach dem äußeren `RunWithToken`-Acceptance-Block,
außerhalb `_sync`, aber vor `operation()`** ist für genau die alte nested-use
Hypothese sinnvoll:

1. Der Test kann belegen, dass der äußere Use bereits gezählt ist.
2. Danach kann `Dispose`/Release angefordert werden.
3. Der alte nested Wrapper würde beim erneuten `RunWithToken` wegen
   `ReleaseRequested` ablehnen; der aktuelle direkte Core sollte ohne neue
   Token-Akzeptanz weiterlaufen.

Aber der Seam beweist **nicht** automatisch, dass der Worker anschließend an
`_lease.RunExclusive` blockiert. Hält der Testthread den Lease-Monitor, zeigt
der Callback nur den Zustand vor dem Core-Aufruf. Die tatsächliche
Lease-Warteposition ist danach schedulerabhängig und ohne separaten
Lease-Wait-Seam nicht beobachtbar. Ein Test darf daher aus dem Callback allein
keinen negativen W6-02-Runtimebeleg und keine exakte Zwischeninstruktion
behaupten.

Der Seam selbst wäre eine zusätzliche Testinstrumentierung in `ProfileAdmission`
oder `RunAdmitted` und ist **keine** bereits vorhandene Evidenz. Für diesen
Auftrag wurde er nicht hinzugefügt und nicht ausgeführt. Bei einer späteren
Umsetzung muss der Test weiterhin:

- den Token nur einmal akzeptieren und nach `Dispose` neue Uses ablehnen;
- Direct- und admitted APIs getrennt auf falsche/disposed Rollen prüfen;
- den Lease-Monitor erst nach Freigabe des Callback-Gates lösen;
- den Worker, Barrier-Completion und jeden Fehler beobachten;
- anschließend beide betroffenen SQL-Zeilen sowie untouched rows passiv prüfen;
- Dirty-Rebuild/Dirty-URL separat verifizieren, ohne daraus einen Lease-Wait-
  Beleg zu machen.

## Befundklassifikation

- **W6-01 alter negativer Befund:** echt und nicht durch diese Reviewanalyse
  aufgehoben.
- **W6-02 nested-before-lease Race:** aktueller Codepfad wirkt strukturell
  korrigiert; kein negativer Runtimebeleg vorhanden und hier nicht behauptet.
- **Live lease lock / use-pin:** für die drei geprüften Core-Transaktionen
  korrekt verschachtelt: outer accepted use, dann `RunExclusive`, SQL-Commit,
  Rückkehr, erst danach Use-Ende.
- **Direct/admitted rejection:** im geprüften Pfad erhalten.
- **Dirty marking:** nach erfolgreicher Mutation erhalten; kein konkreter
  Verlustpfad im Delta nachgewiesen.

**Astra-Entscheidung:** kein Code-, Test-, Build-, State- oder Git-Schritt aus
diesem Review. Nur dieser Bericht wird geschrieben.

### Zusätzliche Seam-Auflagen

Der Hook muss innerhalb des vorhandenen `try/finally` liegen, damit auch ein
Hookfehler `EndUse` ausführt. Instanzlokal, exact-token-gefiltert, one-shot; kein
Hook unter Admission-`_sync`, kein ambient authority bypass. Derselbe rein
beobachtende Seam muss auf Alt- und Fixstand liegen, sonst ist ein späterer
Red/Green-Vergleich nicht aussagekräftig. Ein Gate nach **Rückkehr** von
`RunWithToken` wäre falsch: dann ist der Pin bereits beendet. Gemeint ist der
Beginn seines akzeptierten Delegates. Nach Workerende muss Exclusive/Drain
abschließen dürfen; danach noch „pending“ zu verlangen wäre eine falsche
Assertion. Für Dirty-Prüfungen zuerst den initialen Full-Rebuild mit `TakeDirty`
verbrauchen, sonst würde `IsDirty` auch ohne Mutation true liefern.

Gelesene Bezüge: `WorkStateAccess.cs:84–136,160–240`,
`WorkStateStore.cs:89–91,176–353`, `ProfileLease.cs` (`RunStoreWrite`,
`RunExclusive`, `Dispose`), `ProfileAdmission.cs` (`RunWithToken`, `Release`,
`EndUse`), `review/wave7-workstate-continuation-tests-sol.md`. Alte W6-01-
Laufzeitevidenz hier nicht neu ausgeführt oder unabhängig revalidiert; deren
Existenz ist die ausdrückliche Vorgabe dieses Auftrags.
