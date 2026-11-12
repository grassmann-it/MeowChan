# Lose lokale Medien taggen: Vorkommen-Identität und Tagspeicher v2 (AP-01b / CX-07b)

Stand 12.11.2026. Ergänzt [private-tag-store-v1](private-tag-store-v1.md) (lokal, nicht versioniert) um
Tags für lose und alte Medien. Grundlage sind die drei Nutzerentscheidungen vom 12.11.2026
(`.claude/coordination/CLAUDE-UEBERNIMMT-2026-11-12.md`), das AP-01a-Entscheidungspaket und das
CX-07a-Readiness-Review. Das gemeinsame Medienmenü (AP-01c) ist nicht Teil dieses Schnitts.

## 1. Was ein loses Medium ist

Ein Eintrag einer Galerieauswahl (`GallerySelection.Build`) ist **lose**, wenn er nicht an eine
erfasste Beitragsfassung gebunden ist: nicht zugeordnet (`Unassigned`) oder nur über den alten
Metadatenabgleich einem Beitrag zugeordnet (`AttachmentResolution.IsLegacy`). Er ist **lokal taggbar**
(`GalleryEntry.LocalTagEligible`), wenn zusätzlich

- kein Eintrag derselben Auswahl dasselbe Manifest-Asset gebunden zeigt (eine Datei mit gebundener
  Ansicht bleibt im gebundenen Namensraum; ihre Legacy-Ansicht bekommt kein zweites, lokales Ziel), und
- kein Manifest des Threads beschädigt ist (ein beschädigtes Manifest versteckt Bindungen; dann wird
  nichts als lose angeboten).

Einträge, die außerhalb der Auswahl von Hand gebaut werden, sind nie lokal taggbar. Der Medienindex
übernimmt die Markierung als `MediaLibraryEntry.LocalSlot` (angeforderter Dateiname); der Cache ist
dafür auf Version 2 gehoben, ältere oder neuere Cachedateien werden verworfen und neu aufgebaut. Der
Cache bleibt Startpunkt, nie Schreibberechtigung.

## 2. Identität (local-media-occurrence-v1)

- **Vorkommen** = ein Platz in einem Thread-Manifest: Besitzer-Thread (`ResourceId`), Medienordner
  genau so, wie der Katalog ihn nennt, und der angeforderte Dateiname des Manifest-Slots
  (`LocalMediaLocator`). **Revision** = ein in diesem Slot beobachteter, vom Manifest
  veröffentlichter Asset-Datensatz (Asset-ID, Länge, SHA-256).
- Vorkommen-ID und Revisions-ID sind zufällige, profillokale GUIDs. Bytes, Hash, Pfad und Asset-ID
  **lokalisieren oder belegen**, sind aber nie die Identität. Gleichheit eines Ziels ist ausschließlich
  (Vorkommen-ID, Revisions-ID).
- Gleiche Bytes an zwei Slots (anderer Dateiname oder anderer Ordner, auch bei identischer
  manifestlokaler Asset-ID) sind zwei Vorkommen. Kein Hash-Fan-out, keine Pfadvermutung.
- Neue Bytes im selben Slot (das Manifest veröffentlicht ein neues Asset unter demselben
  angeforderten Namen) = **neue Revision desselben Vorkommens ohne geerbte Tags**. Die alte Revision
  behält ihre Tags. Ein identischer Asset-Datensatz, den das Manifest erneut nennt, ist dieselbe
  Revision (das Manifest vertritt ihn als dieselbe veröffentlichte Datei). Unbeobachtete externe
  Ersetzungen auf der Platte werden nicht als Revision erkannt; sie fallen bei der Bytes-Prüfung als
  „geändert“ auf und blockieren jedes Hinzufügen.
- Eine Konfliktlage (registrierte Revision mit derselben Asset-ID, aber anderer Länge oder anderem
  Hash) wird abgelehnt, nie umgedeutet.
- Erneuter Import: Nach endgültigem Löschen des Besitzer-Threads sind dessen Vorkommen weg; ein
  späteres Wiederarchivieren desselben Platzes erzeugt ein unabhängiges Vorkommen. Verschieben des
  Ordners (anderer Locator) erzeugt ebenfalls ein neues Vorkommen; das alte bleibt als Waise sichtbar.
  Eine verwaltete Verschiebung mit Kontinuitätsnachweis oder ein explizites Neu-Verknüpfen gibt es in
  diesem Schnitt nicht.
- Lokale Sammlungen (`LocalMediaRevisionReference`, UUID-Tripel aus dem Import): Keine Desktop-Ansicht
  zeigt heute Sammlungseinträge, daher ist kein Tripel für ein taggbares Medium maßgeblich. Eine spätere
  Sammlungszielart muss das Tripel exakt übernehmen und ist eine neue Dokumentversion (v3).
- Die bestehende enge Bound-Key-Übergabe (`MediaTagKeyCarryOver`, gleiche Anlage und gleiche Bytes)
  bleibt unverändert und gilt nur für gebundene Ziele. Zwischen gebundenem und lokalem Namensraum wird
  nichts kopiert, gemischt oder vererbt; ein Wechsel (Datei wird später gebunden) lässt die lokale
  Zuordnung als Waise stehen.

### Warum eine eigene Registry im Tagspeicher und kein Archivschema

Gewählt: eine profileigene Vorkommen-Registry als eigener Abschnitt **im selben Dokument**
`private-tags.json` (v2). Begründung:

- **Atomarität ohne Journal:** Registrierung und erste Zuordnung sind ein einziger atomarer
  Dateiersatz. Es gibt keinen Zustand, in dem eine veröffentlichte Zuordnung eine unveröffentlichte
  Identität nennt, und keine Querschreib-Reihenfolge zwischen zwei Dateien oder Datei und SQL.
- **Kleinste Migration:** kein Archivschema-Bump (bleibt 12), keine Änderung an Lesern des Katalogs,
  kein Umschreiben von Manifesten. Ein v1-Dokument wird ohne Umschreiben gelesen.
- Das lokale Sammlungsschema (eine Revision je Item, nur Originalrolle, Payload-Buchführung) passt
  nicht auf Manifest-Slots mit Revisionsfolge; es zu erweitern hätte eine transaktionale
  Schemamigration plus Lesereinschränkungen bedeutet, ohne dass eine Ansicht davon profitiert.

Nachteil: Die Identität gehört dem Tagspeicher. Sollen später Favoriten oder Bewertungen loser
Medien dieselbe Identität nutzen, muss die Registry in einen eigenen Store ausgelagert werden (mit
dann nötigem Journal). Die Registrierung bleibt erhalten, auch wenn alle Tags eines Vorkommens
entfernt werden, damit erneutes Taggen dieselbe ID liefert.

## 3. Speicher v2 (private-tag-store-v2)

```json
{"Version":2,"Revision":9,
 "Tags":[…],"Threads":[…],"Media":[… gebundene Ziele exakt wie v1 …],
 "LocalOccurrences":[{"Id":"<guid>","Source":"…","Board":"…","Thread":"…",
   "Directory":"<Katalog-Medienordner>","RequestedFile":"<Slotname>",
   "Revisions":[{"Id":"<guid>","AssetId":"<hex64>","Length":123,"Sha256":"<hex64>"}]}],
 "LocalMedia":[{"Tag":"<guid>","Occurrence":"<guid>","Revision":"<guid>"}]}
```

- Diskriminierung über getrennte Felder: gebundene Ziele bleiben in `Media` mit unverändertem Satzbau,
  lokale Ziele stehen in `LocalMedia`. Im Code ist das Ziel `MediaTagSubject.Bound | .Local`.
- Strikt: unbekannte/doppelte/fehlende Felder, nicht kanonische GUIDs, Hex in Großbuchstaben,
  negative Länge, leere Revisionsliste, doppelte Locatoren, doppelte Asset-IDs in einem Vorkommen,
  wiederverwendete IDs und Zuordnungen auf unbekannte Tags/Vorkommen/Revisionen machen das Dokument
  ungültig. v1-Felder in v2-Form oder v2-Felder in v1-Form ebenso.
- Grenzen: 50 000 Vorkommen, 64 Revisionen je Vorkommen, 100 000 Revisionen, 50 000 lokale Zuordnungen,
  weiterhin 32 MiB Datei. Überschreitung lehnt den ganzen Schreibvorgang ab.
- Datenschutz: v2 enthält absolute Medienordner und Dateinamen. Fehlermeldungen nennen nie Pfade,
  Namen oder IDs. Die Datei verlässt das Profil nicht (Abschnitt 6).

### Migration, Klebrigkeit, Downgrade

- Ein v1-Dokument bleibt **byte-identisch**, solange kein lokaler Schreibvorgang stattfindet: Lesen,
  lokale No-ops und gebundene/Thread-Schreibvorgänge schreiben weiter v1.
- Der erste Schreibvorgang, der lokale Daten festschreibt, schreibt v2; dabei rotiert die bisherige
  v1-Datei unverändert nach `private-tags.json.bak`. Alle Tag-IDs, Namen, Thread- und gebundenen
  Medienzuordnungen bleiben in Wert und Bedeutung gleich (Hülle/Version ändern sich zwangsläufig).
- v2 bleibt v2, auch wenn die letzte lokale Zuordnung oder Registrierung entfernt wurde.
- **Ältere Programmstände lesen v2 nicht**: Sie lehnen das Dokument ab (Tags „nicht verfügbar“,
  Bereinigungsschutz „unbekannt“) und überschreiben es nicht. Ein manueller Rückweg ist, die v1-Datei
  aus `.bak` zurückzulegen — das verliert ausschließlich die lokalen Tags und alles, was nach dem
  Upgrade geändert wurde. Eine automatische Rückstufung gibt es nicht.
- Neuere (v3+) oder beschädigte Primär- **oder** Backupdateien werden nie überschrieben oder
  wegrotiert; Änderungen schlagen dann fehl.

### Schreibreihenfolge und Absturzverhalten

Jeder Schreibvorgang: Admission → Lease-Sektion → Datei frisch lesen → Revision prüfen → Änderung am
Modell (dabei ggf. Registrierung) → Backup prüfen → serialisieren → temporäre Datei, Flush → atomarer
Ersatz mit Backup. Daraus folgt:

- Abbruch, Kapazitäts-, Revisions- oder Backupfehler vor dem Ersatz: alter Stand, keine Registrierung.
- Absturz nach dem Ersatz, bevor der Aufrufer Erfolg sieht: Registrierung und Zuordnung sind beide
  da; der Aufrufer hat keinen Erfolg gemeldet. Wiederholen ist ein No-op mit derselben ID.
- Gleichzeitige erste Zuordnungen derselben Datei laufen nacheinander durch die Lease-Sektion; die
  zweite findet die Registrierung über den Locator und verwendet sie. Nie zwei IDs für einen Platz.
- Ein Dialog friert die Registrierung ein, die er sieht (`MediaTagSubject.Local.Expected`). Löst die
  Datei beim Schreiben anders auf (z. B. nach Löschen und Wiederarchivieren), wird mit `StaleTarget`
  abgelehnt statt umgeleitet.
- `DefineAndSetMediaSubjects` legt Tag und Zuordnung(en) in **einem** Schreibvorgang an; die frühere
  Zwei-Schritt-Folge von `MediaTagSession.AddAsync` (Tag angelegt, Zuordnung gescheitert) gibt es nicht
  mehr, auch nicht im Auswahl-Dialog der Medienseite.

## 4. Host und Prüfung vor dem Schreiben

- `SetMediaSubjectTagsAsync` / `DefineAndSetMediaSubjectTagsAsync`: Vor jedem **Hinzufügen** wird
  jedes lokale Ziel frisch geprüft (`LocalMediaTagRevalidation`): Thread im Katalog, Ordner unter den
  Medienordnern des Threads, Manifest lesbar und unbeschädigt, Asset-Datensatz vorhanden, Slot gleich,
  Ansicht weiterhin lose, vollständiger Hash der Datei stimmt. Das läuft im Befehlsabschnitt des
  Hosts; ein endgültiges Löschen kann nicht dazwischen laufen. Jede Abweichung lehnt den ganzen
  Schreibvorgang als `TargetUnavailable` ab. **Entfernen braucht keine Datei** — eine Waise bleibt
  entfernbar.
- Kosten: Jedes Hinzufügen hasht die betroffene Datei vollständig (bei großen Videos spürbar). Das ist
  bewusst; ein Prüf-Cache wäre keine Bytes-Prüfung.
- Restlücke: Zwischen Hash-Prüfung und Dateiersatz kann ein fremdes Programm die Datei ändern; das
  wird erst bei der nächsten Prüfung sichtbar.

## 5. Waisen, Bereinigung, Löschen

- Fehlende, verschobene, ersetzte oder inzwischen gebundene Dateien behalten ihre Zuordnungen.
  Sichtbar: in der Ansicht (solange das Manifest sie nennt) mit ihren Tags, im Tagverwalter in der
  Medienanzahl, und über „Getaggte lose Dateien prüfen“ (Existenz/Länge/Manifest/Bindung, kein Hash).
  „Diese Zuordnungen entfernen“ entfernt genau die geprüften Zuordnungen, gebunden an die geprüfte
  Revision; Tags und Registrierungen bleiben. Unlesbare Belege werden als „nicht prüfbar“ gezählt,
  nie als Waise.
- Archivbereinigung: Eine lokale Zuordnung schützt ihren Besitzer-Thread (`PrivateTag`). Die
  Schutzinventur validiert Primär- und Backupdatei mit dem kanonischen v1/v2-Parser.
- Endgültiges Löschen: entfernt Thread-, gebundene und lokale Zuordnungen sowie die lokalen
  Registrierungen der gelöschten Threads; Tagdefinitionen bleiben. Scheitert das, bleibt die Löschung
  bestehen, der Fehler steht im Ergebnis und `TagCleanupThreads` erlaubt im Löschdialog
  „Tagbereinigung wiederholen“ (idempotent).

## 6. Transfer

Lokale und gebundene private Tags sind **nicht** Teil privater Übertragung. `NativePrivateMetadataSelection`
hat bewusst kein Tag-Feld (ein Wunsch nach Tags kann nicht still fallen gelassen werden);
`NativePrivateMetadataSnapshot.Omissions` nennt `PrivateTags`, und jede Export-/Transferoberfläche muss
das vor dem Export anzeigen (`PrivateTransferDisclosure`). Heute gibt es im Desktop keine Oberfläche
für privaten Export; der Hinweis steht im Dialog „Originale exportieren“, der einzigen Medienausfuhr.

## 7. Was nicht abgedeckt ist

- **Eigenständige Dateien außerhalb jedes Thread-Manifests**: Es gibt kein unterstütztes Inventar
  (die Medienanzahl behandelt unverwaltete Dateien ausdrücklich als „unbekannt“). Ohne Entscheidung
  wird kein Dateisystem-Scanner gebaut.
- Gemeinsames Kontextmenü auf allen Flächen (AP-01c). Die Galerie hat weiterhin keinen Tag-Eintrag im
  Kontextmenü; Archivmenü, Galerie-Werkzeugleiste und Medienseiten-Dialog nehmen lose Ziele an.
- Favoriten-Galerie: baut Einträge ohne Thread-Auswahl und bietet weder gebundene noch lokale Ziele
  (unverändert).
- Verwaltete Verschiebung / explizites Neu-Verknüpfen von Waisen; Sammlungsziele (v3).
