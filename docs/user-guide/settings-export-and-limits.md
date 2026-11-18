# Einstellungen: Tagfilter- und URL-Textexportgrenzen

Referenz für den Quellstand von oChan/MeowChan 1.1.0. Die beschriebenen
Einstellungen und Caller sind implementiert; diese Referenz ist kein Nachweis
einer ausgeführten Laufzeit- oder Plattformabnahme.

## Welche Einstellung begrenzt was?

Die drei Zahlen stehen unter **Speicher und Grenzen**. Der Fensterschalter steht
unter **Oberfläche**. Änderungen im Einstellungsentwurf werden erst mit
**Speichern** wirksam; ein Neustart ist dafür nicht nötig.

| Einstellung | Standard | Erlaubt | Tatsächlicher Geltungsbereich |
| --- | --- | --- | --- |
| Archiv-Tagfilter pro Liste (`ArchiveTagFilterTagsPerList`) | 32 | 8–128 | Aufnahme weiterer Tags je Einschluss- bzw. Ausschlussliste im Archiv und auf der Medienseite; nicht die Zahl eigener Tags insgesamt. |
| Größe des URL-Textexports in MiB (`UrlTextExportLimitMiB`) | 1 MiB | 1–64 MiB | Writerbudget für die lokale URL-Textdatei; weder eine allgemeine Exportgrenze noch eine Erweiterung des URL-Dateiformats. |
| Slash-Tagvorschläge (`SlashTagSuggestionCount`) | 8 | 3–20 | Zahl erkannter `/name/`-Marker, die als Vorschlagskandidaten aus Threadtexten übernommen werden. |
| Fenster beim Wechsel zu Minimal verkleinern (`ShrinkWindowForBarebones`) | an | an/aus | Automatische Verkleinerung und zugehörige Größenwiederherstellung beim Arbeitsansichtswechsel. |

Ein MiB sind **1.048.576 Bytes**. Zahlenbereiche sind keine Zusage, dass jeder
Export dieses Volumen erzeugen kann: Format-, Identitäts- und Pfadprüfungen gelten
zusätzlich.

## Tagfilter nach einer Senkung von 128 auf 8

**Vorhandene Filter werden nicht auf acht Tags gekürzt.** Gespeicherte
Suchdefinitionen bleiben bis zum technischen Deckel von **128 Tags je Liste**
lesbar. Auch Suche, Chipentfernung und Wechsel zwischen „alle“ und „mindestens
einer“ rekonstruieren den vollständigen vorhandenen Filter unter diesem Deckel,
nicht unter der gesenkten Aufnahmegrenze.

Einschluss und Ausschluss haben jeweils ein eigenes Aufnahmebudget. Ein Tag kann
nicht gleichzeitig in beiden Listen stehen. Ein bereits in seiner Zielliste
enthaltener Tag ist keine neue Aufnahme und verändert die Liste nicht.

Beispiel: Ein Filter enthält 128 eingeschlossene und sieben ausgeschlossene Tags.
Nach dem Speichern der Grenze **8** gilt:

- Suchen verwendet weiterhin alle 128 eingeschlossenen und sieben ausgeschlossenen
  Tags; die Einstellung schreibt die gespeicherte Definition nicht um.
- Ein weiterer Tag in der Einschlussliste wird mit einer Meldung zur Grenze **8**
  verweigert, solange die Liste nicht wieder Platz unter dieser Grenze hat.
- Ein eingeschlossener Tag darf in die Ausschlussliste wechseln: Danach sind es
  127 eingeschlossene und acht ausgeschlossene Tags. Die große Quellliste
  verhindert diesen zulässigen Wechsel nicht.
- Ein weiterer Wechsel in die nun volle Ausschlussliste wird verweigert, ohne den
  Tag aus der Einschlussliste zu entfernen.
- Entfernen bleibt möglich; die übrigen Tags bleiben erhalten. Bei mindestens zwei
  Einschlüssen bleibt auch die gewählte Matchart erhalten; darunter sind die beiden
  Matcharten gleichbedeutend und werden einheitlich als „alle“ gespeichert.

Im Archiv sind Änderungen am Filterentwurf erst nach **Suchen** angewendet; die
Medienseite filtert bei Änderungen direkt. Ein gelöschter Tag oder ein nicht
verfügbarer Tagindex kann die Ausführung weiterhin verhindern. Die App darf dann
nicht still den Tagfilter weglassen und mehr Treffer anzeigen. Ein höherer
Aufnahmegrenzwert repariert keinen fehlenden Tagindex.

## URL-Textdatei: drei unabhängige Grenzen

Der Archivbefehl **Linkliste exportieren…** exportiert derzeit **genau eine
Seiten-URL des gewählten archivierten Threads**, als UTF-8 mit abschließendem
Zeilenumbruch. Es werden keine Medien, Tags, Aufträge oder Zugangsdaten exportiert
und kein anderes Programm gestartet. Nur passende, unterstützte Threadseiten von
4chan oder 8kun werden zugelassen; eine Bestätigung garantiert keine Erreichbarkeit.
Die Seiten-URL kann beim Weitergeben Leseinteressen offenlegen.

1. **Einstellbares Writerbudget: 1–64 MiB, Standard 1 MiB.** Die Operation bindet
   den Wert bereits vor dem Lesen der Threadzusammenfassung. Eine Änderung der
   Einstellung während dieses Vorgangs gilt erst für einen später gestarteten
   Export; sie verändert nicht die bereits gestartete Bestätigung.
2. **Technischer Formatvertrag: höchstens 1 MiB.** Der zugrunde liegende
   URL-Textcodec hat außerdem maximal 4.096 Zeilen und 8.192 UTF-16-Codeeinheiten
   je Zeile. Der heutige Archivplan ist enger: genau eine Seiten-URL mit passender
   Quelle, Board und Threadidentität, keine beliebige mehrzeilige Linkliste.
   **64 MiB einzustellen ermöglicht deshalb heute keinen 64-MiB-URL-Export.**
   Bei allen zulässigen Einstellungen bleibt die Formatprüfung vorrangig; ein
   gültiger heutiger Plan erreicht die Writerbudget-Überschreitung nicht.
3. **Technische Zielpfadgrenzen: höchstens 4.096 UTF-16-Codeeinheiten und 128
   Pfadkomponenten ohne Laufwerks-/Dateisystemwurzel, einschließlich Dateiname.**
   Weitere Betriebssystem- und Sicherheitsprüfungen können einen Pfad schon vorher
   verweigern. Diese Grenzen sind keine Dateigrößenangabe. Mehr Ausgabe-MiB machen
   einen zu langen oder zu tiefen Pfad nicht zulässig: Wähle einen kürzeren Pfad.

Eine ungültige oder nicht passende Seiten-URL wird vor der Dateiauswahl verweigert;
ein größerer MiB-Wert ändert daran nichts. Die Fehleranzeige unterscheidet die
Ausgabegrenze von der Pfadlänge/-tiefe. Bei diesen Vorprüfungsverweigerungen wird
keine neue Datei angelegt. Eine vorhandene Zieldatei wird nicht überschrieben.
Nach begonnenem Schreiben können bei Abbruch oder Fehlern dagegen unvollständige
Dateien verbleiben; sie werden nicht automatisch gelöscht.

Dieser MiB-Schalter betrifft **nicht** den JSON-Einstellungsexport, HTML-Exporte,
Originalmedien oder native Archivtransfers. Das Exportieren von Einstellungen
überträgt Präferenzwerte, nicht die Inhalte gespeicherter Suchfilter; es ist weder
ein Archivbackup noch die oben beschriebene URL-Linkliste.

## Slash-Vorschläge und Minimal-Fenstergröße

Die Slash-Zahl ist **unabhängig** von der allgemeinen Quick-Tag-Vorschlagszahl.
Sie begrenzt erkannte Marker aus Titel-/Threadtexten, nicht sämtliche Treffer einer
beliebigen Texteingabe. Das gemeinsame Ranking und dessen eigene Anzeigegrenze
können weniger Vorschläge anzeigen. Die Textprüfung bleibt auf 8.192
UTF-16-Codeeinheiten je Eingabetext und 32 Zeichen je Markerinhalt begrenzt.
Vorschläge weisen keine Tags automatisch zu. Der Reader berücksichtigt eine
geänderte Zahl auch beim bereits gecachten Thread.

Ist **Fenster beim Wechsel zu Minimal verkleinern** ausgeschaltet, wird beim
Eintritt weder auf die Minimalgröße verkleinert noch ein maximiertes Fenster dafür
normalisiert. Beim Austritt wird keine alte Shrink-Geometrie restauriert. Auch ein
während Minimal gespeichertes Ausschalten verwirft eine zuvor erfasste
Restore-Geometrie; späteres Wiederanschalten holt diese nicht zurück. Welche
Ansichtselemente beziehungsweise Inspector-Bereiche sichtbar sind, bleibt davon
unabhängig. Die Option ist keine Sperre gegen manuelle oder betriebssystembedingte
Größenänderungen.
