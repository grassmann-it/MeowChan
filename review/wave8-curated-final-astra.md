# Wave 8 final — unabhängiger Astra-Review des Curated-Projection-Deltas

## Prüfauftrag und Prüfgrenzen

Geprüft wurde ausschließlich das aktuelle Delta gegenüber dem vorherigen
Curated-Projection-Review:

- `libmeowchan/Transfer/NativeTransferCuratedProjection.cs`
- die aktualisierte
  `tests/MeowChan.Tests/Transfer/NativeTransferCuratedProjectionTests.cs`
- `tests/MeowChan.Tests/Transfer/NativeTransferCuratedProjectionBoundaryTests.cs`

Der Abgleich erfolgte gegen die bereits gelesenen Transfer-/Selection-Verträge,
insbesondere die Files-/HTML-Trennung, den Preview-Grenzvertrag und die
v1/v2/v3-Descriptorregeln. In diesem Auftrag wurden **keine** Builds oder Tests
ausgeführt. Es wurden keine Git-, RUNSTATE-, Lease- oder Snapshotoperationen
durchgeführt und kein Produktcode geändert. Die einzige Änderung dieses
Auftrags ist dieser Reviewbericht.

## Delta-Befund

Die beiden vorherigen P2-Befunde sind im aktuellen Produktcode grundsätzlich
begrenzt behoben:

1. `Project` verwirft jetzt vor der Descriptorprojektion jede Selection mit
   `selection.Mode != CuratedExportMode.Files` oder
   `selection.PreviewsRequested == true`.
2. Der Kommentar benennt ausdrücklich, dass `Files` hier nur als Vertrag für
   Original-Occurrences verwendet wird; HTML und angeforderte Previews werden
   nicht still als erfüllt gemeldet.
3. Die aktualisierten Projection-Tests decken die drei nicht unterstützten
   Kombinationen ab: HTML ohne Preview, Files mit Preview und HTML mit Preview.
   Sie prüfen außerdem, dass der Quellkatalog byte-/serialisierungsseitig
   unverändert bleibt.
4. Die neuen Boundary-Tests decken zusätzlich Multi-Thread-Scope und
   Quellreihenfolge, gleiche Payloadbytes mit verschiedenen Ressourcen,
   fehlende Occurrences, Mehrfach-Occurrences, exakte Identität statt
   Hash-/Dateinamenmatching, Preview-Rolle, Source-/Board-Trennung,
   ungültige Metadaten außerhalb des ausgewählten Scopes, Retained-Bindings
   sowie die Nicht-Promotion fehlender Payloads ab.

### Einstufung

- **P0: keine gefunden.**
- **P1: keine gefunden.**
- **P2-Produktbefunde: keine neuen gefunden.**

Die Guard-Änderung ist für den geprüften Pure-Slice fail-closed und beseitigt
die zuvor dokumentierte semantische Falle. Sie macht aus dem Slice weiterhin
keinen Preview- oder HTML-Producer; sie weist diese Eingaben jetzt lediglich
korrekt zurück.

## Nicht blockierender QA-Follow-up — weitere gültige Current-Statuswerte

**Typ:** ergänzende Testabdeckung, kein neuer P2-Produktbefund und kein
Blocker für den eng definierten Original-/Omitted-/Unavailable-Schnitt.

Der neue Boundary-Test `SelectedObservationKeepsItsStatusWithoutPromotingMissingBytes`
prüft für Current und Retained `Contained`, `Omitted` und `Unavailable`. Der
Serializer-/Transfervertrag erlaubt bei aktuellen Attachments daneben jedoch
auch `Unknown`, `Failed` und `Cancelled`. Die Projektion gibt diese Statuswerte
im aktuellen Code generisch unverändert weiter; der Test beweist das noch nicht.
Für Retained sind diese drei Werte nach dem Vertrag absichtlich nicht zulässig,
was getrennt bleiben muss.

**Begrenzte Empfehlung:** Vor einer Abnahme des Status-Erhaltens mindestens
`Unknown`, `Failed` und `Cancelled` für eine aktuelle Occurrence synthetisch
prüfen und jeweils Payload-/History-Nullheit bestätigen. Für Retained darf
stattdessen ein negativer Validator-/Serializerfall bestehen. Bis dahin ist
„alle gültigen Current-Statuswerte bleiben erhalten“ nicht vollständig
belegt; der Pure-Slice bleibt für die bereits getesteten Statuswerte
unauffällig.

## Compile-/Test-Risiken des aktuellen Deltas

1. Für den aktuellen Stand gibt es noch keinen gültigen grünen Receipt: Der
   genannte Build `9556` scheiterte an einem GLM-URL-Test mit einer öffentlichen
   Signatur, die einen internen Enum-Typ verwendete. Laut Auftrag wurde diese
   Signatur inzwischen korrigiert; das ist aber noch keine unabhängige
   Compilebestätigung des reparierten Stands. Der nächste Freeze-/Buildlauf ist
   daher erforderlich, bevor der aktuelle Delta-Stand als grün bezeichnet wird.
2. Die neuen Boundary-Tests wurden in diesem Review nicht ausgeführt. Ihre
   synthetische Konstruktion verwendet keine Dateien, Profile, Stores oder
   Netzwerkzugriffe; ihre erwarteten PASS-/FAIL-Ergebnisse bleiben bis zum
   erneuten autoritativen Lauf unbestätigt.
3. Die Testfälle sind statisch plausibel angelegt: Bei den fachlichen
   Negativfällen validiert der Test zunächst den veränderten Katalog mit
   aus synthetischen Bytes berechneten SHA-/MD5-/SHA1-Messwerten. Bei den
   absichtlich beschädigten Descriptoren besteht stattdessen eine gültige
   Ausgangs-/Projektionskontrolle vor der Beschädigung. Damit wird die
   erwartete Rejection nicht allein aus einem ungeprüften Setup abgeleitet.
   Das ersetzt trotzdem weder den fehlenden Lauf noch den echten
   PackageWriter-/Payload-Receipt.
4. Der Delta-Guard liegt vor `ValidateDescriptors(source)`. Bei nicht
   unterstützter Mode-/Preview-Auswahl wird deshalb erwartungsgemäß zuerst die
   Eingabegrenze gemeldet, nicht ein eventuell zusätzlich beschädigter
   Katalog. Das ist für einen Boundary-Guard korrekt; ein Caller darf daraus
   keine Aussage über die Validität des verworfenen Katalogs ableiten.

## Exakte Annahmegrenze nach diesem Review

Nach erfolgreichem Re-Freeze und den dazugehörigen fokussierten Läufen kann der
Delta-Stand höchstens als folgender Baustein angenommen werden:

> Reiner öffentlicher In-Memory-Descriptor-Slice für eine eingefrorene
> `CuratedExportMode.Files`-Selection ohne angeforderte Previews. Nicht
> unterstützte HTML-/Preview-Selections werden vor der Projektion abgewiesen.
> Für unterstützte Eingaben werden exakte Resource-/Post-/Position-/Revision-
> Occurrences gebunden, nicht ausgewählte Payload-/Historyfelder ausgelassen,
> öffentlicher Threadkontext und Retained-Surrogates erhalten und fehlerhafte
> Descriptoren fail-closed abgebrochen.

Nicht abgenommen oder freigegeben sind weiterhin:

- Previewerzeugung oder Previewformat-/Größenvertrag;
- HTML-Export durch diesen Consumer;
- echter nativer/ZIP-Export, PackageWriter, Payloadquellen,
  Byteverifikation und Manifest-/Envelope-Fencing;
- private Favoriten, Tags, Notizen und deren Importauswahl;
- Importvorschau, Apply, Recovery, Konfliktauflösung und 404-/Multi-Thread-
  Merge;
- UI-, Desktop-, Windows- und macOS-Abnahme;
- Gesamtgoal oder Releasefreigabe.

**In diesem Auftrag geänderter Pfad:**
`review/wave8-curated-final-astra.md`
