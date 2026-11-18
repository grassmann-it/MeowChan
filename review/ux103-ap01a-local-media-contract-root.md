# AP-01a — Zielidentität für Tags auf lokalen Medien ohne Beitragsbindung

Stand: 11.11.2026, Root-Entwurf. Der dafür vorgesehene Terra-Worker (AP-01a) konnte
wegen `native_config_source_trust_required` nicht starten; dieser Entwurf entsteht
deshalb am Root und bleibt **vorläufig**, bis eine unabhängige Gegenprüfung oder eine
ausdrückliche Nutzerentscheidung ihn bestätigt. Nichts davon ist implementiert.

## 1. Bestätigte Anforderung und heutiger Stand

Nutzerbestätigung (11.11.): Rechtsklick auf ein Bild/Medium soll überall dieselben
Medienaktionen inklusive **Medientags** anbieten, und **auch lose/alte lokale Medien
ohne Beitragsbindung müssen tatsächlich taggbar sein**. Ein gesperrter Eintrag gilt
nicht als Erfüllung.

Implementierter Baselinevertrag `docs/architecture/gesamtprogramm-2026-10-31/contracts/private-tag-store-v1.md`:
Medientags hängen an `PrivateMediaTagTarget` = exakte Attachment-Fassung
(`AttachmentRevisionReference`: Quelle, Board, Thread, Post, Position, `att-v1|att-v2:`-
Revisionsschlüssel) plus Medien-ID, Variante, Rolle, SHA-256. `GalleryMediaTagTarget.Resolve`
und `MediaLibraryFilter.TagTarget` liefern `null`, sobald `AttachmentResolution` fehlt,
legacy ist oder `PostId`/`Position`/`AttachmentRevisionKey` fehlen. Genau dieser Pfad
ist die Sperre; der am 11.11. committete Archiv-Einstieg (30e6d5d) deckt nur gebundene
Medien ab.

## 2. Welche Identitäten existieren wirklich (Quellbefund)

| Kandidat | Persistiert? | Geeignet? |
| --- | --- | --- |
| `FavoriteAssetKey(Thread, MediaIdentifier, Variant, Sha256)` | ja, `FavoriteStore` | **Empfohlene Grundlage.** Funktioniert heute schon für ungebundene/legacy Dateien (`MediaLibraryFilter.AssetKey` verlangt nur `Presence==Present`, `MediaId`, `Sha256`). |
| `GalleryAssetKey(Thread, Directory, AssetId)` | Manifest-Verzeichnis + Asset-ID im Katalog | tragfähig, aber pfadabhängig; Favoriten bewusst nicht darauf gebaut. |
| `LocalMediaRevisionReference` (UUID-Tripel, `local_collections`/`local_items`/`local_media_revisions`, Schema 9) | ja, eigenes Schema | nur über den **Local-Import** gefüllt. Legacy-/Altbestand müsste erst dorthin migriert werden inkl. `payload_relative_path`-Buchführung; ein zweites Identitätssystem für denselben Zweck. |
| SHA-256 (+ Länge) allein | beobachtet, nicht identitätsstiftend | **abgelehnt**: würde unabhängige Fundstellen mit gleichen Bytes zusammenlegen. |
| Pfad allein | nein (verschiebbar, profilabhängig) | **abgelehnt**. |
| erfundener Post/Thread/Revisionsschlüssel | – | **abgelehnt**: keine Fake-Provenienz. |

`MediaLibraryEntry` (Quelle der Medienseite) trägt bereits `Thread`, `MediaId`,
`Variant`, `Sha256`, `Length`, `Unassigned`, `Historical`; `MediaAssetReference` trägt
`AssetId`, `MediaIdentifier`, `Variant`, `Length`, `Sha256`. Archiv-Tabelle/-Raster und
Galerie liefern dieselben Felder über `GalleryEntry`/`MediaAssetReference`. Damit ist
eine **einheitliche Auflösung** für alle drei Flächen möglich, ohne neue Tabellen.

## 3. Vorgeschlagener Vertrag (vorläufig)

**Ein zweiter Zieltyp neben der gebundenen Fassung, kein zweiter Store, keine zweite
Tagliste.** `PrivateMediaTagTarget` bleibt für gebundene Medien unverändert; dazu kommt
ein lokaler Zieltyp mit genau diesen Feldern:

- `Thread` (`ResourceId`) — der Medienordner, in dem die Datei liegt. **Keine**
  Threadtag-Vererbung, keine Behauptung eines Beitrags.
- `MediaIdentifier` (Manifest-Medien-ID), `Variant` (`original`/`preview`).
- `Sha256` (klein, kanonisch) und `Length` als **aufgezeichnete Bytes**, nicht als Identität.
- Kein `PostId`, keine `Position`, kein Revisionsschlüssel.

Gleichheit folgt der Record-Gleichheit dieser Felder. Zwei Kopien derselben Bytes in
zwei Threads/Ordnern bleiben zwei Ziele; dieselbe Datei bleibt in allen Ansichten
dasselbe Ziel.

**Drahtformat `private-tags.json` v2** (nur die `Media`-Einträge ändern sich):
`Post`, `Position`, `RevisionKey` werden optional; ein Eintrag ist entweder gebunden
(alle drei wohlgeformt, `att-v1|att-v2:`-Schlüssel) oder lokal (`"Local": true`, keines
der drei). `Length` kommt neu hinzu. v1-Dateien bleiben unverändert lesbar; `Version`
bleibt 1, bis mindestens ein lokaler Zieltyp geschrieben wird — dann 2 mit derselben
Rückwärtskompatibilität wie heute (unlesbare/neuere Dateien werden nie überschrieben).
Empfohlene technische Form: diskriminiertes Record statt Nullable-Felder auf dem
bestehenden Ziel, damit `TryCreate` die beiden Formen nicht vermischen kann.

**Verhalten an Grenzen (Vertragsbestandteil, nicht Implementierungsdetail):**
- Datei fehlt/verschoben: Ziel bleibt bestehen, keine Neuzuordnung, kein Raten aus Hash
  oder neuester Fassung. Host-Cleanup wie bei Favoriten/Bewertungen.
- Bytes ersetzt (neue Fassung derselben Asset-ID): alte Tags bleiben an der alten Fassung,
  genau wie heute; die neue Fassung ist ein eigenes Ziel.
- Thread endgültig gelöscht: lokale Medienzuordnungen dieses Threads werden entfernt
  (wie Favoriten), die Tags selbst bleiben.
- Zwei Offline-Neustarts: Tags bleiben sichtbar und identisch auf allen Flächen.
- Transfer: lokale Ziele werden **nicht** in ein Privatpaket übernommen (Kategorie
  „Tags“ ist weiterhin nicht implementiert); ein stiller public-only-Abfall ist zu melden.

## 4. Abnahmematrix (für AP-01b/c)

1. Ungebundenes Altbild in Archiv-Tabelle, -Raster, Medienseite und Galerie taggen →
   dieselbe Zieltasche, derselbe Dialog, echte Host-/Store-Kette.
2. Zwei Neustarts offline: Zuordnungen unverändert, `private-tags.json` nutzt v2.
3. Vorhandene v1-Datei mit gebundenen Zuordnungen: unverändert lesbar, keine Migration
   erforderlich, gebundene Tags unverändert.
4. Gleiche Bytes an zwei unabhängigen Fundstellen: getrennte Ziele, keine Zusammenlegung.
5. Threadtags und Medientags bleiben getrennt (keine Vererbung in beide Richtungen).
6. Fehlende Datei, fehlgeschriebener Write, beschädigte Store-Datei: sichtbarer Fehler,
   kein stiller Tagverlust.
7. Rechtsklick auf B bei markiertem A trifft B; Identität beim Öffnen eingefroren;
   Filter-/Rebuildwechsel lenkt nicht um.
8. Keyboard-Kontextmenütaste/Shift+F10 liefert dasselbe Menü; Textfelder behalten ihr
   natives Menü.

## 5. Offene Nutzerentscheidungen (blocken AP-01b, nicht die Menüarbeit für gebundene Medien)

1. Soll die Identität die **Datei/Fassung** (empfohlen, wie Favoriten) oder die **Bytes**
   sein? Byte-Identität würde neue Fassungen automatisch erben.
2. Sollen lokale Medienzuordnungen beim endgültigen Threadlöschen entfernt werden
   (empfohlen: ja, wie Favoriten/Bewertungen)?
3. Bestätigt die Empfehlung, lokale Ziele vorerst **nicht** zu transferieren?

Root-Empfehlung: (1) Fassung, (2) ja, (3) ja — mit der Begründung, dass Favoriten und
Bewertungen für genau diese losen Dateien bereits dieselbe Identität benutzen und ein
zweites Modell nur neue Widersprüche erzeugt.
