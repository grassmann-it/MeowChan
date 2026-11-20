# Validate auf 1.1.1

Grundlage sind die Doku-PR-Läufe [#163](https://github.com/grassmann-it/MeowChan/actions/runs/44436732593) und [#162](https://github.com/grassmann-it/MeowChan/actions/runs/44432779726). Der spätere Push auf den Ausgangscommit 45a45f5bd wurde [abgebrochen](https://github.com/grassmann-it/MeowChan/actions/runs/44436747538). Im Lauf von #163 waren 25 Jobs rot, zehn grün und neun übersprungen. Coverage- und Sammeljobs melden fehlende Ergebnisse der jeweiligen Einzeljobs.

| Fehlersignatur und Beleg | Gezielte Prüfung |
| --- | --- |
| Paket-Fixtures melden auf Linux, Windows und macOS einen Manifestkonflikt. Der Aufrufer übergab die Kandidatenversion implizit an einen bereits gebundenen Standardwert. [Linux lane-a](https://github.com/grassmann-it/MeowChan/actions/runs/44436732593/job/135267348168) | Lokal 57 Prerelease- und sieben Stable-Pakettests grün nach 01db26d79. |
| Reader-Toolbar bei 760 px. Der deaktivierte Sidebar-Schalter wird bei zu wenig Platz bewusst unter More abgelegt. Der Smoke behandelte ihn als Teilnehmer der Prioritätsreihenfolge. [Linux lane-b](https://github.com/grassmann-it/MeowChan/actions/runs/44436732593/job/135267348155) und [macOS native-08](https://github.com/grassmann-it/MeowChan/actions/runs/44436732593/job/135267348353) | Registry-Szene reader-navigation sowie ReaderMediaSidebarLayoutTests und ActionOverflowLayoutTests. |
| Preference-Startup meldet den erwarteten Fehler beim finalen Speichern nach dem erfolgreichen Negativtest erneut. Automatisches Dispose wartet auf dieselbe absichtlich fehlgeschlagene Aufgabe. [Linux lane-a](https://github.com/grassmann-it/MeowChan/actions/runs/44436732593/job/135267348168) und [macOS native-03](https://github.com/grassmann-it/MeowChan/actions/runs/44436732593/job/135267348269) | Normal-Smoke mit MEOWCHAN_PREFERENCE_STARTUP_ONLY=1. |
| Windows Desktop-TRX meldet die frühere Formulierung zur Release-Abnahme als fehlend. Der Upgrade-Test in derselben TRX ist ohne Upgrade-Fixture erwartungsgemäß übersprungen. [Windows lane-d](https://github.com/grassmann-it/MeowChan/actions/runs/44436732593/job/135267348161) | HelpDocumentationTests lokal und offline-retest mit windows-2022 und lane-d. Der separate Upgrade-Job stellt das Fixture. |
| macOS native-05. FIFO-Testkanal überschreitet das Unix-Socket-Pfadlimit. Medien-Shortcut-Tests senden Control statt Command. Decode-Prioritätstest misst per Timer, wann der Poster-Auftrag ansteht. [Job und Artefakte](https://github.com/grassmann-it/MeowChan/actions/runs/44436732593/job/135267348159) | NativePrivateMetadataSpecialFileTests, MediaMultiSelectionCommandTests und ThumbnailDecodeGateTests. macOS-Bestätigung steht aus. |
| macOS native-04 wartet in gif-playback-core auf das aktuell anwählbare Play/Pause-Element. [Job](https://github.com/grassmann-it/MeowChan/actions/runs/44436732593/job/135267348283) | Isolierte Registry-Szene. Native Ursache offen. |
| Windows lane-c meldet nach Timeline-Klicks fehlenden Fokus in focused-pointer-away und custom-routing-loop. Der separate Offline-TRX zeigt einen CRLF-abhängigen Quelltextvergleich in FocusClickContractTests. [Job](https://github.com/grassmann-it/MeowChan/actions/runs/44436732593/job/135267348417) | FocusClickContractTests mit normalisierten Zeilenenden sowie isolierter Media-Input-Smoke auf Windows. |

Der Vorschlag zur PR-Matrix steht auf 1.2.0 in docs/development/release-planning.md.

## Gezielte Wiederholungsläufe

- [Windows Offline lane-d](https://github.com/grassmann-it/MeowChan/actions/runs/44809099203) prüft die korrigierte Hilfe-Assertion im ursprünglichen Test-Shard. Dispatch mit genau einem Windows-Runner auf 52fee4ab6.
- [Windows Media Input](https://github.com/grassmann-it/MeowChan/actions/runs/44809099152) prüft native Timeline-Klicks, nachdem das tatsächliche Timeline-Fenster als aktives Eingabeziel bestätigt wurde. Dispatch auf 52fee4ab6 mit einem ausführenden Windows-Szenario und drei Matrixzellen für den Quellstand.
- [macOS ARM GIF](https://github.com/grassmann-it/MeowChan/actions/runs/44809216079) prüft, ob zwei verschiedene Positionen beim nativen Mauswechsel die Transportleiste zuverlässig sichtbar machen. Der vorherige Smoke bewegte den Cursor bei weiteren Versuchen an dieselbe Position. Dispatch mit einem macOS-Runner auf cf96689de.

Lokal lief eine Auswahl von 236 Desktop-Tests unter SDK 10.0.112 mit deaktivierten Analyzern durch. Die Wiederholung mit dem gepinnten SDK 10.0.111 und aktiven Analyzern läuft separat.
