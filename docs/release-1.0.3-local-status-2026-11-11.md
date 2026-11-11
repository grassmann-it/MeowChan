# MeowChan 1.0.3 — lokaler Abnahmestand (11.11.2026)

Stand: Branch `goal/gesamtprogramm-profile-a`, Head `7e7b729` (Quellstand der
unten genannten Läufe; der Core-Lauf lief auf `4d21da9`, danach änderten sich nur
zwei Testdateien). Nichts ist veröffentlicht, gepusht oder getaggt.

**Ergebnis: kein freigebbarer 1.0.3-Kandidat.** Die lokal prüfbaren Pakete sind
belegt; der Kandidat bleibt nach `START-GOAL-1.0.3.md` blockiert, solange die
Windows-, macOS- und Hardwarebelege fehlen. Sie sind unter Linux nicht ersetzbar.

## Belegt (Linux, Logs unter `/tmp/mc-evidence`, außerhalb des Repositories)

| Bereich | Beleg |
| --- | --- |
| Core-Tests, frischer `--no-incremental`-Build | 9375 bestanden, 0 fehlgeschlagen, 5 übersprungen (`final-*/test-MeowChan.Tests.log`) |
| Desktop-Tests, frischer `--no-incremental`-Build auf `7e7b729` | 3065 bestanden, 0 fehlgeschlagen, 11 übersprungen (`final2-*/test.log`); der erste Lauf auf `4d21da9` fand zwei veraltete Test-Pins, die in `7e7b729` korrigiert wurden |
| Beispiel-/Experimental-Plugins | 360 bestanden, 0 fehlgeschlagen |
| Transfer/Merge/Recovery/Reexport/Private-Apply (Core-Filter `Transfer`) | 6238 bestanden |
| GUI-Smokes im privaten Xvfb (nice 19) | Archivfilter 12/12 Szenarien, Saved-Search-Konflikt, Settings (X1), Feedback (X2), Reader-Find (SF-06), Startup-Preview, Favoriten, Promote-Dialog, HTML-Export (3), FirstRun (2), Galerie versteckter Favorit, Mute, Volume, Playback-Features, Preview-Sprachen, Diagnose (2), Export-/Settings-Sprachwechsel |
| Promote | Core (Export, Re-Merge, Budget) und Dialog-Smoke mit benannter Bestätigung |
| Zeitzonen-„assembly not found“ | nicht reproduzierbar; gezielte Diagnose und Regressionstest in `b14f4b1` |

## Bewusst offen oder begrenzt

- **Windows/macOS/Hardware:** keine Belege; blockiert den Kandidaten.
- **Paketprüfungen** (`check-desktop-package.py`, Linux-Distribution): nicht ausgeführt.
- **Discard:** speichert nur die Entscheidung im Ledger. Die Bytes der verworfenen
  Alternative bleiben bis zu einem späteren Cleanup erhalten; es gibt keine
  automatische Freigabe.
- **Guro.cx:** dokumentierter technischer Block (kein registrierbarer Source);
  **Anon Imageboards:** kein öffentlicher Quellvertrag, nur Plan und Fixtures zur
  Nichtverfügbarkeit. Beide gemäß Releaseplan als begründeter Nichtverfügbarkeitsfall.
- **Experimental-Quellen:** standardmäßig deaktiviert und gebündelt; Livebelege fehlen
  (Live-Tests übersprungen), daher „nicht aktuell verifiziert“.
- **UX103:** SF-03 und SF-05 bleiben Nutzerentscheidungen; UX103-E
  (Startkostenmessung) nicht begonnen.
- **Ledger-Lücke:** Alternativen am selben Slot mit anderem Provider-Tupel sind nicht
  gesondert behandelt.
- Crash-/Platzmangel-Recovery ist durch die vorhandenen Core-Tests abgedeckt; ein
  echter Platzmangel- oder Absturz-Lauf auf Dateisystemebene wurde nicht ausgeführt.
