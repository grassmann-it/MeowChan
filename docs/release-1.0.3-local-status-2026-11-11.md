# MeowChan 1.0.3 — lokaler Abnahmestand (11.11.2026)

Stand: Branch `goal/gesamtprogramm-profile-a`. Finale Vollläufe auf einem Head
`4bc4d01` (frischer `--no-restore --no-incremental`-Build, `dotnet test --no-build`,
`/tmp/mc-evidence/final3-latest`): Core 9399 bestanden/0 fehlgeschlagen/5 übersprungen,
Desktop 3079/0/11, Beispiel-/Experimental-Plugins 360/0. Danach nur `9948b8f`
(Startup-Probes in zwei Spuren, Desktop-Filter `Plugin` 155 grün) und `ef116e2`
(Opt-in-Platzmangeltest, ohne Variable übersprungen). Nichts ist veröffentlicht,
gepusht oder getaggt.

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
- **Discard / Platzfreigabe:** Discard speichert weiter nur die Entscheidung. Auf
  Nutzerentscheidung (11.11.) gibt es zusätzlich den ausdrücklichen Cleanup „Platz
  verworfener Alternativen freigeben“ im Konfliktdialog: erster Druck misst und nennt
  die Größe, zweiter Druck löscht, Escape bricht ab. Freigegeben wird nur der ganze
  Full-Root einer committed Merge-Operation, deren Konflikte alle entschieden sind
  (mindestens einer verworfen, keiner offen/behalten), der in keinem Thread registriert
  und nicht mit dem Safe-Root identisch ist. Ablauf: atomares Umbenennen nach
  `.reclaim-<guid>`, dann Löschen; Reste werden beim nächsten Lauf aufgeräumt.
  Ledger, Operationszeilen, aktive Wurzeln, Promote-Wurzeln bleiben unberührt; ein
  fehlender Root gilt als „freigegeben“. Belege: Core `NativeV2MergeConflictInventoryTests`
  (14 grün, inkl. Offen/Behalten gepinnt, Vorschau löscht nichts, Crash-Rest,
  Wiederholung, Pfad-/Namensschutz), Desktop-Katalog-/Lokalisierungstests (487 grün),
  Smoke `MEOWCHAN_NATIVE_TRANSFER_PREVIEW_ONLY` (Zweischritt, Escape). Nicht belegt:
  echter Platzmangel/Absturz auf Dateisystemebene, Windows-Umbenennen mit offenen Handles.
- **Guro.cx:** dokumentierter technischer Block (kein registrierbarer Source);
  **Anon Imageboards:** kein öffentlicher Quellvertrag, nur Plan und Fixtures zur
  Nichtverfügbarkeit. Beide gemäß Releaseplan als begründeter Nichtverfügbarkeitsfall.
- **Experimental-Quellen:** standardmäßig deaktiviert und gebündelt; Livebelege fehlen
  (Live-Tests übersprungen), daher „nicht aktuell verifiziert“.
- **UX103:** SF-03 (Enter auf Find-Tasten) und SF-05 (Primary+F nur in der großen
  Leseansicht, Befehl `reader.find`, umbelegbar) umgesetzt in `db28407`.
- **UX103-E Startkosten (11.11.):** Messung unter `/tmp/mc-evidence/startup-measure-2026-11-11`.
  Befund: Die Pluginphase startete bei jedem Start seriell einen Describe-Worker je
  gebündeltem Kandidaten (23). `15e469d` verwendet die zuletzt validierte Beschreibung
  wieder, wenn Kandidat und Ausführungsidentität (Revision, Assembly-/SDK-/Compiler-
  Fingerprints, logische Ressourcen) unverändert sind; Aktivierung, Grants, Owner und
  Provenienz laufen unverändert. A/B direkt hintereinander, warmes Profil, Last 9–16:
  Wiederöffnen Pluginphase 6,65 → 1,28 s (0 Threads) bzw. 11,5 → 1,49 s (25 Threads),
  Add-Freigabe 8,7 → 3,3 s bzw. 15,6 → 4,6 s; Erststart unverändert (~6,8 s Pluginphase).
  Tests: Desktop-Filter `Plugin` 155 grün, 1 übersprungen. Erststart: `9948b8f` probt
  in zwei Spuren, Pluginphase in 3 abwechselnden Paaren 14,3/9,4/10,3 → 6,5/6,9/5,2 s
  (Last 16–28). Nebenbefund: Das Mess-Harness
  löschte das Profil nach jedem Lauf, frühere „reopen“-Werte (auch 10.11.) waren
  Kaltstarts; nur in der Messkopie korrigiert. Erste Messrunde (Last 20–38) nicht als
  Vergleich verwendbar. Nicht umgesetzt: der `MainWindow`-Konstruktor (unter Last 6–8 s, sonst ~2 s).
- **Ledger-Identität (11.11., Nutzerentscheidung „zusammenführen“):** neue Konflikte
  tragen einen Slot-Digest `occurrence-v2` (Ressource, Post, Position) ohne Provider-
  Tupel; gleiche Alternativen unter verschiedenen Tupeln sind eine Zeile. Bestehende
  V1-Zeilen behalten ihre Identität und werden nicht migriert; ein V1-Eintrag (auch
  entschieden) unterdrückt das erneute Anlegen als V2 (`LegacyIdentitySha256`), Promote
  erkennt beide Digests. Kein Schema-Wechsel. Nicht zusammengeführt werden bereits
  doppelt vorhandene V1-Zeilen. Beleg: Core-Filter `Transfer` 6242 grün, 2 neue Tests.
- **Platzmangel, echt (11.11., tmpfs 64 MiB, `ef116e2`, 3×4/4 grün):** bei 0/4 KiB frei
  schlägt der Import vor dem dauerhaften Claim fehl, derselbe Plan committet nach
  Freigabe; bei 256 KiB committet er mit veröffentlichten Bytes. **Bei 64 KiB bleibt die
  Operation `ReconciliationRequired` und die Startprüfung verweigert das Profil dauerhaft;
  im Produkt gibt es keinen Ausweg.** Nichts wird teilweise veröffentlicht. Das ist ein
  offener Recovery-Blocker im Sinne von Releaseplan §2.1 („kein Downgrade“). Entwurf für
  ein bestätigtes Verwerfen: `docs/architecture/gesamtprogramm-2026-10-31/NATIVE-TRANSFER-INTERRUPTED-RESOLUTION.md`
  (lokal, git-ignoriert), wartet auf Freigabe. Echter Absturz (SIGKILL) nicht ausgeführt.
