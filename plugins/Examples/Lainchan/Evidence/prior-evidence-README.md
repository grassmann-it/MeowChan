# R10-01b / R10-04: begrenzte Quellenbelege

Erhoben am 2026-10-22, 15:49:57–15:51:10 UTC. Lokale Basis: `8ec0cf50b270e0551a06d3ad61eff0f448bd70ff`.
Status: **API-Form teilweise belegt; kein Nachweis produktiver Lainchan-Unterstützung.**

Es gab genau sechs HTTP-GETs: fünf an lainchan.org, einen an das von dessen Homepage verlinkte GitHub-Repository. Keine Medien, Cookies, Anmeldung, automatischen Retries, Redirects oder Builds. Antworten waren auf 256 KiB begrenzt; beim bedingten Request wurde kein Body gelesen. Persistiert wurden ausschließlich Status, ausgewählte Header, Hashes, Strukturmerkmale und kleine Quellcodeauszüge; keine realen Posttexte, Namen, E-Mails, Dateinamen oder Medienbytes. Die JSON-Belegdateien tragen URL und UTC-Zeit. Hashes dokumentieren die gelesenen Bytes, ermöglichen ohne archivierten Originalbody aber keine unabhängige Rekonstruktion.

## Tatsächlich beobachtet

| Beleg | URL | Ergebnis |
|---|---|---|
| `lainchan-home.json` | https://lainchan.org/ | 200; offizielle Links zu `/sec/res/28669.html#28673` und anderen Threadseiten sowie https://github.com/lainchan/lainchan |
| `lainchan-sec-catalog.json` | https://lainchan.org/sec/catalog.json | 200 application/json; Array aus 9 Seitenobjekten mit `page` und `threads`, insgesamt 90 Einträge; erster Thread `27498` |
| `lainchan-sec-thread.json` | https://lainchan.org/sec/res/27498.json | 200 application/json; Objekt mit `posts`, 167 Posts; ein Post hat zwei `extra_files`; OP enthält `omitted_posts:0`, `omitted_images:0`, `locked:0` |
| `lainchan-sec-missing.json` | https://lainchan.org/sec/res/0.json | 404 HTML; absichtlich nicht vorhandene synthetische Thread-ID, kein ehemals existierender Thread |
| `lainchan-upstream-api.json` | https://raw.githubusercontent.com/lainchan/lainchan/master/inc/api.php | 200; offizielle Implementierung beschreibt 4chan-kompatible JSON-API; übersetzt `locked`, `cyclical`, `omitted_posts`, erzeugt `extra_files`, `posts` und Katalog-`threads` |
| `lainchan-sec-unchanged.json` | https://lainchan.org/sec/res/27498.json | 304 auf `If-None-Match` des vorherigen erfolgreichen Snapshots; Body ungelesen |

Die upstream-URL zeigt einen beweglichen Branch und ist kein Beweis der eingesetzten Serverrevision. Auszüge und SHA-256 sind dokumentiert. Die Homepage belegt `/board/res/id.html`-Threadlinks, der konkrete Livecheck belegt `/board/res/id.json` und `/board/catalog.json`. Kein globaler Boardlistenendpunkt wurde geprüft. Keine weiteren URLaliase oder Medienhost-/Medienpfadregeln wurden verifiziert.

## Bedeutungen und Grenzen

- **Katalog:** Seitenstruktur ist belegt. Ein einzelner vollständiger HTTP-Body beweist keine historische oder serverübergreifende Vollständigkeit. Katalogauslassung ist niemals Threadende. Fehlende globale Boardliste darf ReadCatalog für ein explizites Board nicht verhindern.
- **Thread:** Posts und geordnete Zusatzdateien sind belegt. `extra_files` ergänzt die primäre Datei; Positionen müssen stabil transportiert werden. `com` ist in der 4chan-nahen Form ein HTML-Feld; der Adapter muss HTML als Format deklarieren, keine lokalen Pfade entscheiden.
- **Unchanged:** Der Livecheck belegt ETag/304. Nur mit Bezug auf einen zuvor akzeptierten Snapshot normalisieren; keine leere Postliste produzieren. Neue Validatoren erst nach erfolgreichem Parsing/Validieren übernehmen.
- **404:** Der geprüfte JSON-Endpunkt liefert für `0` eine HTML-404-Seite. Das belegt einen fehlenden Ressourcenpfad, nicht die Löschung eines zuvor existierenden Threads. Für den Providervertrag ausschließlich den geprüften Threadendpunkt als NotFound behandeln; eine Medien-404, Katalog-404, 403, Timeout oder Parsefehler darf kein Threadende auslösen. HTTP410 wurde nicht geprüft.
- **Archived:** Weder Liveantwort noch überprüfte upstream-Auszüge belegen Lainchan-Archivmarkierung. `locked` ist kein belegtes Endsignal, `cyclical` ebenfalls nicht. Lainchan `Archived` bleibt offen; den generischen terminalen Snapshot anhand vorhandener 4chan-Fälle testen. Kein erfundenes `archived:1` als reale Lainchan-Evidenz.
- **Partial:** Fehlende Felder und aus einem späteren Snapshot verschwundene Posts löschen die lokale Erfassung nicht. Fehlend, ausdrücklich leer und ausdrücklich gelöscht getrennt halten. Nicht-null `omitted_posts` mit positivem Wert muss zumindest zu unvollständiger Inhaltsabdeckung führen; hier war nur 0 beobachtet. HTTP206 oder kaputtes/abgeschnittenes JSON ist ein Fehler, kein akzeptierter partieller Snapshot.

## Vorhandene lokale Inlinefixtures

Alle folgenden Daten sind **synthetische Repositorytests**, keine datierten Livecaptures. Herkunft und Inhalt dürfen deshalb nicht als aktuelle öffentliche APIbelege ausgegeben werden.

| Datei / Bereich | Bereits abgedeckter Fall |
|---|---|
| `tests/MeowChan.Tests/ProviderHttpIntegrationTests.cs:17` | EmptyThread / OneMedium / TwoMedia: kleine JSON-Posts mit IDs 123/124, Medien 1000/1001 |
| dieselbe Datei, `:33`, `:82` | 304 mit Last-Modified, Body darf nicht gelesen werden; 304 ohne akzeptierten Snapshot ist Fehler (4chan und 8kun) |
| dieselbe Datei, `:103` | Ungültiges JSON darf seinen Validator nicht committen |
| dieselbe Datei, `:122` | **4chan** finaler `archived:1`-Snapshot liefert letzte Medien, stoppt Polling, lässt Downloads auslaufen |
| dieselbe Datei, `:179` | Thread404 nach akzeptiertem Snapshot, kein neuer Snapshot, vorhandene Medien laufen aus (4chan/8kun) |
| dieselbe Datei, `:233` | 8kun `locked:1` bleibt Active |
| dieselbe Datei, `:307`–`:353`, `:496` | Optionale Boardmetadaten fehlen/timeouten; Katalog bleibt nutzbar. Unterschiedliche `/boards.json`-Formen. Übergröße abgewiesen. |
| `tests/MeowChan.Tests/ContentRuntimeTests.cs:170` | 8kun Text-only und Hauptdatei plus `extra_files`, geordnete Offlineanhänge |
| `tests/MeowChan.Tests/ContentRuntimeTests.cs:109` | Capturefehler, späterer terminaler Snapshot, Retention fehlender Posts bei Retry |
| `tests/MeowChan.Tests/ArchiveCatalogTests.cs:53` | Fehlende Posts/Felder behalten; ausdrücklich vorhandenen Wert ersetzen bzw. auf null setzen |
| dieselbe Datei, `:81` | Explizite Anhanglöschung bewahrt frühere Metadaten und weitere geordnete Anhänge |
| `libmeowchan/Archive/ProviderPostCapture.cs:15` | Aktuelle Presence-Abbildung: `time/sub/name/com/deleted`, Attachments durch tatsächliche Anhänge oder vorhandenes `extra_files` |

Die 404-Integrationstests beginnen mit einem gültigen Snapshot. Zusätzlich benötigt der SDK-Vertrag ausdrücklich **NotFound ohne vorherigen Snapshot und ohne Posts**. Partial ist im Archiv auf DTO-Ebene abgedeckt; ein kleiner Source-JSON→SDK-Presence-Test fehlt als eigenständiger Herkunftsfall.

## Empfohlene kleine, reproduzierbare Testdaten

`proposed-synthetic-cases.json` enthält einen Vorschlag, keine aktiv eingebauten Testfixtures. Alle Nutzwerte sind frei erfunden; nur minimale Feldstruktur wurde von den Belegen bzw. Repositorytests abgeleitet. Keine Kopie realer Inhalte und keine Lizenzbehauptung über fremde Posttexte. Netz- und Medientests sollen ausschließlich einen lokalen Stub bedienen. Dokumentierte Lainchan-URLs sind Herkunft, keine Aufforderung, Tests gegen die öffentliche Site zu richten.

Benötigt werden: normaler Thread mit Hauptdatei und zwei Zusatzdateien plus Text-only-Reply; Katalog mit zwei Seiten; partielles Folgeupdate mit fehlenden Feldern; explizit leere Textfelder; 304 mit akzeptiertem Validator; 304 ohne Baseline; Thread404 ohne Body/Snapshot; 4chan finaler Archivsnapshot; 8kun locked weiterhin Active; ungültiges JSON; HTTP403/429/503/206 und Bodyübergröße als Failure/Deferred. Ein transportabler SDK-Snapshot sollte Presence pro Feld und Inhaltsvollständigkeit explizit unterscheiden. Anhänge benötigen Postreferenz, ID/Variante und Position unabhängig von URL/Dateiname.

Offen für R10-04: URLcanonicalisierung einschließlich Unicodeboards/Aliase, verifizierte Medien-URLs, tatsächlich eingesetzte Version, Archiv-/Löschhistorie, Vollständigkeitsgarantie des Katalogs, echter Compiler→Worker→Registry→Queue→Store→Neustart-Pfad. Keine dieser offenen Prüfungen wurde durch diese Evidenz ersetzt.
