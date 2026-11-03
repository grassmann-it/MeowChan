# Zwischenablage und Downloadgrenzen

## Zwischenablage freiwillig prüfen lassen

Die automatische Prüfung ist **standardmäßig ausgeschaltet**. Unter **Einstellungen → Verhalten** können Sie **„Zwischenablage nach URLs durchsuchen“** aktivieren und die Änderung speichern. Zum Ausschalten entfernen Sie das Häkchen und speichern erneut. Das Ausschalten beendet die automatische Prüfung und entfernt den noch offenen Vorschlag; bereits hinzugefügte Downloads werden dadurch nicht entfernt oder abgebrochen.

Bei eingeschalteter Funktion prüft die laufende App die Zwischenablage ungefähr alle zwei Sekunden, sofern die Sitzung bereit ist und kein blockierender Dialog oder Vorgang die Prüfung verhindert. Auch eine im Hintergrund oder im Tray weiterlaufende App kann prüfen. MeowChan holt sich dafür nicht selbst in den Vordergrund. Im minimierten oder versteckten Hauptfenster wird kein Vorschlag angezeigt; auch bestimmte Reader-/Galerieansichten unterdrücken die Anzeige.

War die Funktion bereits beim Appstart eingeschaltet, dient die erste zulässige Lesung nur als Ausgangsstand: unveränderter Startinhalt wird auch nach später geladenen Quellen nicht vorgeschlagen. Sobald ein anderer, innerhalb der Textgrenze liegender Inhalt gelesen wurde, gilt wieder die normale Erkennung. Ein ausdrückliches Ausschalten und erneutes Einschalten mit Speichern prüft dagegen auch den aktuellen Inhalt.

### Einen Vorschlag bestätigen oder verwerfen

Erkannte, unterstützte und noch nicht beobachtete Thread-/Board-URLs erscheinen in einem Hinweisbereich im Hauptfenster mit Anzahl und schreibgeschützter URL-Vorschau. Dieser Hinweis ist kein automatisch öffnendes Bestätigungsfenster.

- Prüfen Sie die Liste und klicken Sie auf **„URLs hinzufügen (Enter)“**. Für die Tastaturbedienung fokussieren Sie diese Schaltfläche, beispielsweise mit Tab, und drücken dann **Enter** (oder die Leertaste).
- **Enter ist keine globale Zustimmung:** Enter in der URL-Vorschau oder einem anderen Eingabefeld bestätigt den Vorschlag nicht. Während der Aufzeichnung eines Tastenkürzels ist ebenfalls keine Bestätigung möglich.
- Mit **„Verwerfen“** lehnen Sie den Vorschlag ab. Escape innerhalb des Hinweisbereichs verwirft ihn ebenfalls. Verwerfen verändert den Inhalt der Zwischenablage nicht.

Erst die ausdrückliche Bestätigung übergibt die angezeigte URL-Liste an den normalen Hinzufügen-Ablauf. Dabei wird die Zwischenablage nicht erneut gelesen. Die üblichen Prüfungen und gegebenenfalls weitere Board-Entscheidungen gelten weiterhin; Downloads können danach starten, sofern der normale Ablauf dies zulässt. Eine Bestätigung garantiert weder die Aufnahme jeder URL noch einen sofortigen Downloadstart.

### Welche URLs werden vorgeschlagen?

Die automatische Erkennung berücksichtigt nur HTTP-/HTTPS-URLs mit einem exakt unterstützten Host einer registrierten Quelle und einem von ihr erkannten Ziel. Andere Webseiten, andere Protokolle, URLs mit eingebetteten Zugangsdaten und bereits beobachtete Ziele werden nicht vorgeschlagen. Ein ausbleibender Vorschlag bedeutet deshalb nicht, dass die Zwischenablage leer ist.

Die Prüfung ist bewusst begrenzt: Texte mit mehr als 65.536 UTF-16-Codeeinheiten (64 Ki Zeichen, kein 64-KiB-Bytelimit) werden vollständig von der Auswertung ausgeschlossen, nicht auf einen Präfix gekürzt. Der Plattformzugriff kann den vollständigen Text bereits vor dieser Längenprüfung in den Speicher laden; pro Text werden höchstens die ersten 32 gefundenen HTTP-/HTTPS-URL-Kandidaten geprüft. Ungültige oder nicht unterstützte Kandidaten können diese Anzahl mit ausschöpfen. Das ist **keine** einstellbare Downloadgrenze und keine Garantie, alle Links eines langen Textes zu finden. Gleiche Vorschlagslisten werden während der laufenden Überwachung nach Bestätigung oder Verwerfen begrenzt im Speicher wiedererkannt und nicht ständig erneut angeboten; dies ist keine dauerhafte Sperrliste.

### Datenschutz und manuelles Hinzufügen

Aktivieren Sie die Funktion nur, wenn die laufende App den Text Ihrer Zwischenablage prüfen darf. Der Monitor führt keine dauerhafte Zwischenablagehistorie; Vorschläge und Wiedererkennung liegen im Arbeitsspeicher. Der passive Fehlerpfad protokolliert selbst keine Zwischenablageinhalte und zeigt bei vorübergehend fehlendem Zugriff keine fortlaufenden Fehlermeldungen an. Das ist keine pauschale Zusage, dass niemals eine URL in Diagnosedaten vorkommt: Wenn optionale Performance-Diagnostik aktiv ist, können klassifizierte URL-Ziele bereits vor der Bestätigung in deren begrenztem In-Memory-Diagnoselauf enthalten sein. Eine erfolgreiche URL-Erkennung ist keine Zusage, dass die Quelle erreichbar oder der Zugriff vom Betriebssystem erlaubt ist. Nach bestätigtem Hinzufügen gelten die normalen Speicher- und Netzwerkabläufe für beobachtete Ziele und Downloads.

Bei ausgeschalteter Automatik bleibt **manuelles** Hinzufügen möglich: „Hinzufügen“ bei leerem URL-Feld liest die Zwischenablage einmal; mit ausgefülltem Feld wird nur dessen Text verwendet. Die Tray-Aktion **„URLs aus Zwischenablage hinzufügen“** importiert ebenfalls ausdrücklich. Strg+V beziehungsweise Cmd+V im Downloads-Bereich außerhalb nativer Eingabesteuerelemente kann den Import auslösen; in Textfeldern bleibt es normales Einfügen. Diese manuellen Aktionen sind nicht die passive Vorschlagsbestätigung und setzen deren Opt-in nicht voraus.

## Parallele Downloads und Ressourcenobergrenze

In den Einstellungen gibt es zwei unterschiedliche Werte:

| Einstellung | Bedeutung |
| --- | --- |
| **Parallele Downloads** | Gewünschte Zahl gleichzeitig laufender Mediendownloads; eine positive ganze Zahl. |
| **Ressourcenobergrenze für Mediendownloads** | Zusätzliche Sicherheitsgrenze für gleichzeitig aktive/reservierte Mediendownload-Slots: **1–256**, **Standard 32**. |

Wirksam ist immer der **kleinere** der beiden Werte: `min(gewünschte Parallelität, Ressourcenobergrenze)`. Diese Grenze zählt Transfer-Slots und begrenzt dadurch indirekt gleichzeitig genutzte Transfer-/Buffer-Ressourcen. Sie misst oder garantiert kein festes Byte- oder RAM-Budget. Beispiele:

- Parallele Downloads 8, Ressourcenobergrenze 32 → höchstens 8 gleichzeitig zugelassene Transfers.
- Parallele Downloads 64, Ressourcenobergrenze 32 → höchstens 32.
- Parallele Downloads 64, Ressourcenobergrenze 64 → bis zu 64, soweit Arbeit verfügbar ist und die übrigen Grenzen es erlauben.

Um mehr als 32 Transfers zuzulassen, müssen **beide Werte** über 32 liegen. Das Erhöhen allein der Ressourcenobergrenze erhöht nicht automatisch den gewünschten Parallelwert. Hohe Werte benötigen mehr Ressourcen und garantieren keine höhere Geschwindigkeit.

**Speichern wirkt sofort, ohne Neustart.** Eine Erhöhung kann wartende Arbeit freigeben. Beim Senken werden bereits laufende Transfers nicht nur zur Durchsetzung der neuen Grenze abgebrochen oder neu gestartet: Sie dürfen fertig werden. Deshalb kann die Zahl aktiver Transfers vorübergehend über dem neuen wirksamen Wert liegen; neue Starts warten auf freie Kapazität.

Die Sicherheitsgrenze ist weder ein Limit für beobachtete Threads noch die Zahl wartender Dateien, eine Dateigrößen-/Speicherplatzgrenze oder eine Bandbreiteneinstellung. Sie ersetzt die normalen Grenzen nicht: Quellenlimits und die eingestellte Bandbreitenbegrenzung gelten weiterhin. Ebenso unabhängig davon ist die feste Begrenzung der automatischen Zwischenablageerkennung auf 32 URL-Kandidaten.
