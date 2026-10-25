# Neue Filter — Arbeitspakete FE-01–05

## Aktueller Umsetzungsstand — 25.10.2026

Der Nutzer hat die parallele Umsetzung von FE-01–05 ausdrücklich beauftragt.
Die vier SDK-only Filter sind implementiert und im separaten Integrationsbranch
`feature/source-filter-wave/integration` registriert. Sie sind **kein Bestandteil
des Alpha-2-Releases**. Der Umfang bleibt ein Filter pro effektiver Regel;
Filterketten und Tags/Auto-Tagging bleiben eigene offene Entwürfe.

| Paket | Tatsächlicher Stand |
| --- | --- |
| FE-01 | Dateigröße/Originalname implementiert; zusammen mit FE-02 gezielt 30/30 Modulfälle bestanden |
| FE-02 | SHA-256-Sperrliste implementiert; generischer begrenzter Mehrzeileneditor vorbereitet, echte GUI-/Save-/Restartprüfung offen |
| FE-03 | Bytebasierte Format-/Animationsprüfung implementiert; 28/28 Modulfälle; Hostheader erweitert ohne höhere Budgets |
| FE-04 | PNG-Schlüsselpräsenz implementiert; einschließlich Cancelreviewfix 42/42 Modulfälle |
| FE-05 | Liefergraph, SDKfreie Probe und Doku vorbereitet; integrierter Stage-/Workerpfad 23/23 PASS auf `3b7a9f1`; Gesamtabnahme offen |

Das gemeinsame Kandidateninventar mit NW-02–06 umfasst 16 Plugins / 11 Quellen /
14 editierbare Dateien. Pythonprüfungen zum Paket-/Workflow-/Aktivierungsinventar
15 + 23 + 12 bestanden. Das ist keine Paketveröffentlichung. SDKfreies echtes
Kompilieren, OS-Prozessneustart, native Pakete und die GUI-Matrix sind noch offen.
Pluginlabels bleiben mit dem aktuellen SDK Englisch, Hostbedienelemente EN/DE;
die Lokalisierungslücke ist ausdrücklich dokumentiert. Details und genaue
Nachweisgrenzen im [Integrationspaket](05-integration.md).

## Historische Ausgangsplanung

Die folgenden Abschnitte dokumentieren den ursprünglichen Plan gegen `3ea020d`.
„Keine Implementierung beauftragt“ und „Abnahmen geplant“ beschreiben diesen
historischen Zeitpunkt. Sie widerrufen weder den späteren Umsetzungsauftrag noch
die oben belegten Ergebnisse. Die ursprünglichen Pakettabellen bleiben erhalten.


Stand 25.10.2026, geprüft gegen `3ea020d`, vorbestehender `.gitignore`-WIP.
Nutzerwahl: „1–3 klingen gut gern arbeitspakete metadaten filter auch gut“.
Dateiregeln, Hash-Sperrliste, Animation/Format und Metadaten-Präsenz sind damit
zur Planung ausgewählt. Keine Implementierung beauftragt; Zielrelease offen.
Diese Pakete erweitern nicht automatisch den bisherigen R10-Releaseauftrag.

| Paket | Ergebnis | Abhängigkeit |
| --- | --- | --- |
| [FE-01](01-dateiregeln.md) | Größe und Originalname prüfen | heutiges SDK |
| [FE-02](02-hash-sperrliste.md) | Identische unerwünschte Dateien ausschließen | heutiges SDK |
| [FE-03](03-animation-format.md) | Format und Animation aus Originalbytes prüfen | heutiges SDK |
| [FE-04](04-metadaten.md) | PNG-Textfelder auf Vorhandensein prüfen | heutiges SDK |
| [FE-05](05-integration.md) | Gemeinsam ausliefern und verständlich bedienen | FE-01–04 |

## Verbindliche Grundlage und Planvorgaben

[Filtervertrag](../filter-vertrag.md), [Pluginvertrag](../plugin-vertrag.md) und
[Scopeentscheidungen](../entscheidungen-2026-10-22.md) gelten weiter; OCR bleibt
zurückgestellt. Technische Grundlage: `MeowChan.PluginSdk/FilterContracts.cs`,
`Settings.cs`, `SdkContract.cs`, `plugins/Examples/ImageDimensionsFilter/`.
Pfadangaben zum Code beziehen sich auf die Repositorywurzel.

Die nachfolgenden Detailzuschnitte sind begründete Planvorgaben des Assistenten,
keine zusätzlich behaupteten Nutzerentscheidungen. Vier SDK-only Plugins, jeweils
editierbare einzelne C#-Datei und Assembly nach bestehendem Liefermuster; keine
neuen nativen Decoder, Fremdprozesse oder NuGetabhängigkeiten zur Auswertung.
Eigenständige kleine Implementierungen statt ungeprüfter Fremdcodeübernahme.
Stable IDs/Settingkeys vor Lieferung festlegen; keine SDK-Versionserhöhung nötig.

Der Host liefert tatsächliche Größe/SHA-256 nach Stagebeschaffung. Filter sparen
keinen ursprünglichen Transfer. Tags/Posttexte stehen nicht zur Verfügung.
Match/NoMatch sind fachlich; Unavailable/Timeout/Error bleiben technische
Auslassungen. Ergebnisse erklären sich über ReasonCode/Message ohne Bildtextlogs.
Keine Archivänderungen, Löschungen oder erneuten Downloads durch Plugins.
Describe bleibt passiv. Grant-, Revisions-, Abbruch- und Stageverträge erhalten.
SDK-Budgets nicht erhöhen, auch wenn ein einzelner Filter keine Bytes liest.

**Bekannte Einschränkung:** `MediaImageFilterRule` enthält genau einen Plugin-/
Filterverweis. FE-01–04 sind einzeln auswählbar, nicht automatisch verkettbar.
Host-Medientyp-/Größenregeln bleiben davor wirksam. Eine Filterkette ist eine
[separate offene Erweiterung](06-filterkombination-entwurf.md), kein versteckter
Teil dieser Pakete. Sie blockiert unabhängige Einzelfilter nicht.

## Umsetzung und Übergabe

FE-01/02 zuerst, um Lieferung und metadatenbasierte Auswertung zu prüfen;
FE-03/04 sind fachlich unabhängig. FE-05 bündelt gemeinsame Projekt-/Paketdateien
und GUIabnahme. Keine Delegation oder parallele Ausführung durch diesen Plan
beauftragt. Build-/GUIarbeiten nach geltenden Projektregeln koordinieren.

Alle Abnahmen in den Paketdateien sind geplant, nicht ausgeführt. Bei späterem
Start HEAD/WIP prüfen, vorhandene Änderungen erhalten und tatsächliche Ergebnisse
separat festhalten. Diesen lokal ignorierten Ordner samt referenzierten Verträgen
bei Checkout-/Rechnerwechsel bewusst mitgeben.

Tagfilter und Auto-Tagging sind Interesse, noch keine ausgewählte Architektur:
[Vorgehen und offene Entscheidungen](07-tagging-entwurf.md).
