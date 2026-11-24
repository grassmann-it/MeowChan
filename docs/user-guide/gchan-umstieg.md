# Von GChan zu MeowChan wechseln

[English](../Help.md#moving-from-gchan) · [MeowChan-Hilfe](../Help.md)

## Beobachtete Threads und Boards übernehmen

Wähle in GChan **Copy all thread URLs**. Die Zwischenablage enthält eine
kommagetrennte Liste in diesem Format:

```text
https://boards.4chan.org/g/thread/123,https://boards.4chan.org/g/thread/456
```

1. Füge deine eigene Liste in MeowChan unter **Downloads** ins URL-Feld ein
   und wähle **Hinzufügen**. Die Adressen oben zeigen das Format.

2. Prüfe das [Ergebnis je URL](../Help.md#add-results-and-rejected-urls).
   **Hinzugefügt** bedeutet, dass eine neue Beobachtung angelegt wurde.
   **Wird bereits beobachtet** verweist auf einen vorhandenen Eintrag. Wenn die
   Zusammenfassung **abgelehnt** meldet, klappe den Bericht auf. Eine Adresse
   ohne passende Quelle erscheint etwa als **Nicht unterstützte URL**. Mit
   **Abgelehnte zurück ins Feld** kannst du die Einträge korrigieren und erneut
   hinzufügen. Bei einer neuen, einer bereits beobachteten und einer nicht
   unterstützten URL lautet die Zusammenfassung **1 hinzugefügt · 1 bereits
   beobachtet · 1 abgelehnt**.

3. Übernimm Board-URLs separat. Füge zum Beispiel eine Adresse wie
   `https://boards.4chan.org/g/` ein und wähle **Hinzufügen**. Bei einem neuen
   Board öffnet sich standardmäßig die
   [Board-Vorschau](../Help.md#board-admission-and-exclusions).
   Prüfe, welche Threads angehakt sind, und bestätige die Auswahl mit
   **Alle Boards übernehmen und beobachten**.

MeowChans **Sitzungs-URLs kopieren (n)** kopiert beobachtete Threads und Boards
zeilenweise, einschließlich ausgefilterter Einträge. Wenn du nur Threads
kopieren willst, fokussiere die Liste **Threads**, prüfe oder leere den Filter und
drücke **Strg+A**, dann **Strg+C** (auf macOS **⌘A**, dann **⌘C**). Die Auswahl
folgt dem aktiven Tab und dessen Filter. MeowChan beginnt mit dem aktuellen
Stand der Quelle. GChans Downloadfortschritt, bereits geladene IDs und
gespeicherte Posts bleiben in GChan.

## Vorhandene Dateien nutzen

Angenommen, dein alter Ordner `GChan Downloads` enthält `g/123/image.png`
und `g/123/clip.webm`. Du kannst einen dieser Wege wählen:

- **Dateien am Ort behalten.** Wähle **Einstellungen → Speicher und Grenzen →
  Medienordner → Ordner hinzufügen…** und den Ordner `GChan Downloads`.
  Aktiviere im Beispiel **Unterordner einbeziehen**. Prüfe unter **Medien →
  Ordner**, ob beide Dateien erscheinen. MeowChan liest den
  [Medienordner](../Help.md#read-only-media-folders) schreibgeschützt am
  ursprünglichen Ort.
- **Einen benannten Stapel importieren.** Wähle im App-Menü **Dateien
  importieren…** und füge `image.png` und `clip.webm` hinzu. Prüfe das Ergebnis
  der Duplikatprüfung per SHA-256, benenne den Stapel und starte den
  [Import](../Help.md#importing-your-own-files). Neue Dateien werden in
  `meowchan-import/` unter deinem MeowChan-Downloadordner kopiert. Identische,
  bereits vorhandene Dateien werden verknüpft. Wenn Dateien kopiert wurden,
  öffnet **Stapel anzeigen** den Stapel unter **Medien → Eigene Importe**.

Beide Wege machen die Dateien unter **Medien** zugänglich. Für Reader-Posts
und die Zuordnung ihrer Anhänge braucht MeowChan erfasste Beitragsdaten.
Bewahre GChans HTML-Seiten zusammen mit ihren zugehörigen Dateien und
Unterordnern auf, damit relative Links beim Öffnen im Browser weiter
funktionieren. Stelle Downloadpfad, Prüfintervall und parallele Downloads
manuell in MeowChans **Einstellungen** ein.
