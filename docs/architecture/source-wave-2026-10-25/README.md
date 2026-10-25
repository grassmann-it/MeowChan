# Quellenwelle: 2ch, Leftypol, Uboachan, ZZZchan und SushiChan

## Aktueller Umsetzungsstand — 25.10.2026

Der Nutzer hat die parallele Umsetzung beauftragt und die Empfehlung für NW-01
angenommen: gerundete Anbietergröße als unverbindlichen Rohhinweis erhalten,
SHA-1 zusätzlich und strikt prüfen, Leftypol-Abweichungen bei Größe/MD5 nur nach
bewusster Freigabe je Quelle und Profil. Alle fünf neuen Quellen sind
**Experimental und bei der ersten Registrierung deaktiviert**. 2ch, Uboachan,
ZZZchan und SushiChan aktivieren ausschließlich die Quelle; Aktivierung vergibt
keine Integritätsausnahme. Bestehende Profile/Quellen behalten ihre Zustände.

Die Arbeit läuft separat in `feature/source-filter-wave/integration`, Ausgangsstand
`b5e4979`; sie ist **kein Bestandteil des Alpha-2-Releases**. Alle fünf Adapter,
NW-01-Datenpfad und gemeinsame Aktivierung sind implementiert und als Kandidat
registriert. Zusammen mit FE-01–04: 16 Plugins / 11 Quellen / 14 editierbare Dateien.
Ein Inventareintrag ist kein produktiver Abnahmenachweis.

| Bereich | Tatsächlicher Stand |
| --- | --- |
| NW-00 | Begrenzte direkte HTTP-/Schema-/Originalproben ausgeführt; kein Host-E2E |
| NW-01 | SDK/Core/IPC/SQL/Manifest/Projektionen umgesetzt; gezielte Worker-Nachweise und Reviewkorrekturen dokumentiert |
| NW-02–06 | Adapter und Parser umgesetzt; gemeinsame Parserprüfung 317/317 am älteren Stand, finales 2ch/Uboachan-Mapping gezielt 53/53 |
| NW-07 | Lieferintegration und getrennte Aktivierung umgesetzt; zwölf echte Source-Hostfälle vorbereitet, noch kein gemeinsamer Source-E2E-PASS |

Offen bleiben insbesondere OriginalMedia-Redirectprüfung `.hk`→`.su`, Ausführung
der zwölf Source-Hostketten, echter OS-Prozessneustart, enge Liveversuche,
SDKfreier Paket-/Compiler-/Workerweg, GUI und native Pakete. Prüfungen und
Quellstände stehen im [Integrationsnachweis](07-integration-abnahme.md).
FE-05 hat 23/23 echte Filterstage-/Workerfälle am Stand `3b7a9f1` bestanden;
dies ersetzt keine Quellenabnahme.

## Historische Ausgangsplanung

Die nachfolgenden Abschnitte bewahren den ursprünglichen Planungsstand gegen
`3ea020d`. Aussagen wie „noch nicht beauftragt“, „NW-01 offen“ oder „Ausnahme nicht
beschlossen“ gelten **nur für diesen historischen Ausgangspunkt**. Der aktuelle
Auftrag und die akzeptierten Entscheidungen stehen oben; die ursprünglichen
Befundtabellen bleiben als Herleitung erhalten.


Stand: 25.10.2026. **Auswahl und Vorprüfung beauftragt; Produktumsetzung noch nicht.**
Der Nutzer nimmt die ersten vier Kandidaten an und ergänzt SushiChan ausdrücklich.
Er verlangt Vorprüfungen für alle fünf und Übernahme der Learnings in Arbeitspakete.
Futaba bleibt eigener späterer Ausbau. Eine Releaseversion ist nicht festgelegt.

Bezugsstand: `feature/1.0.0-ui-ursa`, `3ea020d2008d7fcf2c29656c0f0055ba2f9fdd95`.
Vorbestehender `.gitignore`-WIP unangetastet. Bestehende sechs Quellen bleiben der
Ausgangspunkt. Fünf weitere ergäben elf Quellen, zwölf Plugins einschließlich
des bestehenden Filters und zehn editierbare Beispieldateien, sofern alle geliefert
werden und der bisherige Lieferzuschnitt beibehalten wird. Kein Abnahmenachweis
für dieses Zielinventar liegt vor.

## Ergebnis der Vorprüfung

Alle fünf besitzen erreichbare JSON-Kataloge, Threadantworten und mindestens ein
vollständig geladenes Original. Alle fünf beantworteten bedingte Thread-GETs mit
304. Die Unterschiede liegen nun konkret in Hostrouting und Integrität:

| Quelle | Gemessener Befund | Konsequenz für das Paket |
| --- | --- | --- |
| 2ch | API auf `.hk`; Original 301 nach `.su`, dort 25.475 Bytes und passender MD5; APIgröße 24 | Beide Transporthosts nach Zweck deklarieren, Hex-MD5 in SDK-Base64 wandeln; Größenpräzision klären |
| Leftypol | files[] mit eigenen Pfaden, bis zwei Anhänge; eine PNG-MD5-Abweichung bei passender Größe, zweite WebP passt | Explizites Mapping; echter Mismatch als Regression, keine stillschweigende Ausnahme |
| Uboachan | Im selben Thread MD5 und SHA-1 im Feld md5; beide an Originalen bestätigt; zusätzlicher Thread mit extra_files | Algorithmus je Anhang bestimmen; SHA-1-Vertragsfrage lösen, Mehrfachdateien erhalten |
| ZZZchan | OP im Root plus replies; bis zwei Anhänge; Originalgröße und SHA256 passen | Eigener jschan-Parser, postId statt _id, phash nie als Integritätshash |
| SushiChan | JSON verfügbar, bis vier Anhänge; Originalgröße und MD5 passen | Unmittelbar geeigneter vichan-naher Kandidat; Hauptdatei plus extra_files |

[Ausgeführte Proben, genaue Werte und Grenzen](00-vorpruefung.md).
Das ist eine direkte HTTP-/Schema-Vorprüfung, kein PluginHost-/Queue-/Archiv-PASS.
Kein Adapter wurde implementiert und kein bestehender Integritätsvertrag gelockert.

## Pakete und Abhängigkeiten

| Paket | Ergebnis | Planreife |
| --- | --- | --- |
| [NW-00](00-vorpruefung.md) | Begrenzte Liveprüfung aller fünf und belastbare Mappings | Ausgeführt, Grenzen dokumentiert |
| [NW-01](01-integritaetsvertrag.md) | Größenpräzision, gemischte Hashes und Leftypol-Policy | Architekturentscheidungen offen; betrifft nur abhängige Teile |
| [NW-02](02-2ch.md) | 2ch/Makaba | Parser planbar; Medienabnahme abhängig von NW-01 |
| [NW-03](03-leftypol.md) | Leftypol | Parser und striktes Fehlerverhalten planbar; Ausnahme nicht beschlossen |
| [NW-04](04-uboachan.md) | Uboachan | Parser planbar; SHA-1-Pfad abhängig von NW-01 |
| [NW-05](05-zzzchan.md) | ZZZchan | Keine Vertragslücke in der Stichprobe; Umsetzung planbar |
| [NW-06](06-sushichan.md) | SushiChan | Keine Vertragslücke in der Stichprobe; Umsetzung planbar |
| [NW-07](07-integration-abnahme.md) | Lieferintegration und produktive Kette pro Quelle | Nach tatsächlicher Sourceübergabe, keine Vorab-PASS |

Empfohlene technische Reihenfolge nach den Befunden: **SushiChan und ZZZchan zuerst**,
während NW-01 für 2ch/Uboachan/Leftypol geklärt wird. Die fachliche Priorität von
2ch bleibt bestehen; sie verlangt keine Wartezeit für unabhängige Quellen.
Alle fünf sind ausgewählt, einzelne offene Verträge machen daraus keine neue Auswahlfrage.

## Gemeinsame Grenzen

Es gelten [Pluginvertrag](../1.0.0/plugin-vertrag.md),
[API-Politik](../1.0.0/api-politik.md),
[Quellenvertrag](../1.0.0/source-erweiterungen/README.md) und
[Autorenweg](../../plugins/source-plugins.md). Jeder Adapter bleibt eine vollständige
SDK-only-.cs-Datei plus vorgebaute Assembly mit eigener Identität. Kein gemeinsamer
Enginebaukasten, kein Plugin-Mediendownload und keine Plugin-Retryschleife.
Metadaten über HostHttp, Medien und Archiv über vorhandene zentrale Dienste.
Describe bleibt netzwerkfrei. Erste Funktion: Board-/Thread-URL, Katalog, Posts,
Originalmedien, Reader/Galerie und Offlinezugriff. Posting, Accountlogin, globale
Boarddiscovery, automatische Archivfallbacks und Deltaoptimierung bleiben außerhalb.

Die angegebenen IDs und konkreten Ordner in den Paketen sind technische Vorschläge;
vor Umsetzung Konfliktprüfung gegen tatsächlichen Registry-/Lieferstand.
Neue Quellen standardmäßig deaktiviert/experimentell ist die Empfehlung, noch keine
angenommene neue Defaultentscheidung. Aktivierung ist keine Integritätsausnahme.
Bestehende Kombidialogfreigabe betrifft Kohlchan/Wizchan/Endchan und erweitert sich
nicht automatisch. Die endgültige Einordnung wird vor NW-07-Auslieferung entschieden.

Für Übergaben diesen gesamten Ordner einschließlich Evidence und die verlinkten
Vertragsdokumente mitgeben; Architekturdateien sind lokal möglicherweise ignoriert.
