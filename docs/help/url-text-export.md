# Seiten-URL als Text exportieren / Export a page URL as text

## Deutsch

Dieser Export speichert **eine Seiten-URL eines einzelnen gespeicherten
4chan- oder 8kun-Archivthreads**, nicht dessen Inhalte.

1. Wähle im Archiv genau einen sichtbaren gespeicherten Thread und öffne im
   Auswahl-/Kontextmenü den Export der Seiten-URL als Text. Bei Mehrfachauswahl
   oder während einer anderen sperrenden Archivoperation ist der Einstieg nicht verfügbar.
2. Prüfe im Bestätigungsdialog Quelle, Board, Thread-ID und die angezeigte URL
   sowie Umfang und Datenschutzhinweise. Erst deine ausdrückliche Bestätigung
   zum Speichern öffnet die Dateiauswahl. Die ausgewählte Threadidentität und
   der daraus geprüfte gespeicherte Link bleiben für diesen Vorgang fest;
   eine spätere Auswahländerung exportiert keinen anderen Thread.
3. Wähle einen **neuen lokalen Dateipfad** in einem vorhandenen zulässigen Ordner.
   Vorgeschlagen wird `page-links.txt`. Die Textdatei enthält nur die geprüfte
   gespeicherte URL als eine UTF-8-Zeile ohne BOM mit abschließendem Zeilenumbruch.
   Bestehende Dateien werden **nicht überschrieben**, auch nicht nach einer
   Überschreibbestätigung des Betriebssystemdialogs. Wähle dann einen anderen Namen
   und starte den Export erneut. Es gibt keinen automatischen Wiederholungsversuch;
   für einen weiteren Versuch öffnest und bestätigst du den Export neu.

**Nur ein Link:** Keine Medien oder Offlineinhalte, keine privaten Tags,
Favoriten, Notizen, Jobs oder Zugangsdaten werden mitgegeben. Die URL selbst kann
Leseinteressen offenlegen; teile die Datei nur bewusst mit vertrauenswürdigen Empfängern.
Fehlt die gespeicherte Threadübersicht oder ist der Link ungültig, nicht unterstützt
oder nicht exakt der Quelle, dem Board und dem Thread zuzuordnen, wird nichts
exportiert. MeowChan rekonstruiert oder repariert den Link nicht. Dieser Export
umfasst keine Boards, einzelnen Postanker, Mehrfachauswahl oder gesamte Ergebnislisten.

**Abbruch und unklare Ergebnisse:** Schließen/Abbrechen vor der Bestätigung oder
Abbruch der Dateiauswahl startet keinen Schreibvorgang. Nach Schreibbeginn ist
Abbruch nur nach bestem Bemühen möglich: Eine möglicherweise unvollständige Datei
kann zurückbleiben. Auch wenn die Dateierstellung nicht bestätigt werden konnte,
kann am Ziel eine Datei existieren. MeowChan löscht sie nicht automatisch. Prüfe
vor einem neuen Versuch das Ziel und seinen Inhalt; lösche eine Datei nicht allein
aufgrund einer Fehler- oder Abbruchmeldung. Schließen des Dialogs ist kein
Erfolgsnachweis und keine Zusage, dass keine Datei existiert.

**Keine Download- oder Transferzusage:** MeowChan startet durch diesen Export
keinen Zielclient, erstellt keine Jobs und lädt nichts herunter. Wenn du die Datei
später einem Zielprogramm übergibst, entscheidet dieses über Annahme,
Authentifizierung und mögliche Downloads. Der Export prüft keine Erreichbarkeit:
Auch ein offline gespeicherter oder inzwischen gelöschter Thread kann einen
zulässigen gespeicherten Link haben, der heute nicht mehr funktioniert. Die Datei
rettet keine toten Links. Dies ist **kein nativer MeowChan-Archivtransfer** und kein
Nachweis eines Grabber-/JDownloader-Roundtrips oder der Unterstützung durch einen
bestimmten Client. Ein lokaler Pfad ist zudem keine Garantie für ein physisch
lokales Laufwerk: Dateiauswahl, Netzmounts oder verbundene Laufwerke können
Betriebssystem-Netzwerkzugriffe verursachen.

## English

This export saves **one page URL from one saved 4chan or 8kun archive thread**,
not the thread's contents.

1. In the archive, select exactly one visible saved thread and open the page-URL
   text export from its selection/context menu. It is unavailable for multiple
   selections or while another blocking archive operation is active.
2. Review the source, board, thread ID, displayed URL, scope and privacy notices
   in the confirmation dialog. Only your explicit confirmation to save opens
   the file picker. The selected thread identity and its validated stored link
   stay fixed for this operation; a later selection change does not export a
   different thread.
3. Choose a **new local file path** in an existing permitted folder.
   The suggested name is `page-links.txt`. The text file contains only the validated
   stored URL as one UTF-8 line without a BOM, with a final newline.
   Existing files are **never overwritten**, even after an operating-system
   overwrite confirmation. Use a different name and start the export again.
   There is no automatic retry; reopen and confirm the export for another attempt.

**A link only:** No media or offline content, private tags, favorites, notes,
jobs or credentials are included. The URL itself may reveal your reading interests;
share the file deliberately and only with trusted recipients. If the saved thread
summary is missing, or its link is invalid, unsupported or does not exactly match
the source, board and thread, nothing is exported. MeowChan does not reconstruct
or repair the link. This export does not cover boards, individual post anchors,
multiple selections or whole result lists.

**Cancellation and uncertain outcomes:** Closing/canceling before confirmation,
or canceling the file picker, starts no write. After writing starts, cancellation
is best effort: a possibly incomplete file may remain. A file may also exist when
creation could not be confirmed. MeowChan does not automatically delete it. Check
the destination and its contents before trying again; do not delete a file solely
because an error or cancellation was reported. Closing the dialog is neither
proof of success nor a promise that no file exists.

**No download or transfer promise:** This export starts no target client,
creates no jobs and downloads nothing. If you later give the file to another
program, that program decides whether to accept it, authenticate or download.
The export does not check reachability: an offline-saved or now-deleted thread may
still have a valid stored link that no longer works. The file cannot recover dead
links. This is **not a native MeowChan archive transfer**, nor evidence of a
Grabber/JDownloader roundtrip or support by a particular client. A local path is
also not proof of a physically local drive: the file picker, network mounts or
mapped drives may cause operating-system network access.
