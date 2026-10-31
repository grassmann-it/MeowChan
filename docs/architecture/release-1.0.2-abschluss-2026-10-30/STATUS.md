# Aktuell: macOS-Einstiegsframes über Exit bewahren, native Gegenläufe offen — 31.10.2026

Run41188344991/6251d86: ARM64validate124499765766 scheitert an unverändertem
`ShellPresentationChecks`-Schnellwechseltest, nicht am Macpaketbau. Trace zeigt
1380×850 während FullScreen-Einstieg, dann Normal1000×700, explizit1011×709 und
spätes natives User1380×850, das irrtümlich neuer Sollwert wird;15sTimeout.
Entryframes wurden nicht erfasst und beim Leave wurde ihr Verlauf gelöscht.

Enger macOS-Fix: tatsächliche e.ClientSize bei explizitem FullScreen-Einstieg vor
Earlyreturns merken, vorhandenen deduplizierten4erFIFO bis zum Exit behalten.
Frischer Einstieg/Dispose löschen weiter; bestehende Generation-/Zielguards und
unbekannte normale Usergrößen bleiben erhalten. Kein Timeoutplus/Skip oder neue
Zeitheuristik. Solreview ohne Blockingbefund; Beobachtungsbuffer3neueUnits.

Lokal21gezielteFullscreen-/Startupgeometrie-TestsPASS, kompletterSmoke-Build0Fehler/
5bestehendeWarnungen. Originaler nativer Schnellwechsel bleibt unverändert.
Zusätzlicher Mac-only-Replay ruft den nativen Resize-Callback synthetisch auf und
prüft tatsächliche Handler-/Leaveintegration, bekannterFrame→neuesZielschutz und
unbekannteUsergröße→queuedKorrekturüberschreiben. Queuevorbedingung explizit geprüft;
KEIN OS-Input-/nativer MacPASS daraus. Physische unbekannteUserfenstergeometrie wird
durch diesen Replay nicht behauptet, nur Intent/managedWidthHeight; native Gates
bleiben erforderlich. Gezielte ARM64-/Intel-selection-CI vor neuem Gesamtkandidaten.

Grenzen: vierPlätze sind keine Zeitexpiry; echtes Userresize auf exakt bekannte
Framegröße ist innerhalb bestehender Feedbackpolicy mehrdeutig. Keine allgemeine
Cocoa-/unbegrenzteAnimationsabnahme. Belege `ci-eighth-failures` im bisherigen
Verifikationsverzeichnis; RUNSTATE enthält nächsteRunIDs. Kein Tag/Release; neue
Quellen/Defaults/Abhängigkeiten unverändert, beide Benutzerdateien geschützt.

---

# Aktuell: Windows-Layoutabsturz eingegrenzt, terminale Overlayfreigabe korrigiert — 31.10.2026

**Run41137675108/7d20825 ist FAIL; weiterhin kein Tag/Release.** Einziger roter
abgelaufener Job: Windowsvalidate124344042052, Galeriefehlerprüfung, tatsächlicher
Prozessabsturz `InvalidateArrange on wrong LayoutManager` nach drittem Noticefall
(Overlay/Playing). Kein Timeout und kein Beleg, dass Audiowarnungen ihn verursachen.
Andere ausgeführte Jobs einschließlich Windowspaket und beiden Linux-/Macpaaren
sind grün; abhängige Paket-/Inventur-/Libraryjobs wurden übersprungen.

Solreview + Rootprüfung: endgültiges Stoppen verschob noch Transport/Chrome/Help
von LibVLCs nativem Overlay ins angehängte Hauptfenster. Terminale Freigabe sperrt
nun solche Platzierung dauerhaft, wartet echten nativen Cleanup ab, trennt den
Transport ab und löst den Overlayteilbaum ohne Rücktransfer. Cleanupfehler bleiben
sichtbar/ownershiphaltend. Live-Umschalten/Suspend/Resume bleiben eigene Pfade;
keine generelle Avaloniafehlerbehebung oder exakte Täterkontrollidentität behauptet.

Lokale gezielte Prüfung (Source-WIP, NICHT nativer Windows-/Releasepaketnachweis):
- 88.NET-Fälle PASS/0FAIL/0SKIP, inklusive6terminaler Admissionregressionen.
- Originalprodukt7d im separaten Clone + isolierter Zwei-Fenster-Test ohne Medien:
  erwartetes FAIL wegen tatsächlichem Host-Rücktransfer beim Dispose.
- Gleicher gezielter Terminaltest mit Fix PASS; wiederholtes Dispose bleibt
  parentlos. Reale Galeriefixtures prüfen zusätzlich fehlenden Rücktransfer.
- Private Ubuntu24.04 mit echtem stummem Nullsink,0Hardwarekarten: komplette
  Galeriefehlerprüfung, Galeriefeatureprüfung und alle5nativen OS-Eingabestufen
  PASS. Funktionales Audio in positiver Feature-/Eingabeprüfung explizit verlangt;
  kein No-output-Ersatzpass. Child/DBus/Pulse exit0, Cleanup vollständig.
- Frühe breitere Zwei-Root-Hilfsproben (inkl ungesetzter Liveplatzierung) schlugen
  auf altem UND korrigiertem Code fehl; unverändert als FAIL erhalten, kein
  allgemeiner Live-Reparenting-PASS daraus. Präziser Terminaltest isoliert seinen
  Übergang; echte native Windows-Umschalt-/Shutdownprüfung bleibt CIpflichtig.

Belege: `artifacts/release-1.0.2-ci-fix-verification/ci-seventh-failures/`, besonders
`baseline-gui-refined` und `fixed-gui-refined`. Neuer eingefrorener Gesamt-CIpass
auf aktuellem Commit noch erforderlich; Fortschritt im bestehenden RUNSTATE.json.
Neue Quellen/Defaults/Version/Abhängigkeiten unverändert, Benutzerdateien geschützt.

---

# Aktuell: vier rote Kandidatengates gezielt korrigiert, neue Gesamtabnahme offen — 30.10.2026

**Run41126700379/f4a6251 ist NICHT grün; kein Tag und kein Release.** Vier konkrete
rote Pflichtjobs wurden anhand echter Logs auf drei Fehlergruppen eingegrenzt:

1. Windowsreader: `ReaderNavigationOverlayChecks.CheckGeometry` lud für eine reine
   Modus-Diagnose live `settings.json` und traf eine SharingViolation. Kein
   behaupteter Timeout oder pauschal behobener Produktkonfigurationsfehler.
   Reine UI-/Capturemodusleser nutzen nun den eigenen lebenden SessionHost;
   echte Persistenz-/Neustartprüfungen bleiben echte Diskprüfungen. Produkt-
   Config.TryRead propagiert Locks/Permissions weiter, keine Defaultübernahme.
2. Linux x64+ARM64: echte funktionale Lautstärkeprüfung hatte keinen nutzbaren
   Audioausgang. Neue private PulseAudio-Nullsinkfixture mit Ubuntu24.04-Standard-
   paketen, eigenem DBus inkl Receive/Systemisolation, privatem Cookie/Socket,
   genau2erlaubten Modulen, stummem Nullsink/Monitor und0Hardwarekarten; kein
   zusätzlicher Modulimport nach Startup. Pulse16-ModulIDs werden mit der Short-
   liste abgeglichen statt ein nicht existierendes JSONindexfeld anzunehmen.
   LinuxCI verlangt zusätzlich explizit positive gerenderte Audiobuffer und
   funktionale Lautstärkebedienung; der No-output-Zweig darf dort NICHT abnehmen.
3. Macsources: alter Gate verlangte2 statt aktueller5Fixtures. Gemeinsamer
   Prüfer verlangt alle5einzigartigen gepinnten Hashes, PASS/exit0/keinTimeout;
   fehlende, doppelte, unbekannte oder fehlgeschlagene Fälle werden verworfen.

Lokale echte Nachweise (keine unveränderliche Releasepaketabnahme):
- Private Ubuntu24.04-Container, Netzwerk aus/keine Audiogeräte: Pulsebackend
  tatsächlich decoded141/played70, nativeVolume61, nativeMute1→0, Playing;
  Nullsink vor/nach stumm,0Hardware, beide Dienste+Kind vollständig beendet.
- Derselbe Ubuntuaufbau mit portablem offiziellen.NET10, echten Xvfb/XTest-
  GUItests und EXPLIZIT erforderlichem funktionalem Audio: komplette geänderte
  Galeriefeatures sowie alle nativen Medieneingabestufen PASS, inkl tatsächlicher
  Lautstärke64 bei weiterlaufendem Video. Beide Befunde+AudioCleanup0 stehen in
  `artifacts/release-1.0.2-ci-fix-verification/pulse-container-proof/evidence/gui-02/`.
- Getrennter tatsächlicher No-output-Lauf: Audiotracks2/decoded>0/played0,
  deaktivierte Regler; komplette native Medieneingabe PASS nach physischem Klick
  auf deaktivierte Ziele. Kein vorgetäuschter Home/End-/Volume-Fokusnachweis.
- Neue Corpus-/Audio-/Evidenzverträge gezielt grün; vor finalem Kandidaten noch
  aktueller separater Python-Gesamtlauf und dieselbe vollständige13Job-Matrix.

Frühere Hilfsversuche (fehlende private DBusReceiveregel, Pulse16JSONindex,
  unportabler ManjaroCoreCLR im Ubuntucontainer) sind als FAIL erhalten und
  KEINE Produkt-/Paketabnahme. Kein lokaler Systemaudiodienst wurde verändert;
  nur eigene private Prozesse/Container. Fremde.gitignore/alte Vorbereitungsdatei
  bytegleich uncommitted. Neue Quellen bleiben aus; manueller Windowsfeldtest
  weiterhin NACH regulärer Veröffentlichung, technische Gates weiterhin davor.

---

# Aktuell: weitere echte Gatefehler geklärt, nächster Gesamtpass offen — 30.10.2026

**Noch kein Tag/Release; reguläres 1.0.2 bleibt Ziel.** Neuere Fortsetzung bis
Produkt-/Prüfcode `76d0a59` auf `release/1.0.2`; die folgenden Abschnitte bleiben
historische Zwischenstände, nicht die aktuelle Abnahme.

- `caab70e`-Kandidat Run41119303069: Macpaket fand `GalleryVideoPlayPause` in der
  ersten Readinessbeobachtung noch nicht; `Single` brach vor Wiederholung ab.
  Jetzt eindeutiges `SingleOrDefault` während Readiness, Präsenz/Enabled bleiben
  Pflicht, Duplikate scheitern weiter, 10s-/Frame-/Decodergrenzen unverändert.
- Kleinstbildprüfung verlangte einen 10%-Schritt UNTER Fit, stellte zuvor aber
  nur Fit>1 sicher. Fixture112×127 plus explizites Fit>1.1 und exakter1.1-Schritt;
  nativ bei760×500 tatsächlich Fit2.133858…/Zoom1.1 PASS. Originalpixel-/1:1-/
  Resize-/Kontext-/Integritäts-/Cancelprüfungen unverändert erhalten.
- Der folgende native Fehler war ein `SetEmpty` NACH permanentem `StopAsync`.
  Dass geschlossene Viewer späte Updates ignorieren, ist gewollt. Live-Emptyaktion
  wird nun VOR Stop geprüft, danach zusätzlich explizite No-op-/keinCallback-
  und KeinPixel-Nachweise. Derselbe Fehler ist in Run41122518068/macOS bestätigt;
  dieser Lauf (`48d423d`) ist deshalb NICHT releasegrün.
- Lokaler vollständiger Solutionlauf: 0Buildfehler/19Warnungen,13Testprojekte,
  **3424PASS/2FAIL/8SKIP**. Kein Gesamtpass! Beide Fehler in PluginRuntime:
  a) externer Tempüberlauf war bereits vor der StartHandshakeabsicherung aktiv,
  b) erster normaler Read nach Retry überschritt den testinternen200ms-Takt.
  Tempfixture schaltet jetzt erst nach Startup0→1025Bytes bei1024Limit; Produkt-
  schutz bleibt unverändert.32RuntimeProcessfälle danach PASS, einschließlich
  unverändertem200ms-Retrytest. Exakte Timingursache des ersten Fehlers nicht
  behauptet; Kandidaten-Gesamttests bleiben erforderlich.
- Separater Pythonclone aufcaab70e: **357PASS/1SKIP**, Root-/Fork-/Git-/Quellguards
  unverändert. Kein Native-/Paketpass daraus. Ergänzender privater Linux-GUIlauf
  mit Live-/Closed-Viewerrevision läuft noch; nach Fehlern gestoppte Reader- oder
  Selectionmodi sind NICHT als bestanden gezählt.

Alle Änderungen eng auf tatsächliche Produkt-/Prüfdefekte beschränkt, keine neue
Quelle, kein Queuewachstum oder gelockertes Herkunfts-/Rechte-/Audiogate. Neues
vollständiges stable-candidate/all/all auf dem gesamten neuesten Commit nötig;
Erst dessen13Jobs/18same-runAssets freigeben, dann regulär veröffentlichen und
anonyme Downloads/Hashes prüfen. Manuelle Windows-Praxisabnahme danach.
Live Fortschritts-/Belegpfade: `artifacts/release-1.0.2-ci-fix-verification/RUNSTATE.json`.

---

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
