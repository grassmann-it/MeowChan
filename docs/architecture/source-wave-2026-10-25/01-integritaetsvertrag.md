# NW-01 — Anbietergrößen, gemischte Hashes und Ausnahmeumfang

Status: Nutzer nimmt die Empfehlung ausdrücklich an (Rootweitergabe
„Empfehlung umsetzen“). **Lokal implementiert; gezielte Komponententests grün, integrierte Lieferung offen.** Größenhinweise
bleiben nichtautoritativ, SHA-1 wird strikt geprüft, Leftypol-Ausnahme nur nach
expliziter Quellen-/Profilfreigabe. Alle fünf neuen Quellen sind standardmäßig
aus/Experimental. Für 2ch wurde keine Abweichungsausnahme erteilt.
NW-05/06 benötigen nach den Stichproben keine neue Integritätssemantik.

## Ziel und Vorschlag

1. **Makaba-Größe:** Gerundete KB/KiB-Angabe nicht in ExpectedSize als exakte
   Bytezahl ablegen. Rohwert, Einheit und Präzision erhalten; tatsächliche Länge
   weiterhin lokal messen. Empfehlung: eigener optionaler Anbietergrößenhinweis
   mit Genauigkeit statt einer Ausnahme für systematisch falsche ExpectedSize.
   Exakte DTO-/Persistenzform nach APIreview festlegen. Ob KB oder KiB und welche
   Rundung gilt, zuerst anhand Betreibervertrag und weiterer Originale klären.
2. **Uboachan:** Empfehlung: algorithmusgebundene SHA-1-Erwartung additiv über
   SDK → IPC → Host → Manifest/Revision → Queueprüfung führen. Pro Anhang
   Base64 dekodieren: 16 Bytes sind bei diesem belegten Mapping MD5, 20 Bytes SHA-1;
   unbekannte/fehlerhafte Formen nicht still akzeptieren. Keine globale Regel für
   andere Anbieter aus der Feldlänge ableiten. Rohangabe und Interpretation erhalten.
   Ein dediziertes ExpectedSha1-Feld ist die kleine Variante; eine generische
   Hashliste erst bei weiterem konkretem Bedarf. SHA-1 dient dem Anbieterabgleich,
   lokale Integrität bleibt SHA256. Kein bewusster Verzicht auf Anbieterprüfung beschlossen.
3. **Leftypol:** Striktes Verhalten muss den belegten Mismatch ablehnen. Empfehlung:
   bestehende bewusste Quellen-/Profilfreigabe als optionale, sichtbare Ausnahme
   wiederverwenden, falls der Nutzer auch diese abweichenden Originale archivieren
   möchte. Diese Erweiterung des Ausnahmeumfangs ist ausdrücklich angenommen.
   Ohne sie kann der Adapter strikt arbeiten, liefert für den konkreten Anhang
   aber korrekt einen Integritätsfehler. Weder MD5 löschen noch durch Ist-MD5 ersetzen.

## Historische Vertragsanalyse vor der Umsetzung

Zum Planungsbeginn bot `MeowChan.PluginSdk/SourceContracts.cs` ExpectedSize,
ExpectedMd5 und ExpectedSha256, noch kein SHA-1 oder Größenpräzisionsfeld.
Die unten dokumentierte additive Umsetzung hat diese Lücke geschlossen.
Betroffen waren DTO-
Validierung/JSON, Hostmapping, Coremedienmodell, Anhangsrevision und Manifest,
Integritätsprüfung sowie Anzeige/Diagnose der Anbieter-/Istwerte. Vor Änderungen
alle tatsächlichen Übergänge verfolgen; kein nur im SDK vorhandenes Feld liefern.
Öffentliche API nach bestehender API-Politik dokumentieren und Consumer prüfen.
Keine neue Abweichungsausnahme für SHA256 oder lokale Istwerte.

## Abnahme

Synthetische Medien mit echten berechneten MD5/SHA1/SHA256 und Größen:
MD5/SHA1 gemischt in einem Thread, beschädigte Datei, ungültiges Base64,
unbekannte Digestlänge, exakte versus gerundete Größen, vollständige Rohwerterhaltung,
IPC-/Manifest-/Neustartroundtrip und alte Archive ohne neue Felder.
Bei erweiterter Ausnahme: Default aus, Profil-/Ownerbindung, bewusste Aktivierung,
Widerruf und frischer Offlinehost. Exakte Anhangsrevision bindet den Annahmenachweis.
Kein Sourceadapter darf diese Policy selbst implementieren.

## Gewählte additive Umsetzung

- SDK: optionale Initfelder `ExpectedSha1` und `ProviderSizeHint` auf
  `SourceAttachment`; `SourceSizeHint(RawValue, UnitLabel, Precision)` bewahrt
  die numerische Rohschreibweise. Precision ist ohne Beleg `Unspecified`.
  Core besitzt eigene entsprechende Typen, keine SDK-Abhängigkeit.
- SHA-1 wird zusammen mit MD5/SHA256 aus denselben Originalbytes berechnet,
  einschließlich Filterstaging vor Bytefreigabe. Abweichung oder fehlender
  Nachweis bei neuer SHA-1-Erwartung ist strikt; bestehende Ausnahmen gelten
  weiterhin ausschließlich für Anbietergröße/MD5.
- Metadaten werden über Queue, WorkState, Kandidaten, Archivschema 5 und
  Manifest 4 bis in Anhangsbindung/Herkunftsdetails und HTML-Export geführt.
- Alte Metadaten erzeugen identische v1-Schlüssel. Neue Felder verwenden v2.
  Historische Bindungen ohne SHA-1 bleiben anhand ihrer SHA256 lesbar;
  aufgezeichnete SHA-1 wird bei Wiederverwendung zusätzlich verglichen.
  Aktuelle Leser unterstützen Archivschema 1–5 und Manifest 1–4; ältere
  Anwendungen dürfen die neuen Formate nicht schreiben.

### Ergänzende 2ch-Belege und Grenze

Sieben begrenzte GETs am 25.10.2026 bestätigen `.hk`→`.su`, die Anzeige `24Кб`
und `file.size + "Кб"` im offiziellen JavaScript. Drei kleine PNG-Originale
mit APIgröße 1/4/5 haben tatsächlich 1339/4449/4381 Bytes; **alle drei MD5s
weichen ab**. Der frühere JPEGnachweis 25475/API24 mit passender MD5 bleibt
ein eigener gültiger Einzelbefund. Das Upload-UI verwendet 1024, belegt aber
keinen exakten Rundungsvertrag des API. Deshalb Rohwert + Label `Кб`, Precision
`Unspecified`, keine erfundene Bytebandbreite und keine automatische Ausnahme.
Rohantworten/UTC/Hashes: externe Arbeitsbelege `nw01-research/` im isolierten
Source-/Filter-Wave-Arbeitsverzeichnis. Dies ist kein Produkt-Live-PASS.

## Gezielte lokale Prüfung

Alle Befehle einzeln von Root freigegeben und unter
`flock -o /tmp/meowchan-ui-1.0-build.lock`, isolierte Contracts-Arbeitskopie:

| Stand | Prüfung | Ergebnis |
| --- | --- | --- |
| SDK549429c / Checkout e75558c | SDK SourceValidationTests inkl. Rohwert-/SHA1-Roundtrip und Negativfällen | 27/27 PASS |
| 01530ba | Coreintegrität, Persistenz, Manifest/Store, Resolver, Contentrevision/-runtime | 149/149 PASS |
| 45df69f | SourceIntegrityAdapterTests mit echtem Hostmapping, SHA1 strikt trotz Größen-/MD5-Freigabe | 4/4 PASS |
| 45df69f | Desktop ArchiveAttachmentMatchingTests: exaktes Cleanup v1/v2, EN/DE Herkunft, tatsächliche Größe | 7/7 PASS |
| 5c58e15 | SourceMediaIntegrityTests nach letzten Reviewkorrekturen inkl. Variantprojektion und gemischter Altnachweise | 26/26 PASS |

SDK/Core-APIanalyzer ohne Diagnosen. Bestehende fremde xUnit-/Avaloniawarnungen
sind kein neuer Paketfehler. Der erste Corelauf fand einen echten Fehler beim
Anreichern alter Assetnachweise vor neuer Referenz; behoben und derselbe
149-Fälle-Lauf ohne abgeschwächte Assertions wiederholt. Zusätzlich hält der
Restore den bekannten SHA1-Nachweis über spätere historische Nullnachweise fest.

Öffentliche PackageReference-Consumer sind um neue SDK-/Corefelder, Restart und
Galeriebindung erweitert; ihre Ausführung am gemeinsam ausgelieferten Paketfeed
bleibt Aufgabe der Integration FE-05/NW-07. Reale Filterstage-/Workerprüfung führt
ebenfalls der Integrator durch. Kein Produkt-Live-PASS, GUI-/Native-/Releaseabschluss
oder Integritätsausnahme für2ch wird aus diesen Komponentenfällen abgeleitet.
Lieferentscheidung bleibt NW-07.
