# Sammlungs-Workflow — aktuelle Vorschau

**Status:** unveröffentlichter Zwischenstand. Diese Seite beschreibt nur Funktionen, die in der aktuellen Arbeitskopie sichtbar sind. Sie ist keine vollständige Import-/Exportlösung, keine Grabber-Alternative und keine native Plattformabnahme.

## Was heute nutzbar ist

MeowChan verwahrt bereits gespeicherte Chan-Medien lokal. Sie können sie im Archiv und in der Galerie ansehen, navigieren und als Medienfavoriten markieren. Threads lassen sich zusätzlich als Threadfavoriten markieren. Ein favoritenbezogener Archiv-Überblick kann vorhandene Favoriten nach aktuellem, ausgewähltem, gefiltertem oder ganzem Archivumfang suchen, lokal prüfen, anzeigen, entfernen und die angezeigten Originale exportieren. Lokales Ansehen und Prüfen allein startet keinen Download.

## Danbooru durchsuchen

Öffnen Sie **Mehr → Booru** in der Hauptnavigation, geben Sie Tags ein und wählen Sie **Suchen**. Sie erreichen die Seite auch über **Medien → Mehr → Booru öffnen**. Das Danbooru-Plugin muss unter **Plugins → Alle Plugins** verfügbar und aktiviert sein. Falls Booru ausgeblendet ist, aktivieren Sie **Booru-Aktionen anzeigen** unter **Einstellungen → Oberfläche → Fußleiste**.

Die Suche liefert öffentliche Danbooru-Posts. Sie können Treffer in der Vorschau ansehen und ausgewählte Originale ausdrücklich zum Download einreihen. Die Bedienwege stehen unter [Danbooru-Suche und Suchbeobachtungen](../Help.md#danbooru-search-and-watchers).

## Gespeicherte Dateinamen im Archiv suchen

In der Archivaktionsleiste und in der Readerleiste öffnet **Erfasste Dateinamen suchen** einen eigenen Dialog. Diese Metadatensuche ist nicht die Threadtextsuche. Text eingeben und **Suchen** wählen; mit **Vorherige Seite** / **Nächste Seite** blättern.

- Gesucht wird ein wörtlicher Teiltext des gespeicherten `original_name`-Claims. Groß-/Kleinschreibung wird ignoriert; der Eingabetext wird nicht getrimmt, Leerzeichen bleiben erhalten, und Wildcards sowie Pfad- oder Suchsprachen werden nicht interpretiert. Eine leere Eingabe fordert ausdrücklich alle erfassten Namen an. Erlaubt sind höchstens 512 UTF-16-Codeeinheiten und kein NUL.
- Jede Ergebnisseite liest Gesamtzahl und Zeilen in derselben Katalogtransaktion. Ein Seitenwechsel ist eine neue Anfrage und kann daher einen neueren kommittierten Katalogstand sehen.
- Die Zeilen zeigen Quelle, Board, Thread, Beitrag, Anhangposition, Variante, Anhang-/Katalogrevision und Löschbeobachtungen. Gelöschte Beobachtungen bleiben suchbar. Dabei wird keine Datei und kein Pfad geöffnet; ein Treffer belegt weder aktuelle Medienbytes noch aktuelle Dateiexistenz.
- **Gewählte Metadaten zurückgeben** liest den Zielthread nach der Auswahl mit einem neuen passiven Reader und prüft Quelle, Board, Thread, Katalogthreadrevision, Beitrag samt Löschzustand sowie Anhangposition, Variante, Originalnamen, Anhangrevision und Anhanglöschzustand. Der Zielbeitrag wird erst nach dieser erneuten Prüfung geladen und angezeigt. Bei Abweichung wird der Zielort stattdessen als lokal nicht verfügbar gemeldet. Es gibt keinen First-Post-Fallback, keine Galerie-, Pfad- oder URL-Rekonstruktion, keine Dateiverifikation, keinen Netzwerkabruf und keine aktuelle Dateisystem-, globale Bibliotheks- oder Ähnlichkeitssuche.

## Text im gespeicherten Thread suchen

In der Readerleiste öffnet **Im gespeicherten Thread suchen** die Textsuche für den geöffneten gespeicherten Thread: Betreff und Beitragstext, auch außerhalb der gerade sichtbaren Readerseite. Bei erreichten Beitrags-, Text- oder Treffergrenzen meldet die Oberfläche unvollständige Ergebnisse. Leere Eingabe sucht nicht; höchstens 512 UTF-16-Codeeinheiten und kein NUL sind erlaubt. Innerhalb der Suchleiste führt **Enter** zum nächsten, **Umschalt+Enter** zum vorigen Treffer; **Escape** schließt sie. Navigation wählt den Beitrag; die genaue Fundstelle wird in der Suchleiste gezeigt, auch wenn sie über das Anzeigelimit des Beitragstexts hinausgeht.

## In der Galerie auswählen und favorisieren

Die folgenden Tasten sind die Standardbelegungen der Galerie; eigene gespeicherte Shortcut-Anpassungen haben Vorrang. M/U/F funktionieren auch bei ausgeblendeten Galerie-Bedienelementen:

| Taste | Wirkung |
| --- | --- |
| **M** | Aktuelles Medium ausdrücklich als Favorit aufnehmen |
| **U** | Favoritenmarkierung des aktuellen Mediums ausdrücklich entfernen |
| **F** | Favoritenmarkierung umschalten; bei einem gespeicherten Favoriten fragt die Galerie vor dem Entfernen nach |
| **↑ / ↓** | Videolautstärke erhöhen oder verringern, nur bei verfügbarer Tonwiedergabe |

Das Favoritenzeichen oben links neben **Zurück zu …** zeigt **★** einen gespeicherten Favoriten und **☆** ein nicht favorisiertes Medium. Während des Speicherns erscheint **…**. Die Anzeige bleibt auch bei ausgeblendeten Galerie-Bedienelementen sichtbar. Ein Klick auf das Zeichen schaltet die Markierung um. Wenn der gespeicherte Zustand eines neu gewählten Mediums noch nicht gelesen ist, wird keine unbegründete „kein Favorit“-Aussage angezeigt.

Die Favoritenübersicht löst die gewählte Menge gegen gespeicherte lokale Manifeste auf. Fehlende oder nicht verifizierbare Dateien bleiben Favoriten, werden aber als nicht verfügbar gemeldet und nicht exportiert. Der Originalexport friert die angezeigte Auswahl ein, meldet fehlende Dateien vor dem Kopieren und kopiert nur lokal verifizierte Dateien. Dabei wird kein Netzwerkabruf gestartet. Favoritenmarkierungen selbst werden dadurch nicht in ein anderes Profil übertragen.

## Transferpakete prüfen und übernehmen

Öffnen Sie im Archivkopf **Archiv importieren…** und wählen Sie über **Archiv wählen…** eine vorhandene ZIP-Datei. MeowChan prüft das Paket und zeigt den Bestand mit Threads, Posts und Medien an. Erst **Importieren** übernimmt den angezeigten Plan.

- Beim Zusammenführen werden bestehende Threads ergänzt. Medienkonflikte werden gemeldet. Nicht unterstützte Pakete und veraltete Pläne werden abgelehnt.
- Die Prüfung und Übernahme laufen lokal.
- Die Prüfung nutzt ein temporäres Arbeitsverzeichnis. MeowChan meldet zurückgebliebene temporäre Daten oder unklare Bereinigungszustände.
- Nach einem Abbruch oder einer unterbrochenen Antwort zeigt der Wiederherstellungszustand, was übernommen wurde. Das erneute Anwenden derselben Operation erzeugt keine doppelten Schreibvorgänge.

Über **Archiv exportieren…** erstellen Sie Transferpakete für ausgewählte Threads oder das gesamte Archiv. Zur Wahl stehen **Nur Metadaten**, **Originale** und **Alles**. Der Export von Originaldateien aus dem Favoritenüberblick ist eine eigene Aktion.

## Videolautstärke, Mute und Wiederholen

**↑/↓** ändert nur die Lautstärke eines Videos mit tatsächlich verfügbarer Tonwiedergabe. Der Wert bleibt im Bereich 0–100, wird gespeichert und ändert nicht zwischen Wiedergabe und Pause. Eine positive Lautstärkeänderung hebt Stummschaltung auf; Lautstärke 0 allein entscheidet nicht automatisch über Mute.

Ton aus/an und Wiederholen sind Bedienelemente des Videobereichs. Im Standardprofil schaltet **Umschalt+M** Ton aus/an und **L** die Wiederholung. Eine manuelle Mute-/Unmute-Wahl bleibt über Vor/Zurück- und Bild-/Videowechsel innerhalb derselben geöffneten Galerie erhalten. Beim erneuten Öffnen einer Galerie gilt wieder die gespeicherte Startpräferenz **Videos stumm starten**.

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

Englisch und Deutsch sind eingebaut. Zehn [mitgelieferte Sprachvorschauen](../../Desktop/Localization/Packs/README.md) lassen sich ohne separaten Download in den Einstellungen auswählen.

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

## Weitere lokale Sammlungswerkzeuge

- **SHA-256:** Hat der gespeicherte Thread genau einen Medienbestand, wählen Sie im Archiv oder Reader den Suchbereich **SHA-256** und geben Sie einen exakten Hash ein. Die Suche verwendet die erfassten Hashwerte dieses Bestands.
- **Eigene Tags:** Sie können Threads und Medien taggen und die Medienseite nach eigenen Tags filtern. Die [Hilfe zu eigenen Tags](../Help.md#own-tags-and-the-shared-media-menu) beschreibt die Bedienwege.
- **Lokale Dateien:** Über **Dateien importieren…** können Sie [eigene Dateien](../Help.md#importing-your-own-files) in die Medienbibliothek aufnehmen.

Transferpakete nur für Favoriten und der Import fremder Sammlungsformate gehören derzeit nicht zur Vorschau.
