# Review — NativePrivateApplyExecutor disjunkter Core-Slice

**Datum:** 2026-11-09
**Ergebnis:** Kein `NativePrivateApplyExecutor.cs`-Stub und keine unsichere Teilimplementierung angelegt.

Der angeforderte Executor ist auf dem aktuellen Quellstand noch nicht sicher
implementierbar. Die vorhandenen `PrivateStoreBatch`-APIs sind brauchbare
Persistenzprimitive, bilden aber zusammen mit `NativePrivateApplyRequest`,
`NativePrivateApplyJournal` und `NativePrivateApplyRecovery` noch keinen
vollständigen owner-issued Apply-Vertrag. Eine Implementierung würde mindestens
einen der folgenden Daten- oder Sicherheitsverträge stillschweigend erfinden.

## Befund 1 — Media-Favorite-Addition verliert erforderliche Werte

`NativePrivateApplyRequest.SelectedIncoming.MediaFavorites` und die
`NativePrivateMediaFavoriteComparison` enthalten nur den exakten
`FavoriteAssetKey`. `FavoriteStore.PrepareAdditiveBatch` benötigt dagegen eine
vollständige `FavoriteEntry` mit `Length` und `OriginalFileName`.

Die Wire-Projektion schreibt bewusst nur:

- Thread-Identität
- `mediaId`
- `variant`
- `sha256`

Ein Executor kann aus dem Request weder `Length` noch `OriginalFileName`
belegbar ableiten. `new FavoriteEntry(key, 0, null)` wäre eine stille
Datenverschlechterung; eine Ableitung aus URL, Dateiname oder SHA-only wäre nach
`native-private-batch-apply-v1.md` verboten. Der Executor braucht deshalb vor
seiner Batch-Erzeugung entweder:

1. einen owner-/target-gebundenen Resolver für das aktuell oder retained
   verifizierte Original, der eine vollständige `FavoriteEntry` liefert, oder
2. eine explizite, gebundene Erweiterung der ausgewählten Media-Addition um
   Länge und Originalnamen samt passender Wire-/Digest-Regeln.

Bis dahin darf die Media-Favorites-Stufe nicht als erfolgreich ausführbar
behauptet werden.

## Befund 2 — Erfolgreiche Store-Commit-APIs haben kein Marker-Retirement

Alle drei `CommitPreparedBatchAdmitted`-Pfade schreiben einen
`OperationMarker`. Die normalen Writer prüfen anschließend
`PrivateStoreBatchFiles.EnsureUnmarked(...)` und blockieren bei vorhandenem
Marker. Im Vertrag `private-apply-store-batch-v1.md` ist ausdrücklich
festgehalten: **kein Marker-Retirement-/Unblock-API**.

Ein Executor könnte daher zwar einen Marker-bearing After-State publizieren und
einen Journal-Receipt schreiben, aber nicht sicher den Übergang in den normalen
Profilebetrieb abschließen. Ein bloßes Löschen oder Umschreiben des Markers
würde die Store-Fence-/Recovery-Semantik umgehen und ist nicht zulässig.

Benötigt wird ein store-owned, token-gebundenes Retirement, beispielsweise
`RetireCommittedBatchAdmitted(batch, admission)` mit:

- exakter Operation-/ContentPlan-/StoreKind-Prüfung;
- exakter After-/Backup-Fence-Prüfung;
- uncancelled Readback;
- idempotentem Replay-Verhalten;
- keiner Freigabe bei Drift, Pending-Evidence oder unbekanntem IO;
- einer klaren Journal-/Receipt-Reihenfolge (Receipt erst nach bestätigter
  Retirement- bzw. explizit definierter marker-bearing Final-State-Semantik).

Ohne diese Entscheidung würde ein vermeintlich `Completed` Apply das Profil für
normale private Store-Schreiber blockieren.

## Befund 3 — Der Request trägt keine überprüfbare Owner-/Token-Provenienz

`NativePrivateApplyRequest` ist laut eigener Dokumentation passive Daten und
keine Consent-, Lease- oder Apply-Capability. `NativePrivateMediaImportApplyAdmission`
prüft beim Erzeugen zwar den Plan-Consent, aber der Request enthält danach keine
owner-issued Capability oder einen unverwechselbaren Bezug zu dieser
Entscheidung. Journal-Replay lädt nur den Request.

Zusätzlich enthält `NativePrivateApplyRequestBinding.ProfileIncarnation` nur
einen übergebenen Scalar. `ProfileLease` stellt aktuell keine passende
persistente/incarnation-gebundene Verifikation bereit, gegen die der Executor
diesen Wert prüfen könnte.

Ein `Execute(request, admission)` würde somit einen beliebig erneut
konstruierten/decodierten Request als owner-issued behandeln. Das wäre mit dem
Private-Consent-Vertrag nicht vereinbar. Erforderlich ist eine interne,
owner-/lease-gebundene Apply-Capability oder ein gleichwertiger Admission-Nachweis,
der nicht aus Request-Bytes rekonstruierbar ist.

## Befund 4 — Recovery-Schritte können Conflict/MissingTarget nicht ehrlich
abbilden

`NativePrivateApplyStepPhase` kennt derzeit nur Store-/Publikationsphasen wie
`Recorded`, `NoChange`, `IntentDurable`, `Drifted` und `Unreadable`. Der
Release-Vertrag verlangt für eine abgeschlossene Kategorie aber auch
`ConflictOnly` und `MissingTarget` als unterscheidbare Fakten. Die
Comparison-Rows liegen im Request, sind aber keine Step-Receipt-Zustände.

Ein Executor könnte Conflict/MissingTarget nur fälschlich als `NoChange`
verbuchen oder die Information außerhalb des Receipts verlieren. Beides wäre
kein belastbarer `Completed`-Receipt. Vor der Executor-Implementierung muss
entweder die Recovery-/Receipt-Domain diese nicht-mutierenden Kategorien
aufnehmen oder eine explizit begründete, content-freie Zuordnung normativ
festgelegt werden.

## Was bereits sicher vorhanden ist

Die folgende Infrastruktur wurde nur gelesen und nicht verändert:

- `PrepareAdditiveBatch` für Thread-, Media- und Bookmark-Store;
- `CommitPreparedBatchAdmitted` mit einem bereits akzeptierten
  `ProfileAdmissionToken`;
- `NativePrivateApplyJournal` mit Claim/Prepared/Applying und create-only
  Journal-Kette;
- `NativePrivateApplyJournal.InspectStores` als read-only Store-Inspektion;
- `NativePrivateApplyRecovery` mit Ordered-Step-Reducer und
  `RecoveryRequired`-Schutz;
- bounded Request-/Fence-/Marker-Validierung.

Diese APIs reichen für weitere Tests der jeweiligen Primitive, aber nicht für
einen produktionsfähigen Executor, der alle drei Kategorien und einen
releasefähigen Receipt sicher abschließt.

## Empfohlene nächste, disjunkte API-Arbeitspakete

1. **PrivateApplyCapability / owner binding:** owner-issued Consent, Request,
   Profile-Lease-Incarnation, Operation und Token in einer nicht serialisierbaren
   internen Capability zusammenführen.
2. **Media addition source:** vollständige, target-originalgebundene
   `FavoriteEntry`-Auflösung vor `PrepareAdditiveBatch`; kein SHA-/Path-/Preview-
   Fallback.
3. **Marker retirement:** store-owned, exact-fenced, admission-gebundene und
   recovery-sichere Retirement-Primitive ergänzen.
4. **Receipt domain:** ConflictOnly/MissingTarget sowie additive/conflict/missing
   Counts content-frei und bounded in Recovery-/Journal-Receipts abbilden.
5. **Erst danach:** `NativePrivateApplyExecutor.cs` mit fester Reihenfolge
   `ThreadFavorites → MediaFavorites → PostBookmarksAndNotes`,
   `Claim → Prepared → Applying`, per-step Intent/StoreInspection/Readback und
   terminalem Recovery-Receipt implementieren.

## Verifikation dieses Reviews

Auf dem unveränderten Core-Stand liefen die einschlägigen vorhandenen Tests:

```text
dotnet test tests/MeowChan.Tests/MeowChan.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~NativePrivateApplyJournal|FullyQualifiedName~NativePrivateApplyStoreBinding|FullyQualifiedName~NativePrivateApplyRequest|FullyQualifiedName~PrivateBatch'

Passed: 537  Failed: 0  Skipped: 0
```

Es wurden keine Desktop-/Host-/Session-/Dialogdateien und keine bestehenden
Verträge geändert. Dieses Review ist bewusst die angeforderte API-Lücken-
Alternative und kein Executor-Stub.
