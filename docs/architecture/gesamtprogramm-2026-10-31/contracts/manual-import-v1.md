# Manueller Import eigener Dateien (manual-import-v1, Phase 1)

Stand 13.11.2026, Branch `feature/manual-import-core` auf `integration/ux-20261113` (950609ac). Vom Nutzer
freigegeben als 1.2-Feature („alle Ideen umsetzen“) mit eigenem Importkatalog. Grundlage:
`.claude/coordination/MANUELLER-IMPORT-MIT-TAGS-ENTWURF-2026-11-12.md` (Variante B) und Abschnitt 4 von
`.claude/coordination/UX-ENTDECKBARKEIT-UND-INTERAKTIONEN-2026-11-12.md`. Abweichungen vom Entwurf sind unten
ausdrücklich als solche markiert.

## 1. Umfang

- Nutzer holen eigene Bilder, GIFs und Videos (optional PDF/ZIP/Audio als Datei ohne Vorschau) in die
  Medienverwaltung: über „⋯ → Dateien importieren…“ oder durch Ablegen von Dateien/Ordnern auf dem
  Hauptfenster (D1-Ablage: Dateien öffnen jetzt den Importdialog statt abgelehnt zu werden).
- **Nur Kopieren.** Quelldateien werden ausschließlich gelesen: nie verschoben, umbenannt, verändert
  oder gelöscht. Verschieben, Ordner beobachten und Referenzieren am Ort sind nicht Teil von v1.
- Phase 1 enthält Katalog, Prüfung, Kopie, Medienindex/Galerie, Taggen über den Lose-Dateien-Pfad,
  den Dialog und die Drop-Weiche. Stapelleiste auf der Medienseite, Drag-out, Stapel löschen/umbenennen
  und die Anzeige von Grenzen in der Einstellungsoberfläche folgen in Phase 2 (§10).

## 2. Identität und Dateilayout

- Ein **Stapel** (Batch) ist eine Importoperation mit zufälliger `BatchId` (GUID, 32 Kleinbuchstaben-Hex).
  Er leiht sich wie Danbooru-Medien die `ResourceId`-Form: `ResourceId("meowchan-import", "import",
  <BatchId>)`. Diese Identität steht **nie** im Archivkatalog; es gibt keine Fake-Threads.
- Dateien liegen unter `<Downloadordner>/meowchan-import/<BatchId>/`, als normale Manifest-Assets
  (`.meowchan-manifest.json`, Manifest v4; das Format ändert sich nicht).
- Jede kopierte Datei ist ein **Item** mit zufälliger `ItemId`. Angeforderter Dateiname =
  `<ItemId><endung>` (Endung klein), Media-Identifier = `<ItemId>`, Variante `original`.
  Der Originalname steht nur als `OriginalFileName` (Anzeige) im Manifest, nie im Pfad.
- `SourceUrl` im Manifest ist der feste neutrale Wert `meowchan-import:local-file`: kein Pfad, kein
  `file:`-URI.
- Kollisionssicher: Liegt am Ziel schon eine fremde Datei, wird sie nie überschrieben; die Kopie erhält
  den bekannten Konfliktnamen `<name>.<sha256>[.<n>]<endung>`. Ein erneuter Versuch desselben Items mit
  denselben Bytes verwendet die veröffentlichte Datei wieder.
- **Abweichung vom Entwurf:** Der Entwurf wollte den Ordner aus Downloadwurzel + `BatchId` berechnen. Der
  Katalog speichert stattdessen den absoluten Stapelordner (wie Archiv-Threads und Danbooru-Originale), damit
  ein späterer Wechsel des Downloadordners bestehende Importe nicht unsichtbar macht. Der Downloadordner
  steht ohnehin in `config.json`; Quellpfade werden weiterhin nie gespeichert.

## 3. Importkatalog `local-imports.json` (Version 1)

Eigene Profildatei, getrennt von Archiv, Booru-Katalog und Tagspeicher. 1.1.0 kennt sie nicht und
ignoriert sie; nichts Bestehendes wird umgeschrieben.

```json
{
  "Version": 1, "Revision": 3,
  "Batches": [{
    "Id": "<32 hex>", "Name": "Import 2026-11-13 10:05",
    "CreatedAtUtc": "2026-11-13T09:05:00.0000000+00:00", "CompletedAtUtc": "…" | null,
    "State": "Copying" | "Completed", "Directory": "/abs/Downloads/MeowChan/meowchan-import/<id>",
    "SourceLabel": "Urlaub" | null, "OtherFiles": false, "Cancelled": false,
    "Tags": ["urlaub"], "TagsApplied": true,
    "Items": [{ "Id": "<32 hex>", "State": "Planned" | "Committed" | "Duplicate" | "Failed",
                "Sha256": "<64 hex klein>", "Length": 123, "Name": "IMG_0042.jpg",
                "Kind": "Image" | "Video" | "Other", "Extension": ".jpg",
                "AssetId": "<64 hex>" | null, "Failure": null | "changed" | "unreadable" | "no-space" | "store" | "interrupted" }]
  }]
}
```

Regeln:

- **Strikt:** genau die bekannten Felder, jedes genau einmal; unbekannte, fehlende oder doppelte Felder,
  Großbuchstaben-Hex, leere oder nicht kanonische IDs, Zeiten ohne UTC, unbekannte Zustände,
  `Kind`/Endung-Widersprüche und Zustandswidersprüche (z. B. `Committed` ohne `AssetId`, `Planned` in
  einem abgeschlossenen Stapel, `Completed` ohne `CompletedAtUtc`) machen die **ganze Datei** ungültig.
- **Versionierung:** `Version` > 1 = von einem neueren Programm geschrieben: wird gelesen als „neuer“,
  nie überschrieben, Importe gesperrt. Gleiches gilt für eine neuere `.bak`. Eine beschädigte
  Primärdatei wird nie überschrieben (Import gesperrt, Hinweis auf `local-imports.json.bak`); eine
  beschädigte `.bak` darf beim nächsten gültigen Schreiben ersetzt werden.
- **Schreiben** nur unter der exakten Profil-Lease mit Store-Writer-Admission, frisch gelesen, mit
  erwarteter Revision (sonst `Conflict`), atomar über `AtomicJsonFile` (Temp-Datei, Flush, Ersatz,
  vorherige Fassung als `.bak`). Ein Fehler nach dem Ersatz wird als bereits veröffentlicht erkannt, nie
  doppelt angewendet.
- Nicht unterstützte, nicht lesbare, zu große oder übersprungene Dateien werden **nicht** gespeichert,
  nur im Dialog gemeldet. `Duplicate`-Items verweisen nur auf vorhandene Bytes (SHA-256, Länge, Name).
- `Tags` sind Tagnamen (gültige `PrivateTagName`, eindeutig nach Vergleichsschlüssel, höchstens 32);
  sie werden nach dem Kopieren als eigene Tags vergeben, `TagsApplied` wird danach wahr.

## 4. Auswahl, Dateiarten und Grenzen

- Auswahl: Dateien und Ordner; Ordner rekursiv (abschaltbar „Unterordner einbeziehen“). Der Durchlauf
  folgt **keinen** Symlinks/Junctions (Ebene für Ebene, Reparse Points werden übersprungen), überspringt
  versteckte/Systemeinträge und Punkt-Namen (darunter MeowChans `.meowchan-*`-Metadaten) und betritt den
  Downloadordner nie; eine ausdrücklich gewählte Datei darin wird als „übersprungen“ gemeldet.
- Dateiart nach Endung (`SavedMediaTypes.Classify`): Bild (png/jpg/jpeg/gif/bmp/webp) und Video
  (webm/mp4/mov/mkv/avi) immer; PDF/ZIP/Audio nur mit „Auch andere Dateien“ (Vorgabe aus), als Datei-
  Kachel ohne Vorschau; alles andere „nicht unterstützt“.
- **Jede Grenze ist eine Einstellung** (`config.json`, validiert; die Stores erzwingen nur technische
  Obergrenzen):

  | Einstellung | Vorgabe | Bereich | Wirkung |
  | --- | --- | --- | --- |
  | `ImportMaxFilesPerBatch` | 2 000 | 1–20 000 | importierbare Dateien je Stapel; Überschreitung wird **vor** dem Hashen gemeldet |
  | `ImportMaxBatchGiB` | 50 | 1–16 384 | Gesamtgröße je Stapel; vor dem Hashen geprüft |
  | `ImportMaxFileMiB` | 4 096 | 1–1 048 576 | Einzeldatei; größere werden „zu groß“ ausgelassen |
  | `ImportMaxTotalFiles` | 50 000 | 1–500 000 | Items über alle Stapel; stoppt nur Wachstum |
  | `ImportCatalogLimitMiB` | 32 | 1–128 | Schreibgrenze der Katalogdatei; Lesen bis 128 MiB |
  | `MinimumFreeSpaceGiB` (vorhanden) | 1 | ≥ 0 | Reserve: freier Platz muss neue Bytes + Reserve decken |

  Technische Obergrenzen: 20 000 Items je Stapel, 500 000 Items gesamt, 100 000 Stapel, Stapelname
  200 Zeichen, Dateiname 255 Zeichen, 32 Tags je Stapel.

## 5. Prüfung und Duplikate

- Die Prüfung liest nur. Jede importierbare Datei wird einmal vollständig mit SHA-256 gehasht
  (Fortschritt in Dateien und Bytes, jederzeit abbrechbar). Die gemessene Länge ersetzt die gelistete.
- Duplikate **innerhalb der Auswahl** (gleiche Bytes): nur die erste wird kopiert, die weiteren werden
  als `Duplicate` verknüpft.
- Duplikate **gegen die Bibliothek**: Abgleich gegen einen frisch gebauten Medienindex
  (`MediaLibraryIndexBuilder.BuildAsync`, mit dem vorhandenen Snapshot oder Cache als
  Wiederverwendungsbasis): archivierte Threads, Danbooru-Originale und frühere Importe mit
  `Presence = Present`. Vorhandene Bytes werden **gezeigt und verknüpft statt kopiert**.
- Ist der Index unvollständig, sagt der Dialog „Duplikatprüfung unvollständig“; nicht gefundene Dateien
  gelten als neu (lieber eine Kopie zu viel als eine stille Auslassung).
- Stapelinfo (Herkunft, Stapel) ist **Metadatum und Filter, kein Tag**: kein automatischer
  `import:manual`/`batch:*`-Tag, damit „ohne eigene Tags“ weiter funktioniert. Stapeltags werden **nicht**
  automatisch auf schon vorhandene Duplikate übertragen.

## 6. Ablauf, Fehler und Atomizität

Reihenfolge je Stapel (ein zugelassener interaktiver Host-Befehl):

1. Unterbrochene Stapel (§6.3) werden zuerst abgeschlossen; danach Kapazität (`ImportMaxTotalFiles`) und
   freier Platz (neue Bytes + Reserve) geprüft. Scheitert das, wird **nichts** geschrieben.
2. Katalog „Plan“: Stapel `Copying`, alle neuen Items `Planned`, Duplikate `Duplicate`, bevor ein Byte
   kopiert wird.
3. Je neue Datei `MediaStore.CommitLocalFileAsync`: CreateNew-`.part`, Hash beim Kopieren, Erwartung
   = SHA-256 und Länge aus der Prüfung, Flush, Verzeichnislease, CreateNew-Verschieben, atomarer
   Manifestersatz. Ein beschädigtes Manifest wird nie ersetzt. Fortschritt wird gebündelt in den
   Katalog geschrieben (alle 50 Dateien oder 2 s).
4. Katalog „Abschluss“: `Completed`, `CompletedAtUtc`, Endzustände; nicht abbrechbar.
5. Stapeltags (§8), danach Katalog `TagsApplied = true`.

### 6.1 Einzelfehler

Eine geänderte Quelle (andere Bytes/Länge, inzwischen Link/Ordner) → Item `Failed/changed`, der Rest
läuft weiter. Nicht lesbar → `unreadable`; Platte voll → `no-space`, danach wird keine weitere Datei
begonnen; Schreibfehler des Ordners → `store`. Keine Teil- oder `.part`-Datei bleibt sichtbar.

### 6.2 Abbruch

„Abbrechen“ heißt „nach der aktuellen Datei anhalten“: die laufende Kopie wird verworfen, wenn sie noch
nicht veröffentlicht ist, keine weitere begonnen; Fertiges bleibt, nie begonnene Items fallen aus dem
Stapel, der Stapel wird `Completed` mit `Cancelled = true`. Abbruch vor dem Planschritt schreibt nichts.

### 6.3 Unterbrechung

Ein Stopp zwischen 2 und 4 hinterlässt einen Stapel `Copying`. Vor dem nächsten Import wird er „so
abgeschlossen“: ein `Planned`-Item, dessen Datei das Stapelmanifest mit genau seiner Media-ID, seinem
angeforderten Namen, Länge und SHA-256 nennt, wird `Committed`; jedes andere `Failed/interrupted`. Kein
Raten aus Dateinamen, kein Kopieren, kein Löschen; idempotent. Ein unlesbares Manifest lässt den Stapel
für einen späteren Versuch liegen. „Fortsetzen“ mit erneutem Lesen der Quelle ist Phase 2.

### 6.4 Tags nicht verfügbar

Scheitert der Tag-Schreibvorgang (Tagspeicher nicht verfügbar, Grenze erreicht, Prüfung fehlgeschlagen),
bleiben die Dateien importiert; der Dialog sagt „ohne Tags importiert“, der Stapel behält
`TagsApplied = false` (Phase 2: „Stapeltags erneut anwenden“).

## 7. Medienindex, Galerie, Cache

- `ManualImportMediaProjection` erzeugt je `Committed`-Item einen Indexeintrag unter der geliehenen
  Stapelidentität: Titel = Stapelname, `Unassigned = true`, `LocalSlot` = angeforderter Name,
  neues Feld `ImportItemId`. Fehlende Datei oder unlesbares Manifest → `Missing`, nie erfunden.
  Duplikate haben keinen eigenen Eintrag; die SHA-Gruppierung zeigt ihre vorhandenen Vorkommen.
- `MediaLibraryIndexBuilder.BuildAsync(readImportCatalog: …)` leitet Importe bei jedem Aufbau neu ab; ein
  unlesbarer Katalog behält die Importeinträge des vorherigen Index (Index unvollständig).
- Galerieauflösung über `ManualImportMediaGallery.ResolveAsync` (Katalog + Manifest + Bytes stimmen
  überein, sonst ausgelassen).
- Cache `media-index-v1.json` Version 3 → 4 (Feld `Im`); ältere/neuere Caches werden verworfen.
- Medienseite: Quelle „Eigene Importe“, Kachel „Import · Stapelname“, „Nur diesen Thread“ wirkt als
  „nur dieser Stapel“; „Stapel anzeigen“ im Dialog setzt genau diesen Filter.

## 8. Taggen importierter Dateien

**Abweichung vom Entwurf (freigegeben im Auftrag):** kein Tagspeicher v3. Importierte Dateien sind
**lose lokale Dateien** im bestehenden v2-Pfad (`contracts/local-media-tags-v2.md`): Vorkommen-Locator =
(Stapelidentität, Stapelordner, angeforderter Name), Revision = Manifest-Asset. `MediaTagSubjects.Of`
liefert für Index- und Galerieeinträge ein `MediaTagSubject.Local`; Mehrfachauswahl-Tagdialog,
Galerie-Tagleiste und Tag-Manager funktionieren unverändert.

- Die Prüfung vor jedem Hinzufügen (`LocalMediaTagRevalidation.CheckAsync`) erkennt Import-Locators und
  prüft gegen **Importkatalog und Stapelmanifest statt Archiv**: Stapel vorhanden, Ordner gleich, Item
  `Committed` mit gleicher Asset-ID, Manifest-Datensatz mit gleichem Namen/Media-ID/Länge/SHA, Bytes
  hashen gleich. Katalog unlesbar → `Unreadable` (nichts geschrieben); Stapel/Item fehlt → `Missing`.
  Die Waisenprüfung nutzt denselben Weg.
- Die Stapeltags werden je Tagname in einem `DefineAndSetMediaSubjectTags`-Schreibvorgang für alle
  kopierten Dateien vergeben (alles oder nichts je Tag).
- Folge für den Rückweg auf 1.1.0: dort sind Importe unsichtbar; ihre Tagzuordnungen bleiben im
  v2-Format erhalten, erscheinen in 1.1.0s Prüfung „getaggte lose Dateien“ aber als Waisen (Archivthread
  fehlt). Sie werden nur auf ausdrücklichen Nutzerwunsch entfernt; nichts wird automatisch gelöscht.

## 9. Datenschutz

- Gespeichert werden nur Dateiname (Anzeige), optional der Name **eines** gewählten Ordners
  (`SourceLabel`), Größe, SHA-256 und Art. Quellpfade bleiben im Speicher des Dialogs, werden nie
  geschrieben, geloggt oder in Diagnosen übernommen.
- Fehlermeldungen und Diagnosen nennen Codes und Zahlen, nie Pfade oder Dateinamen.
- Profil-Backup muss `local-imports.json` und `<Downloadordner>/meowchan-import/` enthalten (Hilfe-
  Ergänzung, Phase 2).

## 10. Phase 2 (nicht in diesem Schnitt)

Stapelleiste auf der Medienseite (Name · Zahl · ohne Tags · Taggen/Umbenennen/Löschen…, Banner für
unterbrochene Stapel), Stapel löschen (write-ahead `Deleting`, Tagbereinigung, Ordner-Reclaim) und
umbenennen, „Stapeltags erneut anwenden“, „Fortsetzen“ unterbrochener Stapel, Ergebnisaktionen
„Ungetaggte dieses Stapels taggen“ / „Vorhandene anzeigen“ / „Tags auf N vorhandene anwenden…“,
Kachel-Drag-out (D2), Import-Einstieg auf der Medienseite und im Leerzustand, Befehl im Katalog,
Grenzen in der Einstellungsoberfläche, Reservierung der Quellen-ID `meowchan-import` in der
Plugin-Prüfung, Favoritenliste-Auflösung importierter Dateien, Hilfe/CHANGELOG/Backup-Hinweis,
native Drop-Prüfung auf Windows/macOS/Wayland.
