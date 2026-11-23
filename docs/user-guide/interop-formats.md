# Interop-Formate — nativer Transfer und geplante Fremdprofile

**Status:** unveröffentlichter Arbeitsstand. Diese Seite beschreibt den verbindlichen
Zielvertrag und grenzt ihn von heute vorhandenen Bausteinen ab. Der native Archivdialog
kann den öffentlichen `chan-v4`-Pfad inzwischen prüfen, einen einzelnen Thread als Neu-
oder Merge-Plan anzeigen und nach ausdrücklicher Bestätigung anwenden. Das ist noch
keine Laufzeit-, Plattform- oder Releaseabnahme: private Kategorien, Mehrthread-Apply,
Konfliktentscheidungen und vollständige Recovery-UX bleiben bis zu ihrer eigenen
Abnahme gesperrt. Davon unberührt bleiben die bestehenden, getrennten HTML- und
Favoriten-Originalexporte. Insbesondere existiert aktuell **kein produktiver
KurobaEx-, YChanEx-, Dashchan-, gallery-dl-, Hydrus- oder Grabber-Adapter**, der fremde
Archive oder Medienbibliotheken übernimmt. Interne FourChan-JSON- und YChanEx-
Inspektoren bleiben reine Metadatenprüfungen ohne Dateisystem-, Medien- oder
Storeübernahme.

MeowChan trennt drei Dinge:

1. **Nativer MeowChan-Transfer:** ein eigenes ZIP mit JSON-Manifest, JSON-Katalog und
   gemessenen Medien. Das Katalogformat kann mehrere Threads beschreiben; der heutige
   interne Archivexport friert dagegen genau einen Thread mit Original- und historisch
   gebundenen Anhangsfassungen ein und rät keine Preview-Rollen.
2. **Bestehende HTML- und Originalexporte:** Ausgabefunktionen für Menschen oder Dateien.
   Sie sind **kein** vollständiger App-Rundtrip und kein Profilbackup. Die Datei
   `meowchan-export.json` ist ein Exportmarker mit Dateiinventar, keine strukturierte
   Postliste und deshalb kein eigenes Post-Rundtripformat.
3. **Begrenzte Fremdprofile (Ziel):** passiv zu inspizierende, explizit ausgewählte
   Dateien. Sie dürfen erst nach einer Importvorschau und ausdrücklicher Bestätigung in
   den nativen Pfad überführt werden.

## 1. Nativer MeowChan-Transfer

### ZIP-Aufbau

Ein natives Paket trägt im Manifest das Format `meowchan-transfer`. Der Container ist
ein bewusst einfach gehaltener ZIP-Aufbau:

```text
manifest.json
catalog.json
[private.json]
payload/<sha256>
payload/<sha256>
...
```

- `manifest.json` beschreibt Version, Paket-ID, Inhaltsschema und die Hash-/Längenbindung
  von Katalog, optionalen privaten Daten und Payloads.
- `catalog.json` enthält die strukturierten Thread-, Post- und Anhangsbeobachtungen.
- `private.json` existiert nur, wenn private Kategorien ausdrücklich gewählt wurden.
- Jede Mediendatei liegt genau einmal unter ihrem SHA-256-Namen. Mehrere Anhänge mit
  gleichen Bytes können dieselbe Payload referenzieren; gleiche Bytes allein machen sie
  aber nicht zur gleichen Fundstelle.

Das Manifest hat im öffentlichen Fall diese Gestalt:

```json
{
  "format": "meowchan-transfer",
  "envelopeVersion": 1,
  "packageId": "11111111-1111-1111-1111-111111111111",
  "contentSchema": "chan-v2",
  "requiredFeatures": ["chan-v2"],
  "catalog": {
    "length": 1234,
    "sha256": "64-lowercase-hex-characters"
  },
  "payloads": [
    {
      "length": 456789,
      "sha256": "64-lowercase-hex-characters"
    }
  ]
}
```

Dies ist eine Strukturillustration, keine kopierfertige Paketvorlage. Eine gültige
Paket-ID ist ein kanonischer **Nicht-Null-GUID** im D-Format; Hashwerte werden von
MeowChan aus den tatsächlichen Bytes gemessen. Bei privaten Daten lautet `envelopeVersion`
`2`, `requiredFeatures` ergänzt
`meowchan-private-v1`, und `privateMetadata` erhält ebenfalls Länge und SHA-256.

### Katalogschemata

Die interne Paketprüfung kennt derzeit die Schemata:

| Schema | Bedeutung |
| --- | --- |
| `chan-v1` | aktuelle Thread-, Post- und Anhangsbeobachtungen |
| `chan-v2` | ergänzt `retainedBindings`, also historisch erhaltene Anhangsfassungen ohne erfundene Postbeobachtung |
| `chan-v3` | erlaubt zusätzlich eine exakt gekennzeichnete Form von Schlüsselinhalt ohne historischen Integritätsnachweis |
| `chan-v4` | ergänzt originale Byteversionen und abgeleitete Repräsentationen; öffentlicher **Single-Thread**-Import/Merge/Apply ist im nativen v2-Apply-Vertrag erlaubt, private Kategorien und Mehrthread bleiben außerhalb dieses Pfads |

Threads enthalten mindestens die Identität `source`, `board` und `thread`, dazu öffentliche
Beobachtungen wie Seiten-URL, Titel, Quellzustand, Zeiten, Revisionsstand, Sortiervertrag,
Posts und Anhänge. Posts behalten Presence-Informationen: Ein Feld kann fehlen, explizit
`null`, leer oder beobachtet sein. Anhänge tragen Position, Medien-ID, Variante,
Erwerbs-/Medien-URL, Dateinamen, erwartete Prüfsummen und Größen, Revisionszustand, Rolle,
Inhaltsstatus und — nur bei `contained` — den gemessenen Payload-Hash.

Mögliche Inhaltsstatus sind:

| Status | Bedeutung |
| --- | --- |
| `contained` | die Bytes sind im Paket und durch Länge/SHA-256 gebunden |
| `omitted` | dieser Anhang wurde bewusst nicht in das Paket aufgenommen |
| `unknown` | der lokale Zustand war unbekannt |
| `unavailable` | die Bytes sind lokal nicht verfügbar; die Beobachtung bleibt erhalten |
| `failed` | eine frühere Beschaffung/Prüfung ist fehlgeschlagen |
| `cancelled` | der Vorgang wurde abgebrochen |

Ein gültiges Paket ersetzt keine Herkunftsprüfung. SHA-256 sichert die Bytes gegen
versehentliche Beschädigung, beweist aber nicht, wer die Datei ursprünglich erstellt oder
berechtigt verbreitet hat.

### Sicherheitsgrenzen

Der interne Writer und Validator arbeiten mit harten Obergrenzen, unter anderem:

- maximal 2 GiB native ZIP-Containergröße beim Schreiben,
- maximal 10.000 ZIP-Einträge; der Writer erlaubt daher höchstens 9.998 Payloads im
  öffentlichen und 9.997 Payloads im privaten Manifest,
- maximal 4 MiB ZIP-Zentralverzeichnis,
- maximal 1 MiB Manifest,
- maximal 16 MiB Katalog und 16 MiB private Metadaten,
- maximal 1 GiB je Payload,
- höchstens 4 GiB deklarierte Komponentenbytes bei passiver Eingangsprüfung,
- maximal 1.000 Threads,
- je 100.000 Posts und Anhangsvorkommnisse; aktuelle und retained Anhangsvorkommnisse
  teilen das Anhangsbudget, während eine ausdrückliche Null-Attachment-Sammlung kein
  Anhangsbudget verbraucht.

Die Prüfungen erfolgen vor jeder Übernahme. ZIP-Pfade, doppelte Einträge, unbekannte
Felder, falsche Längen/Hashes, ungültige Identitäten, unsichere URLs, widersprüchliche
Revisionen oder ungültige Presence-Werte führen zur Ablehnung. Ein Paket wird nicht
teilweise „gerettet“. Öffentliche URLs und Threadseiten müssen dem engen 4chan-/8kun-
Referenzvertrag entsprechen; unbekannte URLs sowie Zugangsdaten-, Bearer- oder signierte
Queryformen werden abgelehnt, nicht bereinigt und nicht sicher weggelassen.

### Implementierter Stand und Bedienbarkeitsgrenzen

Der aktuelle Quellstand enthält interne Bausteine für Schema-/Paketprüfung, einen
create-only Single-Thread-Archivexport, Eingangsstaging, eine paketseitige Inventarprüfung
sowie Text- und Ein-Thread-Medienvorbereitung/-merge. Die Inventarprüfung kann Pakete
mit mehreren Threads zählen; der native **Apply**-Pfad admittiert derzeit genau einen
öffentlichen Thread und führt New/Merge über die bestehende Host-/Receipt-Kette aus.
Ein interner Textpfad kann mehrere medienfreie Threads vorbereiten, ist aber noch nicht
als atomarer Desktop-Batch angeschlossen. Der Archive-Header ist auf einen echten
Transferdialog verdrahtet: Datei wählen, lesen, Plan prüfen, Apply oder Abbruch und
reconciliertes Ergebnis. `private.json` bleibt ohne eine gebundene explizite Consent-
Entscheidung gesperrt; Mehrthread-Apply und dauerhafte Konfliktaktionen sind noch keine
freigegebene Produktfunktion. Native Export-/Import-/Merge-/Prepare-/Commit-
Quellbausteine sind weiterhin kein CLI- oder Fremdformat-Einstieg. Die existierende
HTML-/Originalexportoberfläche ist davon getrennt. Die kuratierte native Projektion ist
nur ein Deskriptorschnitt für Files-Auswahlen ohne Preview und ist selbst kein ZIP-Writer,
Byteprüfungs- oder vollständiger Transferexportdialog.

Die native Eingangsstufe kopiert zuerst eine begrenzte Bytegruppe und übergibt erst nach
vollständiger Paketprüfung ein internes Stage-Objekt. `private.json` darf niemals allein
aufgrund seines Vorhandenseins übernommen werden: Ohne owner-issued, generation- und
paketgebundene Consent-Entscheidung bleibt der private Anteil gesperrt; ein öffentlicher
Teilimport ist keine stillschweigende Teilübernahme. Cleanup-Fehler bleiben als
retained/unbekannt sichtbar, statt Erfolg oder garantierte Löschung zu behaupten.

## 2. Identität und Merge

### Fundstellenidentität

Die Identität einer Threadbeobachtung ist:

```text
Quelle + Board + Thread
```

Ein Post entsteht erst mit:

```text
Quelle + Board + Thread + Post
```

Ein Anhang ist erst mit seiner **Vorkommnisidentität** vollständig beschrieben:

```text
Quelle + Board + Thread + Post + Attachment-Occurrence
```

Dabei gehört zur Attachment-Occurrence mindestens die Position im Post und der belegte
Anhangs-/Revisionskontext, insbesondere Variante, Medien-ID, URL/Dateiname, Position und
Revisionsschlüssel. Der Payload-Hash ist eine Bytebindung, kein Ersatz für die
Fundstellenidentität. Deshalb gilt:

- Gleiche Bytes in zwei Posts sind zwei Vorkommnisse, keine automatisch verschmolzene
  Beobachtung.
- Gleiches Vorkommnis mit anderen Bytes ist ein Konflikt, kein automatischer Update.
- Ein fehlender Payload bleibt `unavailable` oder `omitted`; die Postbeobachtung wird
  nicht gelöscht.
- Eine historische Fassung kann dieselbe Post-/Slotadresse haben wie aktueller Inhalt;
  sie wird über ihre Revision gehalten und nicht auf eine Slotnummer reduziert.
- Reihenfolge, Antwortbeziehungen und Presence-Felder dürfen nicht aus Dateinamen,
  Zeitstempeln oder Thumbnail-Ordnern erraten werden.

### 404-Thread ergänzen

Ziel des nativen Merge ist eine verlustfreie **Ergänzung**, kein Ersetzen des Zielbestands.
Der folgende Ablauf ist der Fachvertrag; die interne Medienimplementierung akzeptiert heute
genau einen Ziel-Thread, ausschließlich gebundene Original-Attachments ohne versteckte
Payloads, und keine Preview-, `omitted`-, `unavailable`-, `failed`- oder `cancelled`-
Medien. Der interne Textpfad ist auf medienfreie Pakete beschränkt.

Beispiel:

1. Rechner A hat einen Thread als Posts 1–20 archiviert; der Thread ist inzwischen 404.
2. Rechner B hat Posts 15–30 aus einer späteren Beobachtung als natives Paket exportiert.
3. MeowChan soll auf dem Zielrechner Posts 1–30 vereinen und die exklusiven Inhalte
   beider Seiten behalten.

Wichtig ist dabei:

- Der lokale 404-/Terminalzustand bleibt autoritativ. Ein eingehendes Paket mit
  `active` belebt die Quelle nicht wieder auf und aktiviert keine Downloads.
- Lokaler Titel, erste Beobachtung, letzte Capturezeit und Sortiervertrag werden nicht
  durch das neuere Paket überschrieben.
- Neue Posts und sicher gebundene neue Medienfassungen werden additiv hinzugefügt.
- Bereits verschwundene, aber historisch gebundene Medien bleiben als `retainedBinding`
  erhalten.
- Widersprüchliche Metadaten werden als Konflikt berichtet, während eindeutige Ergänzungen
  weiterhin möglich sind.
- Der Merge kann nur Inhalte zusammenführen, die in den Paketen tatsächlich vorhanden
  sind. Er sucht nicht im Netz nach fehlenden alten Posts.

Die interne Vorbereitung für diesen Weg existiert, ist aber noch kein freigegebener
Benutzerworkflow mit vollständiger Vorschau, Zustimmung, Recovery und Konfliktauflösung.

## 3. Konfliktvarianten

MeowChan überschreibt nie still mit dem „neueren Export“. Die folgende Tabelle ist der
verbindliche Zielvertrag für einen späteren bedienbaren Import. Die heute implementierte
native „Preview“ ist dagegen nur ein paketinternes Inventar; interne Vergleichs-/Merge-
analysen können Konfliktkandidaten erzeugen, aber noch nicht als Nutzerworkflow angeboten
oder aufgelöst werden. Ein späterer Import erzeugt zuerst eine Vorschau mit mindestens
diesen Ergebnissen:

| Ergebnis | Bedeutung | Behandlung |
| --- | --- | --- |
| **Add** | Zielidentität existiert noch nicht | kann nach Bestätigung additiv übernommen werden |
| **Identical** | Wert und Bindung stimmen exakt überein | keine Änderung, keine neue Duplikatfundstelle |
| **Missing target** | Paket verweist auf Thread, Post, Medium oder private Kategorie, die im Ziel nicht existiert | keine Erfindung ohne separate Zielentscheidung |
| **Public conflict** | Titel/URL/Quellzustand/Sortierung, Posttext, Presence, Deleted-Marker, Anhangsdeskriptor oder gebundene Bytes widersprechen dem Ziel | Konfliktvariante behalten, Zielwert bleibt zunächst aktiv |
| **Unavailable/missing payload** | Beobachtung existiert, Bytes fehlen oder können nicht gemessen werden | Metadaten können erhalten bleiben, Bytes werden nicht vorgetäuscht |
| **Unsupported/rejected** | unbekanntes Format, Schema, Identität, ZIP-Pfad oder Feature | vollständige Ablehnung; kein best-effort-Import |
| **Stale/recovery required** | Ziel oder Vorgang hat sich zwischen Vorschau und Übernahme verändert | neue Vorschau oder Recovery; kein Schreiben mit altem Plan |

Bei öffentlichen Text- und Medienkonflikten bleibt das Originalpaket unverändert. Der
Konfliktkandidat wird als nicht aktive Variante neben dem Ziel geführt oder als
entsprechender Konfliktbefund gemeldet. „Ziel behalten + Konflikt berichten“ ist der
Normalfall. Eine explizite spätere Nutzerentscheidung darf den Konflikt auflösen, aber
keinen automatischen Quelle-Wahrheits-Gewinner erzeugen.

## 4. Private Daten

Private Daten sind standardmäßig **nicht** Bestandteil eines Exports. Sie dürfen nur
kategorieweise ausdrücklich gewählt werden:

- Threadfavoriten,
- Medienfavoriten,
- Postlesezeichen mit Notizen.

Die Aufnahme erzeugt `private.json` mit Schema `meowchan-private-v1` und Manifestversion 2.
Nicht gewählte Kategorien werden nicht gelesen und nicht als leere Auswahl behauptet.

Zusätzliche Regeln:

- Private Tags, Zugangsdaten, Quellenplugins, Grants, Watches, Schedulerzustände,
  Konfigurationen oder lokale Profile werden nicht in diesem Abschnitt transportiert.
- Medienbytes werden durch privates Metadata nicht anonymisiert. Eingebettete
  Metadaten können erhalten bleiben.
- Der interne passive private Vergleich kann `Add`, `Identical`, `Conflict` oder
  `MissingTarget` erzeugen; er ist keine Benutzeroberfläche, Zustimmung oder
  Applyautorität. Notizen mit gleichem Schlüssel und anderem Text sind ein echter
  Konflikt, kein Last-Write-Wins.
- Medienfavoriten müssen auf tatsächlich enthaltene öffentliche Vorkommnisse im
  Zielpaket/-katalog zeigen.
- Der aktuelle native Eingangsstadium-Vertrag lehnt Pakete mit `private.json` bewusst
  ab, solange die finale private Zustimmungs-/Apply-/Recoverystrecke nicht freigegeben
  ist. Codec und passiver Vergleich allein sind keine Übernahmegenehmigung.

## 5. Begrenzte Fremdprofile — Stand und Zielverträge

Alle folgenden Profile sind **passiv und lokal**. Sie starten kein Fremdprogramm,
rufen keine Website auf, laden keine Config und übernehmen keine Credentials, Jobs oder
Berechtigungen. Ein Profil wird außerhalb einer Fremddatei ausdrücklich vom Nutzer
gewählt; JSON-Form allein beweist weder Erzeuger noch Version.

Heute implementiert sind nur passive interne In-memory-Nähte ohne Dateisystem-, Medien-
oder Storeimport: `ThreadArchiveRecognition` erkennt 4chan-API-JSON als `Recognized` und
KurobaEx-/YChanEx-Strukturen als bekannt, aber als Import unsupported. Zusätzlich gibt es
reine Metadateninspektoren für explizit übergebene 4chan-API-JSON-Bytes (`FourChanJsonArchiveParser`)
und für ein explizit gewähltes, enges YChanEx-Profil (`YChanExArchiveParser`). Sie erzeugen
keine URL-Autorität, öffnen keinen Medienroot und führen nicht in Staging, Store oder Merge.
Der `PassiveSidecarCodec` parst exakte Hydrus- und gallery-dl-Danbooru-Tag-JSON im Speicher
und kann für Hydrus explizit gewählte Tags serialisieren; er findet oder schreibt keine
Medien-/Sidecar-Dateipaare. Erkennung, 4chan-Inspektor und Sidecarcodec verwenden maximal
1 MiB sowie JSON-Tiefe 8; der YChanEx-Inspektor verwendet 1 MiB und JSON-Tiefe 16. Die
Nähte begrenzen 4.096 Post-/Attachment- bzw. Tag-Claims, wobei ein Tag höchstens 4.096
UTF-8-Bytes lang ist. Dashchan und Grabber haben keinen Erkennungs- oder Importcode.

### Zielablauf, nicht heutiger Produktablauf

1. Nutzer wählt die exakte Datei, ZIP, HTML- oder Medien+Sidecar-Paarung aus.
2. MeowChan liest begrenzt, misst selbst Länge/SHA-256 und prüft Pfade, Duplikate,
   Unicode, Schema und Budgets.
3. Es entsteht ein inerter Kandidaten- und Verlustplan mit Post-, Anhang- und
   Medienclaims.
4. Herkunft, Rolle und fehlende Felder bleiben sichtbar; nichts wird aus Namen oder
   Ordnern geraten.
5. Erst eine Vorschau und ausdrückliche Bestätigung dürfen den Kandidaten an den nativen
   Staging-/Mergepfad übergeben.
6. Netzbeschaffung ist ausdrücklich kein Teil dieser Importprofile.

### KurobaEx JSON-ZIP

**Status: known-but-unsupported.** Die heutige In-memory-Erkennung kann eine
KurobaEx-artige Root-JSON-Struktur als bekannt einordnen, aber weder ein KurobaEx-ZIP
öffnen noch einen KurobaEx-Thread importieren.

Zielprofil:

- genau ein explizit ausgewähltes KurobaEx-Thread-ZIP,
- begrenzte ZIP-Inventur mit genau einem `thread_data.json`,
- sichere flache Eintragsnamen, ohne Traversal-, Symlink-, Duplikat- oder Case-Sonderwege,
- zuerst reine Post-/Anhangsclaims; Medienbindungen erst in einem separaten Schritt,
- erstes semantisches Profil ohne `postSubNo > 0`-Kollabierung.

Nicht übernommen werden: Kuroba-Backups, Settings, Datenbanken, Cache-Semantik,
Site-Mappings, Profilzustände oder ein Rückexport nach KurobaEx. Ein fehlendes oder
nicht referenziertes Cache-Bild wird nicht durch Dateinamensähnlichkeit repariert.

### YChanEx Thread-JSON und Medienroot

**Status: metadata-only Inspector vorhanden; Import known-but-unsupported.** Der enge
`YChanExArchiveParser` kann explizit übergebene Thread-JSON-Bytes mit festem Profil auf
Identität, Textpräsenz, Anhangsnamen und feste Verlustklassen prüfen. Zeit-, Dimensions-
und URLwerte werden bewusst weggelassen; `*.thread.json` und Medienroot werden heute nicht
geöffnet, gepaart oder gemessen.

Zielprofil:

- eine explizit ausgewählte `*.thread.json`,
- dazu ein getrennt explizit gewählter Medienroot,
- exakte DataContract-Wireform für Zeiten, Größen und optionale Felder,
- Bindung ausschließlich über die belegten `saved_file`- und
  `saved_thumbnail_file`-Referenzen.

YChanEx-eigenes Nachladen oder Neustarten eines Downloads wird nie aufgerufen. Queue-,
Threadstate-, Config- oder Jobrestore sind ausgeschlossen. Aus Anzeigenamen oder
`original_file_name` wird kein neuer Pfad berechnet.

### Dashchan HTML-Archiv

**Status: unsupported.**

Zielprofil ist ein eng gepinnter Dashchan-HTML-Dialekt mit gleichnamigen `src`- und
`thumb`-Ressourcenwurzeln. MeowChan würde die Datenattribute inert auswerten:

- ohne WebView, Browserengine, CSS, JavaScript oder Netz,
- nur sichere relative Dateibindungen,
- Post- und Anhangspositionen aus dem belegten Dialekt,
- Rolle `unknown-local-file`, solange Original/Thumbnail nicht zweifelsfrei belegt sind.

Beliebiges Wakaba-/Futaba-HTML, Browser „Seite speichern“, externe Icons, Zitate oder
nachladbare Links gehören nicht zum Profil. Ein `src`-Attribut allein beweist nicht,
dass die Originaldatei vorhanden ist.

### 4chan API JSON und BASC-Layout

**Status: metadata-only Inspector vorhanden; kein Import.** Der aktuelle Inspektor braucht
explizit übergebene JSON-Bytes und ein explizit gewähltes Board, bewertet nur Metadaten-
claims und beschafft keine Medien.

Zielprofil:

- explizit übergebene JSON-Bytes,
- explizit gewählte Quelle `4chan` und Board,
- genau ein OP und konsistente `resto`-Beziehungen,
- Post-IDs, Zeiten, Presence-Felder und Attachment-Claims als Metadatenplan,
- keine URL-Konstruktion aus `tim`/`filename`, kein 4cdn-Abruf, kein API-Client.

Ein BASC-Ordnerlayout ist ein separater möglicher Medienlayoutadapter und gilt nicht
automatisch für jedes API-JSON. Board oder Thread werden nicht aus einem Dateinamen
erraten. `archived`, fehlende Felder oder ein alter Dump beweisen keinen aktuellen
Livezustand.

### gallery-dl Danbooru-Sidecar

**Status: enger In-memory-Codec implementiert; kein Datei-/Medienpaar-Import.**

Zielprofil:

- ausdrücklich als Danbooru-Extractor-Sidecar deklarierte Datei,
- exakt `{"id":<positive-ganzzahl bis 2^53-1>,"tags":[...]}`,
- Medienname exakt `medium.ext.json` neben `medium.ext`,
- `id` ist eine opake Claim-ID, keine URL oder globale Postidentität,
- Tags bleiben exakte Fremd-/Quellmetadaten und werden nicht automatisch private
  MeowChan-Tags.

JSONL, ungefilterte Dumpobjekte, `info.json`, Config, ID-Archive, Joblisten oder ein
Offline-Rückimport in gallery-dl sind ausgeschlossen. Es wird keine Website, Danbooru-
Instanz oder Originalgarantie aus der ID abgeleitet.

### Hydrus JSON-Tags-Sidecar

**Status: enger In-memory-Codec implementiert; kein Hydrus-Client/API/Paar-Import.**

Zielprofil:

- explizit gewähltes Paar aus `medium.ext` und `medium.ext.json`,
- exakt `{"metadata":{"tags":[...]}}`,
- Tags werden originalgetreu einschließlich Namespace, Unterstrichen, Unicode,
  Reihenfolge und Duplikaten gelesen,
- Medien und Sidecar werden lokal gemessen; Hashclaims im Fremdjson sind kein Ersatz.

Hydrus-Client-API, Notizen, URLs, Ratings, Tagbeziehungen, Dienstzustände, Router-
Konfigurationen oder TXT-Formate sind nicht Teil des Profils. Der implementierte Codec
schreibt nur deterministische UTF-8-Bytes für ausdrücklich gewählte Tags in den Speicher;
Medienkopie, frischer Zielordner und Vermeidung bestehender Sidecars sind Zielvertrag
eines späteren Stagingexports, kein heutiger Datei- oder Ordnerlauf.

### Imgbrd-Grabber

**Status: unsupported/fail-closed.**

Grabber ist deswegen kein allgemeines Importformat:

- es gibt kein hinreichend belegtes, versionsstabiles generisches Grabber-JSON-,
  CSV- oder XMP-Austauschschema,
- konfigurierbare Textlogs und Dateinamen-Tokens können Anhängen, Escaping,
  Konvertierung und mehrdeutige Paarbindungen erzeugen,
- XMP bräuchte einen exakten Namespace-, Array- und Escapevertrag,
- Grabber-Profile, Queues, IGL/DLC-Jobs, Scripts und Zugangsdaten sind kein
  MeowChan-Importziel.

Bis ein konkretes TXT- oder XMP-Dialektprofil belegt und geprüft ist, bleiben unbekannte
Grabberausgaben `unsupported`. MeowChan erfindet keine Kompatibilität und bezeichnet sich
deswegen nicht bereits als Grabber-Ersatz.

## 6. Unbekannte Formate

In den implementierten Nähten werden unbekannte ZIP-, TAR-, HTML-, JSON-, Datenbank-,
Config- oder Sidecarformen abgelehnt, nicht heuristisch gedeutet. Es gibt keine allgemeine
Dateierkennung, die automatisch zwischen diesen Formaten wählt. Insbesondere werden nicht
übernommen:

- fremde Datenbanken und Profile,
- Zugangsdaten, Cookies, Tokens oder Proxykonfigurationen,
- Watch-/Downloadqueues und Schedulerzustände,
- kompilierte Plugins oder Skripte,
- URLs mit eingebetteten Zugangsdaten oder signierten/Bearer-Querybestandteilen,
- Seiteninhalte oder Medien, die erst im Internet nachbeschafft werden müssten.

Ein zukünftiger Import darf immer nur den zuvor begrenzt geprüften Bestand betreffen.
