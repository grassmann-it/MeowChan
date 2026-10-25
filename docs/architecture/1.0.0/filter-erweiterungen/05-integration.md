# FE-05 — Integration, Lieferung und gemeinsame Abnahme

Status: **teilweise implementiert, gemeinsame Abnahme offen**. Separater Branch
`feature/source-filter-wave/integration`; keine Aufnahme in Alpha 2.

## Zwischenstand 2026-10-25

- FE-01–04 importiert, einschließlich FE-04-Reviewfix für verspäteten Abbruch.
- Liefergraph zunächst um vier Filter erweitert; nach vollständigen Source-Mappings
  Kandidateninventar `32f98ad` mit 16 Plugins / 11 Quellen / 14 editierbaren Dateien.
  Registrierung ermöglicht echte Hosttests, sie ersetzt keine produktive Abnahme.
- 15 Python-Pakettests sowie 23 Workflow- und 12 Aktivierungsrunnerprüfungen
  bestanden. Das prüft Verträge/synthetische Payloads, keinen nativen Paketlauf.
- Stand `7df8dc3`: 8/8 neue echte Filterstage-/Worker-/Archivfälle und 317/317
  gemeinsame ExamplePlugin-Parserfälle bestanden. Der spätere FE-04-Reviewfix
  und neue GIF/WebP-Stagefälle sind in diesem Nachweis noch nicht enthalten.
- Stand `ae307f3`: Desktopbuild und 56/56 gezielte Aktivierungs-/Policy- und
  Admissiontargetfälle bestanden. Kein GUI- oder Publishnachweis.
- SDKfreier Compile+Describe-Probe über alle gelieferten editierbaren Dateien
  vorbereitet; Ausführung auf selbständigem Linuxpaket steht aus.
- Generischer Langtexteditor und GUIadapterprüfung für Hashlisten vorbereitet;
  tatsächlicher Tastatur-/Paste-/Speicher-/Neustartnachweis steht aus.

**Produktiver Integrationsbefund:** Der bisherige Hostheader akzeptiert nur
JPEG/PNG. Headerfix `d732a12` ergänzt GIF/WebP bei unveränderten Byte-/Pixelgrenzen;
der Worker meldet dafür 57/57 Modultests bestanden. Gemeinsamer Stand `3b7a9f1`:
23/23 echte Stage-/IPC-/Workerfälle bestanden, 0 Skips/Fehler/Warnungen. Belegt
sind Match/NoMatch, GIF/WebP trotz PNG-Dateiname, beschädigte Container als
technische Auslassung, Pixelgrenze vor Workerread, Revisionwechsel ohne Commit
sowie strikte SHA1-Prüfung trotz ausdrücklicher Größen-/MD5-Freigabe. Der erste
Lauf `b0a7c81` hatte 22/23 PASS; fehlende exakte Revisionsreferenzen der neuen
Consent-Testfixture wurden korrigiert, keine Produktionspolicy aufgeweicht.

Pluginlabels und Pluginhilfe bleiben derzeit Englisch; Hostchrome folgt EN/DE.
Das SDK bietet keinen Pluginlokalisierungsvertrag. Diese Abnahmelücke ist
sichtbar festgehalten; ein neuer SDKvertrag ist nicht Teil dieser Umsetzung.
Weitere offene Nachweise: technische Error/Unavailable-Fälle auf dem Workerpfad,
SHA1-Staging nach NW-01, geerbte/deaktivierte Regeln, vollständige GUI-Matrix
und native Pakete. Keine pauschale FE-05-Fertigmeldung.

## Verbindlicher ursprünglicher Umfang

Bestehendes Liefermuster erweitern: Projektregistrierung, Paketinventar,
editierbare .cs-Dateien und Assemblies, Compiler-/Workerweg, Autorenhilfe und
Filterbeispiele. Exakte Liste betroffener Build-/Packagingdateien beim Start
anhand `scripts/package-plugins.py`, `scripts/test-plugin-packages.py` und
bestehender Projektverweise bestimmen. Keine neue Produktversion festlegen.

Neue Plugins verfügbar machen, keine bestehenden Regeln automatisch umstellen
oder Grants vergeben. Vorhandene Dimensionsfilterbindungen bleiben gültig.
Einstellungen mit verständlichen Labels/Hilfe; EN/DE entsprechend bestehendem
Plugin-Lokalisierungsweg. Technische IDs und Einstellungen nicht übersetzen.
Reasoncodes stabil; UI unterscheidet fachliche Ablehnung, fehlende Metadaten,
unbekanntes Format, beschädigte Datei, Budget und fehlendes Recht.

## Geplante Nachweise

- Je Plugin unabhängiger SDK-only Build und gezielte Algorithmustests.
- SDKfreier ausgelieferter .cs-Compilerweg, echter Worker und Neustartbindung.
- Isolierte Fixturequelle: aktiviertes Plugin bekommt korrekte Stageidentität;
  Match übernimmt dieselben Bytes, NoMatch speichert kein neues Original,
  Error/Unavailable bleiben technische Auslassungen. Keine zweite Beschaffung.
- Revisionwechsel/Grantwiderruf/Cancel gegen wartende oder laufende Auswertung
  anhand vorhandener Hostregressionen gezielt prüfen, nicht ganze Suite duplizieren.
- Bestehender Dimensionsfilter und geerbte/deaktivierte Filterregeln unverändert.
- GUI: alle Settings editierbar, Fehler fokussierbar, Hashliste verlustfrei,
  Tastatur, EN/DE, kleine Fenster und 1×/1.5×/2× auf verfügbarer Plattform.
- Einzelfiltergrenze verständlich: keine UI suggeriert mehrere gleichzeitig
  aktive Plugins. Vorhandene vorgelagerte Hostregeln im Ergebnis berücksichtigen.

Tests sind hier Anforderungen, keine behaupteten PASS. Plattformlücken separat
ausweisen; keine Releasefreigabe aus reinem Parser-/Linuxnachweis ableiten.
O10-01 Regellabor bleibt optional und ist keine Voraussetzung dieser Lieferung.
