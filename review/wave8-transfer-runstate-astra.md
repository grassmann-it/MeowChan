# Wave 8 — Transfer-/RUNSTATE-Reconciliation (Astra)

**Prüfart:** bounded read-only Architektur-/Reconciliation-Review  
**Stand:** 03.11.2026, Arbeitskopie `/home/tomka/Desktop/oChan`  
**Ziel:** GP-12/GP-13/P08e; aktueller RUNSTATE, Wave-7-Belege, Commits/WIP und
relevante Verträge.  
**Durch diese Prüfung:** keine Code-, Test-, RUNSTATE-, Lease-, Pointer- oder
Commitänderung; kein Build und kein Testlauf. Dieser Bericht ist die einzige
Dateiänderung.

## 1. Geprüfte Basis und Kurzurteil

RUNSTATE führt das Profil-A-Gesamtgoal weiterhin als `active`, GP-12 und GP-13
als `in_progress`. Die aktuelle `source-snapshot.json` verweist auf

```text
8127e62a63d467e4204eb35ab62c637e272dfcfb3bac18b3d0137c7f8a985ce1
```

mit 2053 Inputs und `base_sha` `c71493baec168c16f7d0b21b134429d7f518ecbd`.
Der Manifest-Hash wurde aus den gelesenen Input-SHA-256 neu nachvollzogen; der
Hash ergibt wieder `8127e62a...`. Alle 2053 Manifestdateien stimmen sowohl mit
der eingefrorenen Quelle als auch mit den entsprechenden Live-Dateien überein.
Der aktuelle Snapshotpointer braucht deshalb **keine Korrektur**. Das ist keine
Aussage, dass der darin enthaltene WIP bereits abgenommen ist.

Die letzten Commits sind:

```text
814090d fix: continue admitted workstate writes without nested admission
c71493b test: verify admitted workstate continuations after release
```

Der Arbeitsbaum enthält daneben umfangreichen uncommitted WIP, unter anderem
P08e-Producer-/Registry-/Queue-/WorkState-Dateien, Startup-/UI-/L10n-Änderungen,
kuratierten Transfercode und neue Regressionstests. `c71493b` ist daher nicht
der vollständige aktuelle Produktstand. Geschützte bzw. persönliche WIP-Dateien
wurden nicht als Integrationskandidaten behandelt.

**Urteil:** Es gibt einen korrekt gebundenen aktuellen Source-Freeze und einige
belastbare, aber ältere Slice-Belege. Es gibt noch keinen aktuellen
GP-12/GP-13- oder P08e-Abnahmebeleg. Der nächste sichere Schritt ist eine neue,
eng begrenzte Reconciliation auf genau `8127e62a...`; danach darf nur in
getrennten, abhängigkeitstreuen Slices weiter integriert werden.

## 2. RUNSTATE-, Lease- und WIP-Befund

### Belegt

- `accepted_decision_profile` ist korrekt auf Profil A gesetzt; der kumulative
  Umfang bleibt maßgeblich.
- RUNSTATE beschreibt P08e weiterhin als zu verifizierende Owner-/Producer-
  Integration und behauptet keine private Apply-Autorität.
- Die dort eingetragenen P08d-/L10n-Reconciliations sind als bounded slices
  formuliert und enthalten weiterhin die offenen Grenzen (Allwriter, Transfer,
  native Windows/macOS, vollständige Sprach-/UI-Qualität).
- Der aktuelle Snapshot enthält den kuratierten Projection-Schnitt sowie den
  aktuellen WIP. Das Manifest ist intern konsistent; es besteht kein Anlass,
  den Pointer auf den älteren `df1d114...`-Stand zurückzusetzen.

### Nicht als aktuelle Liveness behandeln

RUNSTATE enthält zahlreiche `active`-Leases aus früheren Workerwellen, teils mit
Zeitpunkten und Scopes vor der aktuellen Reconciliation. Aus der Datei allein
folgt nicht, dass die Agenten noch laufen oder dass ihre Dateien noch exklusiv
belegt sind. Vor einer nächsten Integration muss Root die aktuelle Worker-
Liveness außerhalb dieses Reviews feststellen und alte Leases nur dort
abschließen, wo das tatsächlich verifiziert ist. Dieser Review ändert die Leases
nicht.

Ebenso ist die RUNSTATE-Zeile „neue 12/14 bounded assignments“ eine
Dispatch-/Auftragsaussage, kein PASS- oder Livenessnachweis. Reports dürfen erst
nach Hash- und Scopeprüfung in den aktuellen Reconciliationstand einfließen.

## 3. Evidenzstatus: gültig, veraltet oder ungültig

### Wave-7-Belege auf `df1d114...`

Die folgenden Belege sind intern als Aufzeichnungen verwendbar, aber **nicht
aktuelle Evidenz für den Pointer `8127e62a...`**:

- `wave7-core-all-01`: 4451 PASS, 0 FAIL, 2 SKIP;
- `wave7-desktop-all-01`: 2137 PASS, 0 FAIL, 8 SKIP;
- `wave7-continuation-focused-01`: 6/6 PASS;
- `wave7-continuation-focused-03`: 14/14 PASS;
- `wave7-core-focused-01`: 27/27 PASS;
- `wave7-desktop-focused-01`: 10/10 PASS.

Die Log-SHA-256 und die TRX-Counter dieser Receipts stimmen mit den gelesenen
Dateien überein. Die Belege bleiben für den exacten `df1d114...`-Stand gültig,
belegen aber nicht automatisch den aktuellen Freeze: Der aktuelle Manifeststand
fügt mindestens `NativeTransferCuratedProjection.cs` und dessen Testdatei hinzu.
Für eine erneute aktuelle Abnahme sind mindestens frische Current-Freeze-
Build-/Focused-/Full-Suite-Receipts erforderlich. Die bestehenden SKIPs sind
weiterhin SKIPs.

`wave7-core-focused-01` und `wave7-desktop-focused-01` sind zusätzlich an den
älteren `c906681b...`-Snapshot gebunden und daher nur historische Fokusbelege.

### Explizit ungültiger bzw. nur negativer Kontrollbeleg

- `wave7-continuation-focused-02` ist laut
  `wave7-incremental-restore-correction.json` wegen stale Source-mtimes mit
  negativen Control-Binaries gelaufen. Die 6 FAIL dürfen nicht als Regression
  des Fixstands verwendet werden.
- `wave7-nested-negative-focused-01` auf `688a4fe7...` ist ein absichtlich
  negativer Kontrolllauf (6/6 FAIL). Er ist als Kausalitätskontrolle wertvoll,
  aber kein Produkt-PASS.
- `wave7-core-final-build-01` ist in derselben Korrektur ausdrücklich aus der
  Fixed-Source-Akzeptanz ausgeschlossen.

### Aktueller Projection-Beleg

`wave7-curated-build-01` und `wave7-curated-focused-01` sind an den aktuellen
`8127e62a...`-Snapshot gebunden; der Fokuslauf ist 9/9 PASS. Das belegt nur den
reinen In-Memory-Curated-Projection-Slice, nicht Byteverifikation, Package-
Writer, Import, Merge oder UI.

`review/wave7-curated-projection-astra.md` ist gegenüber dem aktuellen Code als
**historischer Review** zu behandeln: Er beschreibt die vorherigen P2-01/P2-02-
Befunde. Der Sol-Follow-up behauptet Korrekturen, aber ein frischer unabhängiger
Astra-Review des korrigierten Bytes liegt für diese Prüfung nicht vor. Deshalb
kein Review-Closure aus dem alten Astra-Bericht ableiten.

`review/wave7-workstate-runtime-reconcile-astra.md` ist ebenfalls an `df1d114...`
gebunden. Er enthält eine offengelegte prozedurale Abweichung (versehentlicher
Aufruf von `freeze-wave7-nested-negative.py --help` mit zusätzlichem Artefakt-
copy). Seine eng begrenzte Runtimeinterpretation kann als historische
Reconciliation gelesen werden, nicht als aktueller `8127e62a...`-Beleg und nicht
als vollständige P08e-/Lease-Abnahme.

### P08e-Evidenz

Die vorhandenen P08e-Reviews (c72/4d8/5cf- und weitere frühere Stände) sind
statische bzw. slicebezogene Handoffs. Sie schließen einzelne alte Findings
eng, geben aber keine aktuelle Laufzeitabnahme für den aktuellen WIP frei. Der
aktuelle P08e-Code besitzt in der geprüften Ablage keinen neuen, auf
`8127e62a...` gebundenen Full-/Focused-Receipt. Insbesondere fehlen weiterhin
aktuelle Belege für:

- gesamte Producer-Epoch über Scheduler, Queue, Worker-Finally, Result-/Cleanup-
  und Completionpfade;
- alle Writer-/Ownerfences außerhalb der bereits enrolled Storefamilie;
- Stop, temporäres Suspend/Exclusive, Retry nach verlorener Activation und
  partial activation auf dem aktuellen Freeze;
- korrekte P08e-Integration zusammen mit dem Transfer-Publisher.

Die WorkState-Continuation-Fixbelege sind ein starker enger Hinweis für die
beiden untersuchten admitted Continuations; sie ersetzen nicht die aktuelle
P08e-Gesamtprüfung.

## 4. Verträge und daraus folgende Blocker

### GP-12/GP-13

Die relevanten Verträge wurden gegen den aktuellen Plan gelesen:

- `contracts/native-import-stage-slice.md`: besitzt und validiert eine passive
  Paketkopie, veröffentlicht aber nichts;
- `contracts/passive-media-publication-v1.md`: fordert vorbereiteten Candidate,
  durable Operationzustände, Receipt-first-Recovery, geordnete Directory-/Host-
  Fences und einen einzigen SQL-Sichtbarkeitsschalter;
- `contracts/passive-media-merge-v1.md`: fordert additive current/retained-
  Identität, 404-Erhalt, sichere Ergänzungen neben Konflikten, Reexport-/Ziel-
  budgets und vollständiges Replay/Restart/Resolver-Verhalten;
- `contracts/private-apply-request-recovery-v1.md` und
  `contracts/private-store-restart-inspection-v1.md`: private Werte sind
  selected-only und passiv, nicht bereits Apply-Autorität;
- `profile-admission-kernel-v1.md`, `desktop-profile-admission-v1.md` und
  `private-store-admission-v1.md`: Admissiontoken, Producerepoch und Desktop-
  Commands sind Lifecyclefences, keine Consent- oder Importcapability.

Daraus bleiben als harte Blocker vor einer Produktfreigabe:

1. Kein vollständiger GP-12 Writer→Stage→Candidate→SQL-Commit→Offline-
   Reader/Resolver-Roundtrip auf aktuellem Freeze.
2. Kein vollständiger GP-13 Merge- und Konfliktworkflow mit sicherer Ergänzung,
   inaktiver Konfliktvariante, sichtbarer Resolve-/Discard-Entscheidung und
   Recovery nach Crash/Cancel/Diskfull.
3. Keine vollständige Allwriter-/Host-/Catalog-/Media-/Cleanup-/Shutdown-
   Fencingkette. Admission allein darf kein Apply öffnen.
4. Keine user-facing Importvorschau mit Consent, Konfliktzahlen, fehlenden bzw.
   Preview-only Medien und Wiederaufnahme-/Fehlerdarstellung.
5. Private Favoriten/Tags/Notizen sind weder vollständig als selected-only
   Transferoption noch als dauerhaftes Apply mit Markerretirement abgenommen.
6. Multi-Thread-Medien, retained/legacy Sonderfälle, Foreign-Adapter und native
   Windows/macOS bleiben offene Bestandteile des gewählten Umfangs. Ein interner
   Text-Multi-Thread-Schnitt ist kein Beleg für Medienparität.

## 5. Exakter nächster sicherer Integrationsablauf

Die Reihenfolge ist bewusst konservativ und bindet jede Stufe an einen neuen
unveränderlichen Source-Freeze. Keine Stufe darf die nächste als „mit erledigt“
bezeichnen.

### Schritt 0 — Scope-/Liveness-Reconciliation (Root only)

1. Aktive Worker-/Lease-Liveness prüfen; keine alten RUNSTATE-Leases aus dem
   Dateitext als noch aktiv oder erledigt interpretieren.
2. Uncommitted WIP nach Eigentümer und Pfad trennen. Insbesondere P08e,
   Curated-Projection, L10n/UI und private Testdateien nicht in einem beliebigen
   Sammelcommit vermischen.
3. Eine Integrationsliste für genau die zu prüfenden Dateien erzeugen. Geschützte
   User-WIP-Dateien bleiben bytegleich. Erst danach einen neuen Source-Freeze
   erzeugen; den bestehenden Pointer bis dahin unverändert lassen.

### Schritt 1 — Current-Freeze-P08e-Gate

1. Auf dem neuen Freeze zuerst einen **no-incremental** Core-Build, danach den
   P08e-/WorkState-Fokuslauf und den seriellen Desktop-Fokuslauf ausführen.
2. Danach die vollständigen Core- und Desktop-Suiten auf demselben Freeze
   wiederholen. Alte `df1d114...`-Receipts nur als Vergleich angeben.
3. P08e-Owner-/Astra-Review exakt auf diesen Freeze erneuern. Der Review muss
   mindestens die echten Producer-Scopes und den nested-Admission-/Dispose-
   Übergang einschließen; keine reine Delegaten-/Textprüfung.
4. Bei Fehlern zuerst den Owner-/Admission-Schnitt reparieren und den Freeze
   verwerfen; keine Tests/assertions lockern. Erst nach grüner Reconciliation
   darf der P08e-Slice lokal gesichert werden.

**Gate:** P08e bleibt bis dahin `in_progress`; kein Transfer-Publisher darf seine
Owner-/Admission-Fences voraussetzen.

### Schritt 2 — Curated-Projection formal schließen, ohne sie zu überschätzen

1. Frischen Astra-Review auf den korrigierten `NativeTransferCuratedProjection`
   plus Tests geben.
2. Den Review auf Eingangskatalogvalidierung, null-vs-empty, current/retained
   Doppelkeys, Contained-Hash, fehlende Occurrences und den ausdrücklichen
   Verzicht auf Preview-Erfindung begrenzen.
3. Bei PASS den Slice separat als **reiner Descriptor-/Selection-Producer**
   verbuchen. Er darf weder GP-12-Publisher noch GP-13-Mergeworkflow heißen.

**Gate:** Projection ist erst ein zulässiger Input für spätere Writer, wenn ihr
Eingangsvalidierungsvertrag und der Byte-/Writer-Handoff explizit dokumentiert
sind.

### Schritt 3 — GP-12-Passive-Publication als eigener Owner-Slice

In dieser Reihenfolge implementieren und prüfen:

1. Ownergebundene Operation-ID, Request-/Stage-Fingerprint und Catalog-/Root-/
   Manifestgenerationen vor erstem Candidate-`mkdir` journalisieren.
2. Stagebytes einmalig bounded kopieren und messen; vollständige sichere und
   konfliktbehaftete Eingangsmenge getrennt halten. Keine SQL- oder Reader-
   Sichtbarkeit während Preparation.
3. Unter geordneten Directory-/Profile-/Hostfences rechecken; große Kopien und
   Hashes außerhalb des SQL-/ProfileLease-Monitors durchführen.
4. Prepared-Candidate mit exakten measured bytes und loser/provenance mapping
   persistieren. `Preparing`/`Prepared` nach Neustart unterscheiden; bei
   unbekanntem Ausgang nie blind erneut materialisieren.
5. Einen atomaren SQL-Visibility-Commit mit erfolgreichem Receipt ausführen.
   Nur dieser Commit veröffentlicht die sichere Root; der Konfliktkandidat bleibt
   inaktiv.
6. Normalen Offline-Reader/Resolver nach Neustart prüfen; Replay vor Freshness,
   Delete/Recreate und unlesbarem SQL getrennt behandeln.

**Gate:** Erst ein echter Writer→Stage→Restart→Reader/Resolver→Reexport-
Roundtrip mit current-only contained Originals darf als GP-12-Producerbeleg
gezählt werden. Kein UI, kein Provider, kein persönlicher Datensatz als Fixture.

### Schritt 4 — GP-13 additive Merge-/404-Stufe

1. Zuerst single-thread current/contained Fälle mit A1–20 + B15–30→B1–30 und
   unverändertem 404-/Capturezustand vollständig abnehmen.
2. Danach current/retained/legacy-null Identitäten, exaktes Reuse, gleiche-Key-
   andere-Bytes und sichere Ergänzung neben Konflikten prüfen. Keine Re-Key-
   Reparatur und kein stilles Skip-existing.
3. Danach vollständige inaktive Konfliktkandidaten, durable Resolve/Discard,
   Markerretirement und Recovery nach Neustart umsetzen. Konfliktbytes dürfen
   nicht in den normalen Resolver gelangen.
4. Erst danach mehrere Threads, fehlende/Preview-only Rollen und die vollständige
   Reexport-/physische Budgetprüfung hinzufügen. Ein einzelner Thread darf nicht
   als Multi-Thread-Freigabe umetikettiert werden.

**Gate:** GP-13 bleibt `in_progress`, bis additive Datenwirkung, Konfliktvariante,
Recovery, identisches Replay, Delete/Recreate und Offline-Reexport zusammen auf
festem Source nachgewiesen sind.

### Schritt 5 — Private selected-only Apply und Desktopworkflow

Erst nach stabiler GP-12/13-Writer-/Fencingbasis:

1. Owner-/Catalog-/Media-/Host-Ziel-Fences aus dem Livezustand ableiten, nicht aus
   Caller-DTOs übernehmen.
2. Consent und Importvorschau mit Kategorien, Counts, fehlenden Bytes,
   Konflikten und unveränderten Zielwerten verdrahten.
3. Store-Batch/Journal/Marker exakt an die bereits geprüften selected-only
   Contracts binden; unbekannter Ausgang bleibt `RecoveryRequired`.
4. Resolve/Discard und Markerretirement atomar sichtbar machen; bei Fehlern keine
   rekursive oder implizite Löschung.
5. Erst danach Desktop-Import, Progress, Wiederaufnahme, private Tags/Notizen/
   Favoriten sowie Fremdformatadapter anbinden.

### Schritt 6 — Plattform-/Gesamtfreigabe

Zum Schluss native Windows/macOS, UI-/Sprachqualität, Foreign-Downloader-/Booru-
Adapter und die vollständige Profil-A-Abnahmematrix getrennt prüfen. Ein Linux-
Xvfb- oder synthetischer Runnerlauf ist kein Ersatz für diese Plattformbelege.

## 6. Entscheidung zum Snapshotpointer

**Keine Korrektur aus diesem Review.** Der aktuelle Pointer `8127e62a...` ist
content-addressed, und alle darin verzeichneten Inputs sind bytegleich zur
Live-Arbeitskopie. `df1d114...` bleibt als historischer Fullsuite-Freeze
referenzierbar, darf aber nicht als aktueller Pointer eingesetzt werden. Ein
Pointerwechsel wäre erst nach einer bewusst ausgewählten neuen Integrationsbasis
und einer erneuten Root-Reconciliation zulässig; dieses Review führt ihn nicht
aus.

## 7. Abschließende Freigabegrenze

Dies ist keine Freigabe von GP-12, GP-13, P08e, privatem Apply oder einem Release.
Die sichere Aussage lautet enger:

> Der Runstate und der aktuelle Sourcepointer sind konsistent. Wave-7 enthält
> gültige historische Slice-Belege, einen aktuellen 9/9-Projection-Fokusbeleg
> und explizit auszuschließende negative/stale Receipts. Als nächstes muss Root
> den aktuellen P08e-/Transfer-WIP auf einem neuen festen Freeze serial bauen,
> fokussiert und vollständig reconciliieren; erst danach folgt der getrennte
> GP-12-Publisher und anschließend der GP-13-Merge-/Konfliktworkflow.
