# Fortsetzung: Kandidatenfehler konkret eingegrenzt — 30.10.2026, abends

**Reguläres 1.0.2 bleibt beauftragt, noch NICHT veröffentlicht.** Die folgenden
älteren Abschnitte sind historische Momentaufnahmen, keine aktuelle Sperre der
inzwischen ausdrücklich genehmigten Legacy-Neuauswahl.

Backend/UI der bestätigten NEUauswahl sind geliefert; echte 1.0.0/1.0.1-Profile
haben je zwei Folgestarts bestanden. Pins/Rechte/Herkunft werden nicht erfunden.
Version 1.0.2, APIreview und 22 lokale Lockgraphs sind eingefroren; externe
Abhängigkeiten unverändert. Kleiner `/gif/`-/„Goon Central“-Galerieworkflow-USP
in den Release Notes ergänzt, keine neue Quelle und kein zusätzlicher Scope.

- Erster Kandidat `76485fa`, Run41105942320: FAIL. Native Windowsaudioverfügbarkeit,
  fehlende Prioritymenüicons, Setupreihenfolge der Apphostfixtures und zu kleines
  Windows-Gesamttestbudget konkret korrigiert; Produktionsprofilhärtung erhalten.
- Zweiter Kandidat `4a00873`, Run41116310065: **kein Gesamterfolg**. Windowspaket
  und beide macOSpakete PASS. Native Validierung trifft veraltete Erwartungen an
  technische Galerieinlinefehler, die laut Nutzerentscheidung zentral erscheinen
  sollen; Windows zusätzlich Queue-Testdeadline und gepoolter Test-SQLitehandle.
  Keine Veröffentlichung aus diesem Lauf und kein Mischen seiner Pakete mit neuen.
- Neu: LibVLCstatistiken explizit aktiviert, damit externe VLC-Konfiguration die
  nachgewiesene Audioverfügbarkeitsprüfung nicht abschaltet. Decodierte Tracks
  ohne gerenderte Audiobuffer aktivieren keine funktionslosen Lautstärkeregler.
- Galerie-Workload/Viewer/Fokusprüfungen an zentrale Diagnosen angepasst: exakte
  Meldungsidentität, keine Inlinefehlermeldung, Fokus/Selektion unverändert;
  echte Unavailable-/Pixel-/Decoder-/Transportprüfungen bleiben verbindlich.
- Disposable SQLitetestverbindung ohne Pool: zwölf TitleRuntimefälle lokal PASS;
  keine pauschale Löschwiederholung oder Lockerung des Produktarchivs.

Lokaler Vorlauf mit `4a00873` plus damaligem uncommitted `--stats`: fünf echte
Video-/Audiofixtures, Galeriefehlerinteraktion, normaler UI-Modus, stummer H264
bei 2× und vierphasiger Apphostworkflow PASS. Eigene stumme Audio-/DBus-/Xvfb-
Session beendet. **WIP-Belege, kein exakter finaler Quell-/Paketpass**; normaler
UI-Modus allein ist insbesondere NICHT der separate download-session-Modus.
Belege: `artifacts/release-1.0.2-ci-fix-verification/pre-final-summary.json`.

Nächste Gates: ergänzende Downloadsession/Reader-/Galerieprüfungen, Queuefix,
exakter neuer vollständiger stable-candidate/all/all-Lauf; erst bei dessen Grün
18 Assets desselben Laufs prüfen und reguläres v1.0.2 Latest veröffentlichen.
Manueller Windows-Praxistest folgt danach. Geschützte fremde `.gitignore` und
`docs/releases/v1.0.1-preparation.md` bleiben uncommitted und bytegleich.

---

# Neue Nutzerfreigabe: Legacy-Neuauswahl umsetzen, Version1.0.2 — 30.10.2026

Nutzer hat die zuvor angebotene sichere **Neuauswahl** aktueller gebündelter
Plugins ausdrücklich angenommen („hätte gemacht wie du sagst“). Kein zusätzlicher
Herkunftsnachweis vom Nutzer erforderlich: Vorschau+explizite Bestätigung,
unveränderliches Originalbackup und tatsächlicher v2-Zustand vor Neuauswahl,
exakte schema-gültige bestätigte Einstellungen; keine automatische Altzuordnung,
keine Pin-/Grant-/Ownerübernahme. Konflikt-/Rechte-/Pin-Fälle weiter blockieren.
Backend, passende sichtbare UI und echte Altprofilprüfungen sind in Umsetzung.

Nutzer bestätigt **1.0.2**, Folgefixes ggf**1.0.3**. Der manuelle Windows-Praxistest
soll **nach** regulärer Veröffentlichung erfolgen; das revidiert die bisherige
vorgeschaltete manuelle Testfahrt aus C102-08, nicht notwendige technische
Build-/nativePaket-/Upgrade-/Integritätsprüfungen. Veröffentlichung ist beauftragt,
kein erneutes pauschales Freigabegespräch nach grünen technischen Gates nötig.
NeueQuellen bleiben aus. Vorheriger Code-/Prüfstand unten ist historische Basis,
nicht Abnahme der jetzt laufenden Neuauswahländerung.

# Aktueller Stand: gemeinsame Integration grün, Release noch gegatet — 30.10.2026

**Geprüfte Produktquelle: `400bd6174089d2d6018ab9ff8b6820d87c00d354` auf
`release/1.0.2`. Kein Push, Tag, neuer Windowskandidat oder Release.**
Nachfolgende reine Statusdokumentation ändert diese Produktquelle nicht.

## Implementiert und gemeinsam geprüft

- Wiederaufnahme nach bewiesener initialer No-send-Admission. Reale HTTP-/Body-/
  Transportfehler bleiben dagegen sichtbar, normale Poll-/Mindestabstände erhalten.
  Host-eigene korrelierte Operationen tragen die Historie über IPC; wertgleiche
  fremde oder bereits beendete Kontexte können sie nicht neu aufnehmen.
- Medien-Headerbudget beginnt nicht in gesunder initialer Admission. Effektive
  Transfers auf32 begrenzt; gespeicherte positive Werte bis1000/int.MaxValue
  unverändert. Defaults1MiB/s/5 und Pending256 bleiben bestehen. Kein behaupteter
  Internetdurchsatzgewinn und keine unbelegte Queuevergrößerung.
- Mehrere High-Watches teilen faire nächste Dateiplätze; Normal erhält bei
  Konkurrenz spätestens nach drei High-Reservierungen einen Platz. Keine
  Präemption, kein Resume durch Priorität, kein Vererben an Ersatzwatches.
  Autoritative Badges und konfigurierte/effektive Parallelität in der Oberfläche.
- Verifizierter Callback-MP4-Stream nutzt avformat; Linuxbundles verlangen diesen
  Demuxer. Reader-eigene Paginationadorners überdecken Galerieklicks nicht mehr.
- Originales gültiges v1-Pluginstate vor erster Mutation einschließlich Startup
  unveränderlich samt Hash gesichert. Konflikte blockieren Veröffentlichung;
  bestehendes v2 wird nicht als erfundene v1-Vorgeschichte ausgegeben.
- Sechs vorhandene Setup-/Preference-/Diagnose-/GIF-/Galeriefehler-/Reader-
  Eingabepfade in Kandidaten-CI aufgenommen; Jobbudget auf60min erweitert.

## Gebundene Prüfergebnisse

- Solution Releasebuild: **0Fehler/19Warnungen**, normale Analyzer.
- Alle13.NET-Testprojekte: **3402PASS/0FAIL/8SKIP**,3410Fälle. Opt-in-SKIPs
  sind keine Upgrade-/Live-/Nativeabnahme.
- Python offline/Packaging-/Runnerverträge in separatem Gitclone:
  **350PASS/0FAIL/1SKIP**,351Fälle. Root-/Fixture-Head,Index,Config,Refs und
  Quellhashguards unverändert; keine GIT_*-Overrides an Fixtures vererbt.
- Native Reader→Galerie→Reader1×/2× auf derselben Quelle PASS.
- Echter Source-Apphost: fünf VP8/VP9/H264-/Audio-/Silentfixtures PASS,
  stummerH264 zusätzlich mit gemessener2×Skalierung PASS. Private Xvfb-/DBus-
  Session und ausschließlichstummer privaterNullsink; eigene Audiodienste beendet.
- Unabhängiger statischer Review: ursprünglicher Core-P1 korrigiert; zwei P2
  (geborgterUserAgent, Hostfehlerhistorie) korrigiert, Lebenszyklusrereview ohne
  neue konkrete P1/P2. Zwischenfehler bleiben als solche in Rohbelegen erhalten.

Belege: `artifacts/release-1.0.2-close-2026-10-30/integration-03-400bd61/SUMMARY.json`,
13TRXdateien, native Ergebnisse und separatePythonreceipt. Alle beim Run erfassten
Quellen waren bis zum Abschluss unverändert. Das ist integrierte lokale QA,
**keine Prüfung eines ausgelieferten Pakets auf Windows/macOS**.

## Vor Release noch erforderlich

1. **DECISION_REQUIRED: Legacy-Neuauswahl.** Historische1.0.0/1.0.1-Profile ohne
   Herkunftsnachweis können weiterhin nicht als bewiesene alte Bundles repariert
   werden. Explizite Vorschau+Bestätigung einer NEUauswahl aktueller gebündelter
   Plugins ist noch nicht freigegeben oder implementiert. Keine automatische
   Namens-/Pfadzuordnung und keine Pin-/Rechte-/Ownerübernahme. Nach Entscheidung
   enger Codepfad und echte Altprofile mit zwei Neustarts abnehmen. Backup allein
   löst die15unverfügbaren Kandidaten nicht.
2. Danach1.0.2Version-/PublicAPI-/22Lockfilefreeze. Derzeit weiterVersion1.0.1;
   zweiQueue-APIs stehen bewusst noch inUnshipped, Release-APIgate nichtgrün.
3. Aktueller vollständiger nativer/paketierter Plattformkandidat, Upgrade-/Live-
   Gates und Windows/Linux-Testfahrt; bisherigerd7d1e06e-Testbuild ist kein Ersatz.
4. Erst danach reguläresv1.0.2 und anonym verifizierte Assets/Prüfsummen.

Neue Quellen bleiben ausgeschlossen. Alle neuen Solworker sind beendet. Fremde
`.gitignore` und `docs/releases/v1.0.1-preparation.md` bytegleich und uncommitted.

## Commits dieser Fortsetzung

`43091d9` Media/Reader · `322af66` CI · `f24aeea` HTTP/Parallelität/Priorität ·
`23b7ca4` Originalbackup · `bc13ec1` Startup/Fehlerhistorie · `400bd61` Hilfetexte.

---

# Fortsetzung / Integration-02 — 30.10.2026

Branch `release/1.0.2`, zusätzlicher Commit `43091d9`: verifizierter MP4-Stream
nutzt bei ISO-BMFF avformat; Reader-eigene Paginationadorners fangen keine
Galerieklicks mehr ab. Linuxavformat-Allowlist erweitert. Unabhängiger statischer
Review ohne konkrete P1/P2-Befunde, kein nativer Gesamt-/Releasepass.

Aktueller integrierter Solutionbuild: **0 Fehler / 15 Warnungen**, normale
Analyzer. **11 gezielte Desktoptests PASS** einschließlich zwei neuer Hosttests:
Mehrfachpriorität bei Pause ohne Netzwerk, entfernte/neuangelegte Watchidentität,
Nicht-Ready-Ablehnung und unverändert gespeicherte Parallelität1000 bei effektiv32.
Worker: Queue-/Prioritysuite50PASS, Konfigurationssuite114PASS; noch getrennte
Forkbelege. Noch NICHT als eingefrorene Gesamtquelle oder Kandidat abgenommen.

A2-Coreprüfung fand realen Unterschied zwischen nie gesendeter Admission und
nach echtem503/Timeout/Redirect deferiertem Check unzureichend abgebildet.
Enger Fix läuft in isoliertem Solfork, SourceAdapterregressionen dort aktuell
noch nicht vollständig grün. Kein Fehler wird als fertige Closure verschwiegen.
Altprofilbackup vor erster Startupmutation geliefert, Integrationprüfung folgt.
Legacy-Neuauswahl bleibt ausdrückliche offene Policyentscheidung, keine
Herkunft erfunden oder Pins/Rechte übernommen. Version weiterhin1.0.1;
keine Veröffentlichung. Neue Quellen weiter ausgeschlossen.

Belege: `artifacts/release-1.0.2-close-2026-10-30/integration-02/` und
`integration-review-media-reader-priority.md` im Runverzeichnis.

# Aktiver Abschlusslauf auf release/1.0.2 — 30.10.2026

Nutzer hat Umsetzung mit mehrerenWorkern und Zwischencommits ausdrücklich
beauftragt. Branchrelease/1.0.2 erstellt, vollständiger vorhandenerProduktWIP als
Checkpointf3ed52f gesichert; Main58915e9 bleibt unangetastet. Original.gitignore/
v1.0.1-Vorbereitung nichtcommitted, bytegleich. IsolierteWorkerworktrees von genau
diesemCheckpoint; keinHEAD-only-Verlust und keineShared-root-Produktwrites.

Ausgangs-Solutionbuild0Fehler/18Warnungen. Vollständiger .NET-Ausgangslauf13Assemblies:
3322PASS/0FAIL/8SKIP=3330;13TRXbelege, Repohead/Branch/Stage/core.worktreeguardintakt.
Das istBaseline, keineGUI-/Upgrade-/Releasefreigabe. HistorischeReader/H264fehler
bleiben bis aktueller nativerClosure offen. Runtimeartefakte unter
artifacts/release-1.0.2-close-2026-10-30/.

A1Upgrade hat bestätigt: fehlendeAltherkunft kann mit bisherigemG-MIGRATION nicht
alsbewiesenrepariertwerden. R0fragte nach A2Review engbegrenzt ausdrücklicheNeue-
auswahl aktueller geprüftergebündelterTargets statt erfundenerLegacyattribution;
Originalbackupvorersterv2Write, keinePin-/Rechte-/Ownerübernahme. Entscheidung
ausstehend. NurdieseErweiterungwartet; unabhängigeBackup-/Core-/Media-/QAarbeiten
laufenweiter. NochkeineMigration/Pins/Grantsgelockert, keineVeröffentlichung.

# Abschlussstatus 1.0.2 — 30.10.2026

**Planung erstellt; keine neuen Builds/Tests/Code-/Remote-/Releaseaktionen.**

| Bereich | Tatsächlicher Befund bei Planerstellung | Abschluss |
|---|---|---|
| Bestehende Media/Reader/Setup/Retryfixes | viel Code geliefert/integriert, zahlreiche Teilbelege | aktuelle gemeinsame QA nötig |
| GIF | Source13gesamtegebundeneGIF-/Reader-/Ownershipsuite1×/2×PASS | aufKandidatenregressionsprüfen, nicht neuimplementieren |
| Source14Units |661Desktop+98Configuration+2StartupPASS, selektiveGruppen | keinGesamtsolution-/ReleasePASS |
| Reader-Rückweg | Source14allgemeinerNativeReader→GalleryClose→ReaderFAIL | C102-05/06offen |
| H264stumm | Source05ApphostFAIL laut aktuellemBericht offen | C102-05/07offen |
| BundledRepair | neuerProvenienz-/Slot-/Previewcode vorhanden | historischeLegacy15jeAltversionweiterunavailable |
| Altprofil-/Formatübergang | tatsächlicherWindowsmissinginventory-Befund; PluginEnvelopev2oldreadersnichtlesbar | benutzbarer erlaubterLegacyweg+Backup/Restoregatesoffen |
| Auto-Continue/Gruppe | NutzermeldungLinux; passenderDefer→Error→60sPfad statischbelegt | Ursache/Regression/Fixoffen |
|25Thread-/1000Parallel-Timeout | Nutzerwerte, konkreteäußereDeadline-/Admissionhypothese | keinegemesseneRootcause/behobenePerformance |
| MehrfachHighprio/Queuegröße | Wunsch/technischeV1empfehlung | keineimplementiertePrio;größererPuffererstA/B |
| Produkt/Kandidat |1.0.1imCode,Main58915e9+WIP |1.0.2Version-/Lock-/Nativefreezeoffen |
| WindowsTestd7d1e06e | Build40932990374Publish/PaketuploadPASS,keinRuntimegate | kein1.0.2Kandidat,nightlyAnon404beobachtet |
| Veröffentlichung | reguläres1.0.2istZiel | nichtgestartet,nichtreleasebereit |

Statuscodes für Ausführung: PLANNED/READY/IN_PROGRESS/REVIEW/PASS/FAIL/
DECISION_REQUIRED/UNRUN. PASS nur bei vollständigemOutcome undgebundenerPflichtQA.
Aktuellen laufendenRun nicht durch dieseMomentaufnahme überschreiben; neuere
Handoffs/Belege zuerst gegenCodehashesabgleichen. Publisher/Versiongates nicht
auf bisherigenPlanungsstatus zurückdatieren.
