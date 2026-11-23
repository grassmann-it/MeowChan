# N7 Ordnersitzung in 1.2.0

Branch `n7-folder-session`, Basis `4f29e9cbf`. `MeowChan <Ordner>` öffnet eine Auswahl, bevor MeowChan ein Profil startet. Die Änderungen in `fedd3e8d6` und `3662c0e82` nutzen die vorhandene Ordnerquelle und Profilsperre.

| Aktion | Ergebnis |
| --- | --- |
| Abbrechen im Startdialog | Der Prozess endet ohne Sitzung, Registrierung oder temporäres Profil. Vor dem schreibenden Schritt bleibt der Dialog während der Leseprüfungen abbrechbar. |
| Temporär öffnen | Ein eigenes Profil und sein Downloadpfad liegen im System-Temp-Verzeichnis. Nach der Registrierung der schreibgeschützten Quelle öffnet sich die Medienansicht. Ein normales Profil kann parallel geöffnet bleiben. |
| Verwerfen und schließen | Nach Freigabe der Profilsperre entfernt MeowChan das temporäre Profil. Abbrechen im Bestätigungsdialog hält die Sitzung offen. Ein Fremdordner am ausgetauschten temporären Pfad bleibt erhalten. Bei Prozessabbruch kann das temporäre Profil zurückbleiben. |
| Ins gewählte Profil übernehmen | MeowChan öffnet das ausdrücklich gewählte bestehende Profil und registriert den Ordner dort als schreibgeschützte Quelle. Bei Wiederöffnung bleibt es bei einer Quelle. Ist das Zielprofil bereits geöffnet, erscheint eine Meldung im Startdialog. |

Der Start prüft den Quellpfad samt Vorfahren auf Links und lehnt Überschneidungen mit Profilen oder dem Downloadpfad des gewählten Profils ab. Die Quelldateien bleiben am Ort, und die Registrierung legt im Quellordner keine Dateien an. Die Löschung des temporären Profils prüft die Verzeichnisidentität und folgt enthaltenen Symlinks nicht. Der Ablauf übernimmt die bestehenden Ordnerquellenlimits aus den Einstellungen.

Die Einstiegspunkte liegen in [Program.cs](../../../Program.cs), [App.axaml.cs](../../../App.axaml.cs), [StartupMediaFolder.cs](../../../Desktop/StartupMediaFolder.cs) und [StartupFolderWindow.cs](../../../Desktop/StartupFolderWindow.cs). Die Shell verwendet den vorhandenen Registrierungspfad und den bestehenden Shutdown mit Bestätigungsdialog. [SettingsTransfer.cs](../../../Desktop/Settings/SettingsTransfer.cs), `libmeowchan/Transfer/`, `libmeowchan/Interop/` und `packaging/` bleiben für die nachfolgenden Entscheidungen offen.

## Offene Entscheidungen für Tom

1. **Lokales Profil und Behalten.** Soll `<Ordner>/.meowchan/` Metadaten und einen Konfigurationsauszug oder ein vollständiges Profil enthalten? Wann soll MeowChan beim Wiederöffnen und nach einem Laufwerks- oder Pfadwechsel erneut nach der Zustimmung fragen? Wie soll Behalten auf ein bereits vorhandenes lokales Profil reagieren?
2. **Schreibgeschützt und parallel.** Wie soll die lokale Wahl bei schreibgeschützten Quellen und bei einem laufenden lokalen Profil aussehen? Soll ein fehlgeschlagenes Behalten die temporäre Sitzung offen lassen oder einen anderen expliziten Zielpfad anbieten? Wie lange sollen nach einem Prozessabbruch verbliebene Sitzungen wiederaufnehmbar sein?
3. **Übernahme beim Schließen.** Welche privaten Metadaten gehören in den ersten Transfer? Soll die Zuordnung über Hash und exakte Dateiversion mit relativem Pfad als Herkunft laufen? Wie werden bestehende Zielmetadaten, veränderte Dateien und ein bereits geöffnetes Zielprofil behandelt? Der implementierte Startweg übernimmt die Quelle ins Zielprofil. Für Sitzungsmetadaten steht derzeit nur Verwerfen zur Verfügung.
4. **P9 und Dateimanager.** Hydrus ist als Partner bestimmt. Der dokumentierte Zielvertrag sieht explizit ausgewählte Medien-/Sidecar-Paare und einen frischen Zielordner vor. Soll die Kollisionsvorschau betroffene Paare überspringen, den Export abbrechen oder einen neuen Paar-Namen zur Bestätigung anbieten? Die Linux-, Finder- und Explorer-Einstiege brauchen jeweils ihre eigene Paket- und Plattformabnahme.
5. **Erweiterte Startauswahl.** Gehören „Neues Profil“ und „Nicht mehr fragen“ zur ersten Abnahme? Der aktuelle Dialog öffnet bekannte oder per Ordnerauswahl gewählte bestehende Profile.

## Prüfung

- [StartupMediaFolderSessionTests](../../../tests/MeowChan.DesktopTests/StartupMediaFolderSessionTests.cs) decken Positionsargument, fehlende und verlinkte Pfade, überlappende Downloadpfade, schreibgeschützte Quelle, parallele Profilsperren, Verwerfen mit einem enthaltenen Symlink und Wiederöffnung derselben Quelle ab.
- Der abschließende Desktop-Lauf auf `3662c0e82` ergab unter `xvfb-run` 738 bestandene Tests ohne Skips für Ordnersitzung, Profil, Settings-Transfer, Lokalisierung und DesignLint. Vor den letzten Anpassungen an der Abbruchprüfung ergab der breitere Desktop-Lauf einschließlich Config und NativeTransfer 924 bestandene Tests ohne Skips.
- Der Linux-Nativlauf startete das gebaute Programm unter `xvfb-run` mit Openbox. Escape im Startdialog hinterließ weder neues Profil noch Profilsperre. Während ein reguläres Profil geöffnet war, öffnete der temporäre Weg eine schreibgeschützte Quelle, ließ das Verwerfen abbrechen und entfernte nach Bestätigung nur sein eigenes Profil. Die direkte Übernahme verweigerte das gesperrte Profil. Nach dessen Freigabe blieb die Quelle auch nach erneutem Start genau einmal registriert. Verzeichniseinträge und Originalbytes waren identisch.
- Beim Schließen direkt nach Anzeige des Hauptfensters kann während der Profilinitialisierung die Meldung `Final preferences unavailable` erscheinen. Das ließ sich auch über den regulären `--profile`-Start auslösen. Der Nativlauf zur Wiederöffnung wartete auf die Initialisierung.
- Der Kernlauf für Transfer, FolderSource, Dateinamenvorlagen und den vorhandenen Sidecar-Codec ergab 6.634 bestanden, 9 mit bestehenden Umgebungs- oder Fixture-Bedingungen übersprungen, 0 fehlgeschlagen. Der Codec-Lauf deckt bestehende In-Memory-Tests ab. Der P9-Dateipaarlauf steht aus.
- Aus der gebauten Desktop-Assembly ergeben sich 5.448 eindeutige EN-Schlüssel. Alle sieben neuen Schlüssel sind in DE und allen zehn Packs vorhanden. Die Inventare stiegen je Pack um sieben. Zahlen und SHA256-Digests der exakten EN-Gleichstände entsprechen dem vorherigen Stand. [Katalogaudit](../../../tests/MeowChan.DesktopTests/IdenticalLocalizationStringsAuditTests.cs)
- Der Desktop-Build lief mit aktivierten Analyzern. Die Baseline enthält weiterhin 828 Einträge. Keiner ist an eine geänderte Quelldatei gebunden, daher war kein neuer SHA256 über LF-Quelltext nötig. Beide `PublicAPI.Shipped.txt` entsprechen der Basis bytegenau. Verwendet wurde das lokal installierte .NET SDK 10.0.112 mit vorhandener Worktree-Änderung an `global.json` außerhalb der Commits.
- Native Windows-, macOS- und Dateimanager-Tests stehen aus.
