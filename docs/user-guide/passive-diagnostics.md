# Passive Diagnose — Vorschau für den nächsten Release

**Status:** Diese Anleitung beschreibt die aktuelle Umsetzung für den kommenden
Releasekandidaten. Die Erweiterung ist noch nicht veröffentlicht; die vollständige
plattformübergreifende Releaseabnahme steht noch aus.

## Einen Eintrag verstehen

Öffnen Sie die vorhandene Seite **Diagnose** und wählen Sie unter **Aktuelle
Anfragen und Probleme** einen Eintrag. Die Ansicht zeigt den aktuellen Zustand,
keine vollständige Anfragehistorie. Die ergänzten Details trennen:

- **Vorgang:** etwa Threadprüfung, Board-Katalogprüfung, Mediendownload,
  Beitragsspeicherung oder Medienauswahl/Filter.
- **Beobachtetes Ergebnis:** vorhandene Fehlerfakten, HTTP-Status und gegebenenfalls
  Diagnosecode oder gemeldeter Wiederholungszeitpunkt.
- **Nächster Schritt:** ein Hinweis, beispielsweise auf Quellenprüfung, Speicherziel
  oder eine vorhandene Wiederholungsaktion. Der Hinweis führt nichts selbst aus.

**Beobachtete URL (nicht unbedingt der fehlgeschlagene Endpunkt)** bezeichnet den
beobachteten Thread-/Boardkontext. Sie ist nicht zwingend die Adresse des
fehlgeschlagenen Medienabrufs oder dessen CDN-Servers.

## Meldungen richtig einordnen

| Anzeige | Bedeutung und Grenze |
| --- | --- |
| **HTTP 403** | Der Server hat die Anfrage abgelehnt. Das allein beweist weder eine Sperre noch ein Loginproblem. Prüfen Sie Quellenkonfiguration und gemeldeten Status vor einem erneuten Versuch. |
| **HTTP 404 bei einer Mediendatei** | Diese Datei wurde nicht gefunden. Daraus folgt nicht, dass der Thread verschwunden ist. |
| **HTTP 429** | Der Server drosselt Anfragen. Der Code allein belegt weder einen aktuell laufenden Cooldown noch eine bereits geplante Wiederholung. |
| **DNS-, Verbindungs- oder TLS-Fehler** | Diese Einordnung erscheint nur bei entsprechend typisierten Fehlerdaten, nicht aufgrund einzelner Wörter im Fehlertext. Ein TLS-Fehler beweist insbesondere keinen Zertifikatsfehler. |
| **Abbruch oder Zeitüberschreitung** | Ein Abbruch ist kein nachgewiesener Netzwerk-Timeout. Eine abgelaufene Frist benennt nicht automatisch die betroffene Netzwerkphase. |
| **Unbekannte Ursache** | Die verfügbaren Daten reichen nicht aus. Fehlende Zusatzdaten, etwa nach einem Neustart, werden nicht nachträglich aus alten Meldungen rekonstruiert. |

Lokale Speicherprobleme, nicht verarbeitbare Daten und Filterentscheidungen sind
nicht automatisch Netzwerkfehler. Folgen Sie dem passenden Hinweis; bei einem
Speicherproblem etwa Speicherziel und freien Platz prüfen.

## Warten anzeigen und erneut versuchen

Bei aktiviertem **Nur Probleme** ergänzt **Auch wartende Vorgänge anzeigen** die
Liste um Anfragezeilen und ausstehende Arbeit. Die Zusatzoption ist zunächst aus;
Quellen- und Textfilter gelten weiter. Ohne **Nur Probleme** sind diese Zeilen
bereits enthalten. Sichtbarkeit macht einen Wartezustand nicht zum Fehler und
nimmt ihn nicht automatisch in eine Sammelwiederholung auf.

Achten Sie auf den konkreten Status: **Wartet auf Anfragefreigabe** und
**Anfragepause** beschreiben tatsächliches Warten, **Wird geprüft** einen laufenden
Abruf. Ein **Gemeldeter Wiederholungszeitpunkt** allein bestätigt keinen aktuellen
Wiederholungsplan und verspricht keinen Erfolg.

Nutzen Sie nur verfügbare Wiederholungsaktionen. Warteschlange, Pause,
Quellenlimits und Anfragefreigabe bleiben unverändert; umgehen Sie keine
Anfragepause. Sammelaktionen gelten für die beim Start erfassten Threads/Boards,
nicht für die gesamte Sitzung. Medienwiederholungen betreffen fehlende Dateien
dieser Threads, nicht zwingend nur die ausgewählte Datei. „Eingeplant“ bedeutet
nicht „behoben“.

## Details vorsichtig teilen

**Details kopieren** kann unveränderte URLs, lokale Pfade und rohe Fehlermeldungen
in die Zwischenablage übernehmen. Diese Werte können privat sein. Prüfen und
bereinigen Sie den Text selbst, bevor Sie ihn weitergeben: Dies ist **kein
bereinigter oder anonymisierter Supportbericht**.

Die passive Diagnose fügt keine automatische Telemetrie oder aktiven
Verbindungsproben hinzu. Anzeigen, Aktualisieren und Kopieren starten keine
zusätzlichen Netzabfragen; normale Downloads können im Hintergrund weiterlaufen.
