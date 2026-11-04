# Sammlungs-Workflow — aktuelle Vorschau

**Status:** unveröffentlichter Zwischenstand. Diese Seite beschreibt nur Funktionen, die in der aktuellen Arbeitskopie sichtbar sind. Sie ist keine vollständige Import-/Exportlösung, keine Grabber-Alternative und keine native Plattformabnahme.

## Was heute nutzbar ist

MeowChan verwahrt bereits gespeicherte Chan-Medien lokal. Sie können sie im Archiv und in der Galerie ansehen, navigieren und als Medienfavoriten markieren. Threads lassen sich zusätzlich als Threadfavoriten markieren. Ein favoritenbezogener Archiv-Überblick kann vorhandene Favoriten nach aktuellem, ausgewähltem, gefiltertem oder ganzem Archivumfang suchen, lokal prüfen, anzeigen, entfernen und die angezeigten Originale exportieren. Lokales Ansehen und Prüfen allein startet keinen Download.

Booru-Suche, Booru-Sammlungen, ein universeller Sammlungs-Browser und ein Import-/Apply-Ablauf gehören weiterhin nicht zum freigeschalteten Umfang.

## In der Galerie auswählen und favorisieren

Die folgenden Tasten sind die Standardbelegungen der Galerie; eigene gespeicherte Shortcut-Anpassungen haben Vorrang. M/U/F funktionieren auch bei ausgeblendeten Galerie-Bedienelementen:

| Taste | Wirkung |
| --- | --- |
| **M** | Aktuelles Medium ausdrücklich als Favorit aufnehmen |
| **U** | Favoritenmarkierung des aktuellen Mediums ausdrücklich entfernen |
| **F** | Favoritenmarkierung umschalten (Kompatibilitätskürzel) |
| **↑ / ↓** | Videolautstärke erhöhen oder verringern, nur bei verfügbarer Tonwiedergabe |

Oben rechts zeigt **★** einen gespeicherten Favoriten und **☆** ein nicht favorisiertes Medium. Während des Speicherns erscheint **…**. Die Anzeige bleibt auch bei ausgeblendeten Galerie-Bedienelementen sichtbar. Das Zeichen ist nur eine Anzeige und kein klickbares Bedienelement. Wenn der gespeicherte Zustand eines neu gewählten Mediums noch nicht gelesen ist, wird keine unbegründete „kein Favorit“-Aussage angezeigt.

Die Favoritenübersicht löst die gewählte Menge gegen gespeicherte lokale Manifeste auf. Fehlende oder nicht verifizierbare Dateien bleiben Favoriten, werden aber als nicht verfügbar gemeldet und nicht exportiert. Der Originalexport friert die angezeigte Auswahl ein, meldet fehlende Dateien vor dem Kopieren und kopiert nur lokal verifizierte Dateien. Dabei wird kein Netzwerkabruf gestartet. Favoritenmarkierungen selbst werden dadurch nicht in ein anderes Profil übertragen.

## Transferpaket prüfen — nur Inventar

Im Archivkopf können Sie **Transferpaket prüfen …** öffnen und eine vorhandene lokale ZIP-Datei wählen. MeowChan prüft sie mit dem produktiven Paketleser und zeigt ein begrenztes Inventar: Threads, Beiträge, eindeutige Payloads, Bytegröße sowie Status vorhandener Originale, Vorschauen und erhaltener Bindungen.

Diese Funktion ist bewusst nur eine Vorschau:

- Es wird nichts in Archiv oder Profil importiert, übernommen, zusammengeführt oder daraus gelöscht.
- Archiv, Profil, Store und Quelle bleiben unverändert; es gibt keinen Apply-Befehl.
- Private Transfermetadaten werden noch abgelehnt.
- Es wird kein Quellenanbieter oder Netzwerk benutzt.
- Die Prüfung schreibt eine temporäre Staging-Kopie in einen eigenen Arbeitsordner und versucht anschließend, die eigenen Staging-Dateien und Ordner zu entfernen. Prüfung und Abschluss warten auf diese Bereinigung; sie kann den Abschluss blockieren. Bleiben temporäre Daten zurück oder ist die Bereinigung unklar, wird das gemeldet. Archivdaten werden dabei nicht geändert.
- Ein 404-Ergänzungsimport oder Bestandsmerge ist nicht enthalten.

Die Oberfläche erstellt auch kein Transferpaket. Interne Archiv- und kuratierte Paketproduzenten sind derzeit technische Kernbausteine ohne Exportdialog.

## Videolautstärke, Mute und Wiederholen

**↑/↓** ändert nur die Lautstärke eines Videos mit tatsächlich verfügbarer Tonwiedergabe. Der Wert bleibt im Bereich 0–100, wird gespeichert und ändert nicht zwischen Wiedergabe und Pause. Eine positive Lautstärkeänderung hebt Stummschaltung auf; Lautstärke 0 allein entscheidet nicht automatisch über Mute.

Ton aus/an und Wiederholen sind Bedienelemente des Videobereichs. Im Standardprofil sind sie nicht zusätzlich mit eigenen Tastenkürzeln belegt. Eine manuelle Mute-/Unmute-Wahl bleibt über Vor/Zurück- und Bild-/Videowechsel innerhalb derselben geöffneten Galerie erhalten. Beim erneuten Öffnen einer Galerie gilt wieder die gespeicherte Startpräferenz **Videos stumm starten**.

Der Modus **Video wiederholen** ist eine gespeicherte Galerie-Wiedergabewahl. Bleibt er aktiv, gilt er für jedes ausgewählte Video, bis er geändert wird; er bleibt auch über einen Neustart gespeichert. Die Diashow muss weiterhin ausdrücklich gestartet werden.

## Optionale Zwischenablage-Beobachtung

**Zwischenablage nach URLs durchsuchen** ist standardmäßig aus. Erst Aktivieren und Speichern schaltet die Beobachtung ein. Sie läuft nur, solange MeowChan geöffnet und bereit ist.

Unterstützte Thread-/Board-URLs, die noch nicht beobachtet werden, erscheinen als passiver Vorschlag. Erkennung allein fügt keine URL hinzu und startet keinen Download. Mit **URLs hinzufügen (Enter)** wird genau die angebotene, eingefrorene Auswahl über den normalen Hinzufügen-Ablauf übergeben; **Escape** oder **Verwerfen** lehnt sie ab. Enter im Textvorschau-Feld oder einem anderen Bereich ist keine Bestätigung. MeowChan drängt sich dafür nicht in den Vordergrund und speichert keine Zwischenablage-Historie.

## Ressourcenobergrenze für Mediendownloads

**Ressourcenobergrenze für Mediendownloads** begrenzt gleichzeitig offene Mediendateitransfers und deren Speicherbedarf. Sie begrenzt nicht die Zahl beobachteter Threads oder wartender Dateien.

- Standard: **32**
- Erlaubter Bereich: **1–256**
- Wirksam ist der kleinere Wert aus **Parallele Downloads** und dieser Obergrenze.
- Parallele Downloads beginnt standardmäßig bei **5**; deshalb sind anfangs 5, nicht 32, Übertragungen wirksam.
- Für mehr als 32 gleichzeitige Übertragungen müssen Parallele Downloads und Obergrenze beide erhöht werden.
- Speichern wirkt sofort. Senken bricht laufende Übertragungen nicht ab; sie dürfen fertiglaufen, während kein neuer Slot über der Grenze belegt wird.

Quellenlimits, Bandbreite, Medienprüfung und Festplattenkapazität gelten unabhängig davon weiter. Der Download-Bereich und die Quickinfo zeigen konfigurierte und wirksame Parallelität getrennt.

## Sprachen

Neben Englisch und Deutsch sind **Español (Untested)**, **Français (Untested)** und **Русский (Untested)** direkt eingebunden und in Einstellungen auswählbar. Dafür muss kein Sprachpaket kopiert werden. Das bloße Einbinden ändert eine bestehende Sprachwahl nicht.

„Untested“ bedeutet hier: keine qualifizierte sprachliche oder native UI-Abnahme. Fehlende oder bewusst nicht übersetzte Flächen fallen auf Englisch zurück; einzelne Helfer und native Steuerungen können weiterhin Englisch zeigen. Aus dieser Vorschau folgt keine Windows-, macOS- oder sonstige Plattformabnahme.

## Früher, inerter Start der letzten Sitzung

Wenn Sitzungswiederherstellung aktiv ist und ein sauberer, vollständiger Abschluss einen Anzeigecache erzeugt hat, zeigt MeowChan beim nächsten Start früh eine deutlich gekennzeichnete, rein textliche Übersicht der letzten Sitzung: Zeit, Gesamtzahlen und begrenzte Zeilen mit Quelle, Board, Titel sowie Medienzahlen.

Diese Vorschau ist bewusst inert:

- Sie enthält keine URLs, Pfade, Verweise, Thumbnails oder Objekte der echten Sitzung.
- Sie erstellt keine Watches und löst kein Netzwerk aus.
- Aktionen und Arbeitsfläche bleiben gesperrt, bis die echte Wiederherstellung zuständig ist.
- Nach dem ersten vollständigen echten Datenrahmen verschwindet sie dauerhaft.
- Ein fehlender, beschädigter, zu großer oder unbekannter Cache wird ignoriert; die normale Wiederherstellung läuft weiter.

Nach einem Absturz, unsauberen Beenden oder ersten Start ohne geeigneten Cache gibt es diese Vorschau nicht. Sie ist keine Latenzgarantie und ersetzt keine Wiederherstellung.

## Noch nicht freigeschaltet

Die geplante Sammlungsrichtung — Booru-Suche, ausgewählte Originale, Offline-Bibliothek, kuratierte Ausgaben und ergänzender Import — ist Arbeit in Fortgang:

- **Booru:** Es gibt keine freigeschaltete Site-Suche, Quellenaufnahme, Sammlungsaufnahme oder Booru-Bibliothek. Eine interne, begrenzte Danbooru-Abfragesyntax existiert; sie ist kein Bedienelement, liest keine privaten Tags und stellt keinen Provider-, API- oder Netzwerkadapter dar.
- **Eigene Tags:** Interne, begrenzte Tagmodelle existieren, aber es gibt keine Tag-Benutzeroberfläche, keine dauerhafte eigene Tag-Sammlung, keine Tag-Suche und keinen Tag-Transfer. Token-Chips in den Regeln sind Regelbestandteile, keine privaten Tags.
- **Kuratierter Export:** Der interne Auswahl- und Projektionskern ist nicht in der Benutzeroberfläche freigelegt. Es gibt daher keinen bedienbaren „Favoriten plus kleine Vorschauen“-Export und keinen privaten Metadaten-Rundtrip.
- **Interner Import-Merge:** Interne Vorbereitungs- und Vergleichsarbeit ist nicht bedienbar. Vorhandene Bestände oder 404-Threads können derzeit nicht über die App importiert und ergänzt werden.
- **Fremdprogramme:** Es wird keine Grabber-, Hydrus-, gallery-dl- oder sonstige Fremdsammlungs-Kompatibilität behauptet.

Bestehende HTML- und Originalexporte bleiben eigene Ausgabefunktionen. Sie sind kein vollständiger App-Rundtrip, kein Profilbackup, kein Transferpaket-Erzeuger und keine Importzusage.
