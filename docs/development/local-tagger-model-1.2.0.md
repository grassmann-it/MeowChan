# B6 · Lokaler Bildtagger

Unter **Quellen → Lokaler Bildtagger** lässt sich ein lokales Modellpaket prüfen und importieren. Die Vorschau zeigt Herausgeber, Modell-ID, Revision, Lizenz, Paketgröße und freien Speicher für eine zusätzliche Kopie. Die Prüfung und der Import lassen sich abbrechen. Der Dialog unterscheidet ein verifiziertes Paket von der Verfügbarkeit der CPU-Inferenz. Für die Ausführung ist eine separate Freigabe nötig.

## Manifest v1

Ein Paket besteht aus einem UTF-8-JSON-Manifest und der danebenliegenden Datei `model.onnx`. V1 sieht eine einzelne Modelldatei vor. Feldnamen sind case-sensitive. Alle Felder müssen vorkommen und doppelte oder unbekannte Mitglieder werden abgelehnt. `sha256` ist der kleingeschriebene Hexdigest der Gewichte und `sizeBytes` deren exakte Bytezahl. Herausgeber, unveränderliche Quellrevision und Lizenz werden im Manifest deklariert. Die Freigabe muss den exakten Manifest-SHA-256 pinnen und die Herkunft und Lizenz gesondert belegen.

| Feld | Inhalt |
| --- | --- |
| `format`, `version` | `meowchan-local-tagger`, `1` |
| `publisher`, `modelId`, `revision`, `license` | Herausgeber, Modellkennung, Quellrevision und Lizenzkennung |
| `sizeBytes`, `sha256` | Länge und SHA-256 der Datei `model.onnx` |
| `tags` | Geordnetes Array aus `name` und `category`. Position im Array entspricht dem Ausgabewert. Zulässige Kategorien sind `unknown`, `general`, `artist`, `copyright`, `character`, `meta`, `rating`. |
| `preprocessing.inputName`, `outputName` | Tensor-Namen |
| `preprocessing.layout`, `width`, `height`, `color` | Float32-Eingabe für ein Bild, `NCHW` oder `NHWC`, positive Zielabmessungen, `RGB` oder `BGR` |
| `preprocessing.resize`, `interpolation`, `pad` | `stretch` mit `null` oder `fit-center` mit drei ganzzahligen Füllwerten in Farbreihenfolge. Die Interpolation ist `nearest`, `bilinear`, `bicubic`. |
| `preprocessing.scale`, `mean`, `standardDeviation`, `scoreTransform` | Normalisierung je Kanal als `(pixel * scale - mean) / standardDeviation`. `identity` oder `sigmoid` bestimmt die Überführung der Modellausgabe in den vom SDK getragenen Rohscore. |

Die Grenzen liegen in `LocalTaggerModelSettings` und gelten vor der Verarbeitung. Für die erste Integration verwendet der Desktop die Standardwerte.

| Setting | Default | Min | Max |
| --- | ---: | ---: | ---: |
| Manifest (KiB) | 1 024 | 64 | 8 192 |
| Gewichte (MiB) | 1 024 | 64 | 2 048 |

## Import und Worker

Der Host verifiziert den Manifest-SHA aus der Vorschau erneut und kopiert die Gewichte mit begrenztem Puffer. Nach Abgleich von Bytezahl und SHA-256 veröffentlicht er den neuen Slot per atomarem Wechsel der aktiven Referenz. Ein falscher Hash, ein Größenfehler oder ein Abbruch vor der Veröffentlichung lässt den alten Slot aktiv. Bei der nächsten Benutzung werden Manifest und Gewichte erneut verifiziert.

Der interne Worker-Pfad bindet den freigegebenen Manifest-SHA, Adapter und Ressourcen an dieselbe Ausführung. Der Worker nutzt das bestehende Bild-IPC und erhält nur Tokens für den laufenden Batch. Host-HTTP ist für diesen Vorgang gesperrt. Der Runner speichert den Rohscore und Modell-ID, Revision sowie Gewichte-SHA im bestehenden Vorschlagsspeicher. Der Host beendet bei Bedarf einen Worker, der auf Abbruch nicht reagiert. Der Worker läuft mit Nutzerrechten.

Der Testadapter berechnet einen deterministischen Score aus synthetischen Modell- und Bildbytes und läuft nur in den Laufzeittests. Offen bleiben die Freigabe eines konkreten, lizenzgeprüften ONNX-Pakets, dessen CPU-Adapter und die Messung von Speicherbedarf und Laufzeit auf den Zielarchitekturen. Erst dann kann der Desktop CPU-Tagging für echte Bilder anbieten.

## Lokalisierungsaudit

Die 19 Schlüssel `localTagger.*` sind im eingebauten Englisch und Deutsch sowie in jedem der zehn Vorschaupakete vorhanden. Es gibt keine neuen exakten Englischübereinstimmungen.

| Katalog | Neue Schlüssel | Gesamt |
| --- | ---: | ---: |
| en, de | je 19 | je 5 378 |
| cs | 19 | 5 422 |
| es | 19 | 5 374 |
| fr | 19 | 5 374 |
| it | 19 | 5 380 |
| ja | 19 | 5 272 |
| ko | 19 | 5 272 |
| pl | 19 | 5 423 |
| pt-BR | 19 | 5 380 |
| ru | 19 | 5 400 |
| zh-Hans | 19 | 5 272 |
