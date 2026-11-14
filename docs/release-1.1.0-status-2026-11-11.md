# MeowChan 1.1.0 — Umsetzung und Abnahme, noch nicht releasefertig

**Abgleich: 14.11.2026, 10:26 CET. Gepinnter Produktbezugsstand:**
`308e6780bf054163b257a4e602c0bcaa03c0e022` (kurz `308e6780`).
G0109 ist beendet: App, DesktopTests und Smokeprojekt kompiliert;
**166 enge Nicht-GUI-Fälle bestanden, 0 FAIL / 0 SKIP**. Danach geschützter
Fast-Forward von `goal/gesamtprogramm-profile-a` von `437e5662` auf diesen SHA
und Hauptcheckout-Appbuild mit **0 Fehlern / 4 Avalonia-Warnungen**, fertig
10:26:18 CET. Das ist **kein finaler Freeze und keine Releasefreigabe**.
Keine Vollsuite-/GUI-/Plattform-/Endpaketabnahme daraus ableiten; kein Push,
Tag oder Upload. Claude UX Wave 3 ist weiterhin nicht als final übergeben
oder integriert gezählt.

Der frühere Hauptcheckout-Anker `6e8e4437` und der damals getrennte Codex-Stand
`88627d6b` sind Vorfahren dieses Bezugsstands. Die Aussage „Codex noch nicht
nach goal übernommen“ vom 13.11. ist damit überholt. Diese Fortschreibung
verwendet den erhaltenen Root-WIP als Ausgangstext, trennt heutige Integration
von historischen Belegen und bindet Ergebnisse an ihren tatsächlich geprüften
SHA. Der vorherige Dokumentabgleich auf `3b9e7e04`/G0105 bleibt unten als eigener
Nachweis erhalten; die neuen G0106–G0109-Ergebnisse ersetzen ihn nicht rückwirkend.

## Entscheidung und Umfang

Das nächste Release heißt **1.1.0**, nicht 1.0.3. Der Umfang umfasst den
Danbooru-Durchstich mit Suche, Einzelpost und Watchern, die vorhandenen
Chan-Quellen, lokale Medien und eigene Tags, den überarbeiteten Reader und die
Galerie, Plugin-Übergang, Profilpersistenz, Bedienoberfläche und Statistik.
Die interne Produkt-/SDK-Version ist bereits 1.1.0; dies ist kein Release-Tag.
Weitere Booru-Familien gehören zur gesonderten 1.2-Linie und werden nicht
stillschweigend Teil dieses Kandidaten.

Alle Tests verwenden synthetische Daten, Fake-HTTP oder Loopback und isolierte
Profile. Keine Live-Providerabnahme und keine Tests gegen das persönliche Profil.

## Integriert auf goal am gepinnten Bezugsstand

„Integriert“ bezeichnet hier den Quellstand, nicht eine bestandene vollständige
Abnahme. Die früher getrennten Arbeitsstatus werden nicht als zusätzliche offene
Implementierung weitergeführt.

| Bereich | Tatsächlicher integrierter Stand | Abgrenzung |
| --- | --- | --- |
| Header-I/O | Zwei Graphflächen: Network I/O mit Down/Up, Disk I/O mit Read/Write; gemessene eigene Nutzlast-/Medien-I/O, keine vollständigen OS-/TLS-Zähler. Lokalisierte Wörter sind Standard, adaptive Raten haben drei sichtbare Ziffern, farbige Richtungsicons sind optional (`ff54a543`, `4c4601d5`, `2e3ab7e9`, `6949018e`). | Keine vier Einzelgraphen oder Richtungssummen; native Kontrast-/Plattformabnahme offen. |
| BP2 Booru-Seite | Lazy-Hauptseite hinter Medien, bestehende Workbench, eigener Host/Lifetime und gespeicherter Anzeigeschalter im Footer/Appmenü (`bc1a2573`, `0463773b`, `15734d44`). Booru standardmäßig Primary+6, Quellen & Plugins 5, Status 7; gespeichertes `navigation.plugins=Primary+6` bleibt vorrangig und führt zu Alle Plugins. | Ausblenden ist keine Quellenautorisierung, Plugin-Deaktivierung oder Watcher-Pause. Öffnen/Vorbefüllen sendet keine Suche. |
| BP3 und BP2-Aktionsgates | Exakter lokaler Booru↔Medien-Sprung, Bereinigung inkompatibler transienter Filter, Aktion im Medien-Mehr-Menü (`8815b062`); Details-Suchaktion an Live-Verfügbarkeit gebunden (`135ca876`). Handoff/Fokus nach Aktivierung, Present/Layout an Navigation und Quellkomposition gebunden; tote MainWindow-Fensterreste entfernt (`b4c5264e`, `3b9e7e04`). | Keine entfernte Abfrage oder Originalaufnahme durch den lokalen Sprung. Bewusst eigenständige Workbench-Window-Fixtures sind nicht die normale Shellroute. GUI-Migration/-Abnahme bleibt separat. |
| Booru-Lebenszyklus und Cooldown | Suche/Einzelpost, Originalqueue, Katalog, Cursor-Watcher und Offline-Medien; F2/F3-Suspend/Drain/Resume und globale Sitzungspause sind integriert (`3bc753c5`, `3fa0ce4f`). Manuelles Jetzt prüfen bleibt während globaler Pause erlaubt, ohne Entpausieren (`929917e8`). Retry-After-Deckel: 24 h Standard, 1–168 h konfigurierbar, 60 s Minimum; Live-Absenkung, terminale Jobs und Reset-Grenzen (`5392aea2`, `02536a70`). | Frühere ungekappte Retry-After-Annahmen sind überholt. Kein automatisches Resume/Replay, kein versteckter Ratingfilter; letzter vollständiger End-to-End-Lauf offen. |
| Status / Quellen & Plugins | SM1/SM2-Statusseite mit Statistik/Diagnose und erhaltener Slot-7-/Segmentsemantik; gemeinsame Quellen-&-Plugins-Seite (`c4090396`), aktive Quellen vor inaktiven mit Chip, Plugin-Details auf zweiter Ebene, Dropdown Quellen/Bildfilter/Alle Plugins. | `navigation.plugins` ist Kompatibilitätsroute, keine separate Hauptseite. Weitere Claude-Statuspakete SM3–SM7 nicht pauschal mitgezählt. |
| Settings / Limits | SP1 draft-only Reset (`76f30070`), SP2 Export aus reinem gespeichertem portablem Snapshot (`782d9d9d`), SP5 konfigurierbare Grenzen mit produktiven Callern (`cadf74d3`, `d8b89687`) und Referenz-/Caller-Verträgen (`b3729bf2`). | Kein Claim, dass sämtliche Settings-Planpakete oder eine Settings-Importoberfläche fertig seien. Save/Discard, Berechtigungen und Validierung bleiben getrennt. |
| Arbeitsansichten | WV0–WV9-Policyflächen einschließlich Galerie; WV11 Übersicht/Differenzfilter (`ce4a9c32`); WV12 kompakter Wechseltoast, Undo, geschützter Details-Host mit Differenz-/Fokusroute und Hinweisdauer (`5a0d9cd9`, `874c6270`, `ee00ba7f`). | Reduktion ist kein Datenverlust; Required-/Safety-Flows bleiben erreichbar. Vollständige Ansichts-/Breiten-/Fokus-Smokes offen. |
| Cache / Staging / Poster / Codex-M5 | Poster-/Scrub-Grundfunktionen mit Profilhost und Wartung (`630fc66f`); Pluginimport-Ownership, Quarantänemarker vor Payload und Unix-Publish ohne Überschreiben (`a82c8ca1`, `53dfbda6`); PNG-/DEFLATE-Member-/Tabellengrenzen (`8c5530a6` und Vorgänger); Galerie-Preloader auch bei Verzögerung 0 außerhalb UI-Thread, bestätigte Decodes behalten und keine Lease an abgebrochene Aufrufe (`221f441a`, `5bde1ea8`). | „M5“ hier = Codex Galerie-stat/Preloader, nicht Claudes Medienfilterraster. Gezielte Belege unten ersetzen weder vollständige Cache-/Plattform- noch Playbackabnahme. |
| Medien, Reader, Tags und bestehende UX | Lokale Identität, eigene Tags, gemeinsames Medienmenü, Import und v1-Backup; vorhandene Reader-/Galerie-/Conversation-/Filter-/Chrome-/Designpakete samt älteren b10–b13- und Codex-Nachläufen sind enthalten. | Kein Vorgriff auf den noch nicht final übergebenen Claude-Wave3-Stapel; gemeinsame Host-/Stempelbindung und native Abnahme bleiben abzugleichen. |
| Help / Locales | EN/DE-Hilfe für Settings/WP/Status/WV12, konfigurierbaren Cooldown (`9563892e`) und tatsächliche BP2-Seite/Anzeige/Navigation (`d211c18c`). Neue Settings-/WV-/Quellen-/BP2-/BP3-Schlüssel in EN/DE und zehn zusätzlichen Packs; auch HTML-Export-Ergänzungen aus `5b467df1` sind Vorfahren des Bezugsstands. | Kein globaler Help-/Sprach-/Muttersprachler-PASS. F1 kann eine wirksame Belegung trotz ausgeblendeter Booru-Seite weiter nennen; das macht das Ziel nicht verfügbar. |
| Paketierung / API | Öffentliche Plugin-Allowlist und physische Alias-/Zielbaumkontrolle (`a32f203b`), AP04 strikt gegen unbekannte Assets; dokumentierte Coreconsumer-Migration über `StartAsync(lease)` sowie API-Review ([Inventar](releases/v1.1.0-api-review.md)). | Private Plugins bleiben außerhalb öffentlicher Repo-/Releaseinputs. Kein Endpaket-/Apphost-/echtes-1.0.2-Upgrade-PASS; finale Analyzer-/Consumerprüfung offen. |

## Noch abzugleichen / nicht als final geliefert zählen

**Claude UX Wave 3** (`integration/ux-wave3-20261114`) ist ein beweglicher
Integrationszweig, kein übergebener Freeze. Der im 09:38-Status genannte
Zwischenanker `2a117ebf` ist kein finaler Liefer-SHA. Viewer V1/V2/V3,
TP3/TP4/PT1/PT5 und gemeinsame Host-/Stempelbindung, Reader-Reste,
Archiv/Medien M2–M5, Profile PR1–PR3, Modals/Kürzel, Taglines/HB1,
SM3–SM7/ADM-A sowie Sprachreview/Plurale und Designsystem-/Icon-Reste bleiben
mit Claudes finalem Stapel und tatsächlicher Restliste abzugleichen. Diese Liste
ist eine Übergabeabgrenzung, keine Behauptung, jeder Punkt sei dort noch ungebaut.

Beim Merge insbesondere beide DefaultsRevision-8-Erweiterungen bewusst
zusammenführen, gespeicherte Overrides erhalten, BP3-/Booru-Medien-Lifetime-Hooks
in Toolbar/Filters/WorkView/Return einschließlich optionalem Gate-Parameter
bewahren und Codex-M5 nicht mit Claudes M5 verwechseln. Keine parallele zweite
Cache-/Settings-/Booru-/Help-Umsetzung aus dem alten Status ableiten.

BooruMedia- und BooruComposition-Smoke-Fixtures sind auf den Seitenhost
portiert (`dd6af97b`, `6e567e7a`); ein kompilierendes Smokeprojekt ist **kein
ausgeführter Smoke**. Der nachfolgende statische Review fand noch zwei konkrete
Prüflücken: die URL muss tatsächlich genau einmal in das isolierte Clipboard
geschrieben werden (kein Fehlerstatus als Copy-Erfolg), und „Booru aus“ muss
über den echten Shell-Schalter statt nur über eine Config-Mutation geprüft
werden. Deren Fixture-Nachlauf und die native Pause-/Serviceverlust-Regression
sind noch nicht integriert/ausgeführt.

Produktiver Booru-Nachlauf auf dem Bezugsstand: Interaktions-/Auswahlrevisionen
widerrufen einen verzögerten Medien-Handoff, ohne normale Index-/Buildwechsel
mit Nutzereingaben zu verwechseln (`5e7b5750`, `7789d258`). Ein aktueller
Navigationsfehler zeigt eine eigene lokalisierte Meldung, während veraltete
oder bereits disposed Aktionen keine neue Notice überschreiben (`437e5662`,
`8a35e58d`, `cbb53752`). Der vorgemerkte Downloads-Fallback umgeht keine
Busy-/Restore-Gates (`775c16e1`); nach Pause oder Einstellungsanwendung wird er
nach Freigabe des tatsächlichen Busy-Owners erneut versucht (`ab24f601`).
Die zusätzlichen Guard-/Wiring-Tests (`5dd58aac`, `ab24f601`) sind keine
native Shell-Abnahme dieses Ablaufs.

Fremder Root-WIP, insbesondere `.gitignore`, bleibt außerhalb dieser
Fortschreibung. Nur die frühere enge Ausnahme
`!tests/Fixtures/persistence102/*.json.bak` gehörte zum damaligen Kandidaten;
andere Ignore-Änderungen werden weder pauschal übernommen noch zurückgesetzt.
Kein belastbares Gesamtprozent und kein RC-Datum; Umfang und finale Abnahme
werden nicht durch einen kompilierenden Dogfood-Build ersetzt.

## Fertige aktuelle Teilnachweise — keine Gesamtfreigabe

| Lauf / SHA | Fertiger, belegter Umfang | Ergebnis / Grenze |
| --- | --- | --- |
| G0099 / `5bde1ea86aaa21f167479e8476054a2f2e174a91` | VideoPosterCacheMaintenanceTests, PluginImportStagingTests, GalleryCacheIntegrityTests; beendet 14.11., 09:19:19 CET | **88 PASS, 0 FAIL, 0 SKIP**; nur diese Klassen auf diesem älteren SHA. |
| G0103 / `d211c18c856bf3f14827ef5670b3b29b125d9bc6` | App und DesktopTestprojekt im Hauptcheckout kompiliert | Beide Exit **0** (App 5 Warnungen, 0 Fehler); kein Test-/GUI-/Release-PASS aus der Kompilierung. SHA-Zuordnung aus 09:49-Nachtrag und anschließendem G0104-Pin. |
| G0104 / `d211c18c856bf3f14827ef5670b3b29b125d9bc6` | Enger Nicht-GUI-Lauf, vor/nach gleicher Quellen-SHA; beendet 14.11., 09:47:27 CET | **106 PASS, 1 FAIL, 0 SKIP**, Exit 1. `BooruPageBehaviorRegressionTests.ShowQueryBeforeAndAfterActivationOnlyChangesTheRealDraft`: leere TabControl-Suche in unpräsentierter Fixture. Erstfehler bleibt erhalten; keine Assertion entfernt. |
| G0105 / `3b9e7e04646f3a927b050ea53de320b28be8183b` | Isolierter Kandidat: App, DesktopTest- und Smokeprojekt kompiliert; enger Nicht-GUI-Lauf. Danach geschützter Root-FF und Root-Appbuild; beendet 14.11., **09:54:14 CET** | Compile-Endcodes und Rootbuild **0**; **138 PASS, 0 FAIL, 0 SKIP**, Test-Exit 0. Enthält Fixturekorrektur und zusätzliche Lifetime-/Fokusguards. Kein Vollsuite-/GUI-/Paket-PASS. |
| G0106 / `437e56620bbb65b9a3ffe72b07d05f29784e8039` | App, DesktopTests, Smokeprojekt und Hauptcheckout-App gebaut; fertig 14.11., 10:03:12 CET | Alle Compile-Endcodes **0**; keine Testausführung auf diesem SHA. |
| G0107 / `6e567e7a584b3eccc74a3adc3e0653dfec702ff7` | App/DesktopTests/Smokeprojekt kompiliert; enger Nicht-GUI-Lauf, fertig 10:07:16 CET | Compile **0**, **154 PASS / 4 FAIL / 0 SKIP**. Vier neue BooruLinkLifetimeTests lasen nach await Controls auf einem fremden Thread; Erstfehler bleibt erhalten. |
| G0108 / `a61b1efac9a47e8682d1f369d551781a9a2cdecc` | Dispatcher-/Interaktionsnachlauf; App/DesktopTests/Smokeprojekt und enger Lauf, fertig 10:13:40 CET | Compile **0**, **159 PASS / 0 FAIL / 0 SKIP**. Tatsächlichen Handlerabschluss abgewartet; keine Assertions entfernt oder Skips ergänzt. Kein Root-FF durch diesen Lauf. |
| G0109 / `308e6780bf054163b257a4e602c0bcaa03c0e022` | Pause-/Settings-Owner-Hook und zusätzliche Media-Handoff-Guards; App/DesktopTests/Smokeprojekt, enger Testlauf, geschützter Root-FF und Hauptcheckout-Neubau; fertig 10:26:18 CET | Alle Endcodes **0**, **166 PASS / 0 FAIL / 0 SKIP**, Root-App 4 Warnungen / 0 Fehler. Kein GUI-/Vollsuite-/Paket-PASS. |

G0105 prüfte die drei G0099-Klassen plus DeflateBoundaryConformanceTests,
BooruPageBehaviorRegressionTests, BooruPresentationActionGateTests,
BooruPageNavigationContractTests, BooruCrossLinkExactFilterTests und drei
HelpDocumentationTests: `DocumentedSavedPluginsOverrideWinsWithoutChangingStatusOrOtherPageSlots`,
`BooruDisplayHelpMatchesTheSavedPresentationSwitchAndItsHideScopes`,
`LocalBooruMediaLinkHelpDescribesTheIntegratedPageBridgeWithoutFetching`.
Das ist ausdrücklich **nicht die gesamte HelpDocumentationTests-Klasse**.
G0104 wurde nicht nachträglich grün umetikettiert; G0105 ist ein eigener Nachlauf
auf geändertem SHA und erweitertem Filter.

G0108/G0109 ergänzen BooruLinkLifetimeTests, BooruUnavailableNavigationTests,
BooruNavigationFailurePackTests und im G0109 zusätzlich
BooruMediaHandoffInteractionTests sowie zwei Source-Wiring-Fälle für die echten
Preference-Owner. Die uninitialisierten Page-Guard-Tests belegen Guards, nicht
native Inputzustellung; die Source-Wiring-Fälle belegen die Hook-Reihenfolge,
nicht einen vollständigen Pause-/Shell-Durchlauf.

Lokale Endnachweise (nur gelesen, durch diese Fortschreibung nichts ausgeführt):

- `/home/tomka/.cache/claude-ssd-tmp/g0099-cache-stage-fixes-20261114-091640/`
- `/home/tomka/.cache/claude-ssd-tmp/g0103-bp2-closeout-094245/`
- `/home/tomka/.cache/claude-ssd-tmp/g0104-focused-followup-20261114-0950/`
- `/home/tomka/.cache/claude-ssd-tmp/g0105-booru-page-handoff-20261114-095026/`
- `/home/tomka/.cache/claude-ssd-tmp/g0106-booru-view-compile-20261114-095858/`
- `/home/tomka/.cache/claude-ssd-tmp/g0107-booru-full-candidate-20261114-100500/`
- `/home/tomka/.cache/claude-ssd-tmp/g0108-booru-followup-20261114-101113/`
- `/home/tomka/.cache/claude-ssd-tmp/g0109-booru-owner-closeout-20261114-102219/`

## Historische Integrationsanker — keine doppelte aktuelle Resteliste

Die b10–b13-Einordnungen des Root-WIP bleiben als Herkunft erhalten:
`2dff1957` (b10), `d458d038` (b11), `6f6af42b` (b12),
`f02b09a4`/`6e8e4437` (b13/Header-Labels), `fc03f19b` (sieben weitere Packs),
`486cef82` (SDK-Übergang), `4cb1f548` (Original-Encoding), `a186ac26`
(UTF-8-Clipboard) und `a22dfb0d` (damalige Hilfe). Ihre damaligen offenen
Nachträge sind keine heutige Integrationsdiagnose.

Die alte Kandidatenzusammenführung `ac383708`/`47c0ea90` und Korrekturen
`64d8bc3b`/`dccb6bff` sind nicht durch Rücksetzen fremder UI ersetzt worden.
P1/P4B/P5B samt Validierung/Persistenz (`df2cc612`, `0693eab9`) und
Postfilter-Transfer/Backup (`280049ff`, `49c58060`, `91fdfb39`, `d53f8cb2`,
`39ee014f`) gehören zur integrierten Herkunft; Apply/Close und die Befehle
`post.hide`, `post.unhide`, `reader.view.conversation`, `reader.post-filters`
bleiben auf dem finalen gemeinsamen Stand wirksam abzunehmen.

Same-Dialog-Reparatur/Reautorisierung (`b3eb13bf`, `03365c08`) bewahrt den
Vertrag: geänderte Konfiguration darf keine frühere Zustimmung erben.
Der frühere F2/F3-Workerbasisstand `63247916` war im alten WIP noch als
uncommitted/offen beschrieben; diesen Arbeitsstatus ersetzen die oben genannten
integrierten Lebenszykluscommits, nicht ein behaupteter Endlauf.
Booru-Transport-/Startup-/Shutdown-Nachläufe (`a67fe512`, `b60999d4`,
`81f345fb`, `1a317e38`) und die damalige Hilfe `aa908158` sind ältere
Integrationsanker, kein erneuter vollständiger Testnachweis. Die damalige
Formulierung „langes Retry-After ohne 24-h-Deckel“ wurde durch den oben
beschriebenen **konfigurierbaren** Deckel abgelöst, nicht durch einen neuen
ungekappten Vertrag.

Paket-/API-Herkunft bleibt `70f2eb5e` (Systemrootaliases), `837fb0e0`
(detached Freeze/Testhostfehler), `8ed0c829`/`882a0ec1` (durabler
`StartAsync(lease)`-Consumer; K-1 ist echte Migration für dauerhafte
1.0.2-Coreconsumer, keine Lockerung der Admission). Der private Audio-/Reauth-
Harness einschließlich portablem dotnet-Host (`cee4549e`) und gepinnter
Probe-Dependencies (`141d4e01`) ersetzt keine native Playbackabnahme; keine
persönlichen Dienste/Geräte oder Installation als Behelf.

Die alten zentralen Warteläufe sind beendet, nicht heute aktiv:
`g2305` auf `dccb6bff` endete am 13.11. nach 1800 s Lockwartezeit mit Exit 75,
ohne Build-/Teststufe. Bei `g2340` auf `88627d6b` verschwand zunächst der
Launcher vor Lock-Acquisition ohne Teststufe (inconclusive); derselbe Lauf
wurde um 23:46:55 erneut eingereiht und am 14.11., 00:13:36 vor Lock-Erwerb
auf Nutzerwunsch zurückgestellt (Exit 143, 0 Stufen;
`g2340/deferred-by-user.json`). Weder Timeout noch Rückstellung sind ein
Quellcode-Fail oder ein PASS. Die damalige SHA-/Dirty-Guard-, isolierte-HOME-
und private GUI-/Audio-Planung ist kein ausgeführter Test.

## Historische, commitgebundene Nachweise

Alle folgenden Zahlen bleiben an ihre damaligen SHAs/Quellenumfänge gebunden.
„Nicht goal“, „noch offen“ oder ein damaliger Buildfehler in dieser Tabelle
bezeichnet den damaligen Lauf, nicht den oben gepinnten heutigen Integrationsstand.
Ein späterer Fix löscht weder den Erstfehler noch überträgt er ältere PASS-Zahlen.

| Beleg | Exakter Umfang / Basis | Ergebnis |
| --- | --- | --- |
| Gezielter Desktop-Lauf | b10 `2dff1957` plus sechs hash-gepinnte Dateien, danach identisch in `a186ac26`/`a22dfb0d` committed | **765 PASS, 0 FAIL, 0 SKIP**; darin 67 Clipboard-, 47 Hilfe- und 11 MainPageHeading-Fälle |
| Plugin-Regressionslauf | **PRE-b10** `2f9a817a`; vollständige SourceAdapter-, OfficialPlugins- und PluginRuntime-Suiten | **394 + 210 + 249 = 853 PASS**, 0 FAIL/SKIP; kein End-goal-PASS |
| Claude b10, Übergabebeleg | `2dff1957`; gezielte Core-/Desktop-Läufe | 1720 Core + 1747 Desktop PASS laut Übergabe; Smokes settings-statistics/statistics/reader-post-width/poweruser grün |
| Getrennte Codex-Integration | `0ec2ccbb`; frischer Desktop-Build und gezielter Desktop-Lauf | **786 PASS, 0 FAIL, 0 SKIP**; Smoke-Build danach wegen vier fehlender `ToggleButton`-Typauflösungen fehlgeschlagen. Importfix liegt separat vor; Core-Zieltests **237 PASS, 0 FAIL, 0 SKIP** |
| Runtime/Booru | `8a6e74d7`; vollständige PluginRuntime-Suite plus gezielte Booru-Suiten | **255/255 PluginRuntime**, **211/211 Runtime-Booru**, **591/591 Core-Booru**, jeweils 0 SKIP; kein End-goal-/Core-Vollsuite-PASS |
| Nativer Filter-Baselinecheck | `33958260`; frischer Smoke-Build, private Xvfb/XTest-Eingabe | **FAIL**, 0 SKIP: Press schließt Popup vor Release; negativer Beleg bleibt erhalten |
| Nativer Plugin-Baselinecheck | `8c080706`; private Xvfb | `plugin-change-review` **PASS**, `plugin-reselection` **FAIL**, 0 SKIP |
| AP04-Helper | `42025df2`; vier Python-Suiten und nichtschreibender Dry-run | **59 PASS** (35 + 14 + 8 + 2), kein vollständiger AP04-Lauf |
| Paketierungs-Helper | `d1967445`; Python-Mutationstests | **92 PASS, 1 SKIP** (Apple-native Signaturtools), kein frischer .NET-Paketierungsbuild |
| Reader P1 | `ff9f8d47`; gezielte Desktoptests und Settings-Smoke mit dokumentiertem buildseitigen Namespace-Shim | **210 PASS**, Settings-Smoke PASS; Standard-Smoke-Build und OS-Eingabeabnahme offen |
| UI-Refinement | `4b6f3951`; source-built Linux/Xvfb 1x, Light/Dark | **68 PASS** + nativer `ui-refinement` PASS; nicht goal |
| Plugin-Neustart/Reautorisierung | `fda49159`; synthetische persistente Prozess-E2E | **14 PASS**, 98 Hostprozesse, 0 HTTP-Requests; kein Package-Apphost-PASS |
| Release-Gates-Helperstack | `aec1d600`; sieben Python-Suiten inkl. Special-Files/Allowlist/AP04-Härtung | **98 PASS**; kein .NET-Publish-/AP04-Endpaket-PASS |
| API-Abschluss und Migration | `243d1a2f`, separater Doku-Fix `882a0ec1` | **109 SDK + 83 Core + 5 Policy + 3 Doku PASS** im jeweiligen Scope; K-1 bleibt dokumentierte Migration |
| Gemeinsame b13/B1/F1/F4-Basis | `64d8bc3b`, `g2247`; Quellen-SHA vor/nach gleich, eine ungecommitete generierte Probe-Lockdatei dokumentiert (später `141d4e01`) | **760 Core + 255 Runtime PASS**, 0 SKIP; Desktop-/Smoke-Build **FAIL** am PixelSize-Namespace. Fix `dccb6bff` noch ohne grünen Wiederholungslauf |
| Nativer Filter-Nachlauf | `17e6f0a2`; source-built Linux/Xvfb/XTest, keine Änderung des Sources während des Laufs | **1 PASS, 0 FAIL, 0 SKIP** (`filter-pointer-selection`); kein Published-Apphost-/Plattform-PASS |
| I/O-/Gate-Helper | `141d4e01`, Public-Inventory-Nachlauf `ab6d6591` bei unverändertem Checker-/Inventarcode | **100 Python-PASS**: AP04/Freeze 54, Rootalias 10, Desktop-Paket 14, Plugin-Paket 18, Public-Inventar 4; kein echtes Upgrade und kein .NET-I/O-PASS |
| I/O-Integrationsgate | `88627d6b`, `g2340` | Am 14.11., 00:13:36 CET vor Lock-Acquisition auf Nutzerwunsch zurückgestellt; Exit 143, **0 Stufen**, kein Test-Fail und kein PASS |
| Historischer Gesamtlauf | `83174f54`, 11.11. | Core 9523/0/11, Desktop 3262/12/12, Plugins 1250/6; **historisch**, siehe unten |

Im ersten gezielten b10-Desktop-Lauf war die Titel-Testmap noch ohne die neue
Statistikseite (693 PASS/1 FAIL). Die Seite selbst hatte bereits ihren Titel;
die Map wurde ergänzt, keine Assertion abgeschwächt. Der erweiterte Lauf bestand
anschließend 765/765. Erstfehler und Folgelauf bleiben erhalten.

Lokale Evidenz:

- `codex-help-clipboard-20261113-tlVHds`: Quellenhashes, Befehle, TRX,
  erster Fehler und erfolgreicher Folgelauf unter dem isolierten SSD-Testpfad.
- `codex-plugin-regression-20261113T155244-2709009`: 853 Plugin-Tests,
  Quellenarchiv und Prüfsummen; ausdrücklich PRE-b10.
- Historischer Gesamtlauf:
  `/home/tomka/mc-evidence/ux103-restpakete-20261111/acceptance-83174f54/`.

## API-Review-Nachtrag 14.11.2026

Das [aktuelle API-Review](releases/v1.1.0-api-review-current-2026-11-14.md)
vergleicht den veröffentlichten Tag `v1.0.2` (`eec6c016`) mit dem Quellstand
`ab24f601`. Es trennt additive SDK-/Core-Einträge, die entwicklungsinterne
`BooruPostId`-Umbenennung, die Versions-/Größenkonstanten und den dokumentierten
`RestoreAsync`→`StartAsync(lease)`-Verhaltensbruch für profilgebundene
Core-Consumer. Das deklarative Inventar ist geprüft; kompilierte Analyzer-,
Paket-/Binärconsumer- und echte Upgrade-Nachweise bleiben die unten genannten
Release-Gates. Kein pauschaler Binärkompatibilitäts-PASS.

## Offene Release-Gates (nach gemeinsamem finalem Integrationsfreeze)

1. **Freeze und Herkunft:** eindeutiger SHA/Tree, frischer isolierter Candidate,
   Quell-/Plugininventar, lokale Release-Pakete und Prüfsummen an denselben SHA
   binden. Den absichtlich dirty Hauptcheckout nicht für ein Gate „aufräumen“.
2. **Vollsuiten:** Core, Desktop und sämtliche Plugin-/Runtime-/Packaging-Suiten
   einmal zentral auf diesem Stand; FAIL/SKIP vollständig bewerten. Ein
   gefilterter PASS ersetzt die Vollsuite nicht.
3. **Alle Registry-Smokes:** einschließlich Reader-/Galerie-/Statistik- und
   Kompositionspfaden. `ui-refinement` war bereits vor b10 rot (selektierte
   Antwortkarte im Light-Modus). Audio-Smokes benötigen die private
   Audio-Umgebung; fehlt sie, ist das kein PASS. Die laufenden Smoke-Fixes müssen
   anschließend auf dem endgültigen Kandidaten nachgeprüft werden.
4. **AP04 Upgrade:** exakter released 1.0.2-Code
   `eec6c0169f8d52f7481ea0bf7febfd20981e97c3`, synthetisch damit geschriebenes
   Profil, Upgrade und zwei Offline-Neustarts; danach Writes/Readbacks und
   byteidentisches `private-tags.v1.bak`. Runner/Checker liegen bereits im
   Repository. Ein fehlender finaler Paketsatz bedeutet **SOURCE-ONLY**, niemals
   Release-PASS. Keine echten Nutzerprofile.
5. **AP04 Paketbezug:** Der veröffentlichte 1.0.2-Quellstand ist lokal gepinnt.
   Ein optional mitgeliefertes Release-Artefakt wird lediglich gegen seine
   Prüfsummen geprüft; dessen Apphost wird vom aktuellen Runner nicht gestartet.
   Auch die 1.1.0-Paketbindung ist bislang ein Herkunftsnachweis, kein Ausführen
   des gepackten Kandidaten. Der Runner weist diese Grenze ausdrücklich aus.
   Endpakete und tatsächliche Packaged-Apphost-Abnahme bleiben separat offen.
6. **API/Bestandsdaten:** Release-Inventarchecker und Compiler-Analyzer, aktuelle
   Transfer-/Backupgrenzen (insbesondere Postfilter), Fehler- und Recoverypfade;
   alle offenen Muss-Befunde ausräumen.
7. **Nutzer-/Plattformabnahme:** Windows, macOS, Hardware, Skalierung, Tastatur
   und Screenreader mit Tom. Linux-Unit- oder Xvfb-Tests ersetzen diese nicht.
8. **Restentscheidungen:** Light-Fortschrittsspur auf Raised/Hover/AccentSurface
   erreicht mit bisheriger Füllung nicht überall 3:1; keine stillschweigende
   Abnahme. Die sieben zusätzlichen HTML-Export-Sprachen aus `5b467df1`
   sind inzwischen Vorfahren des goal-Bezugsstands, aber nicht dadurch
   muttersprachlich abgenommen. Sprach- und Kontrastgrenzen dürfen nicht durch
   einen pauschalen Release-PASS verdeckt werden.

## Historische Belege bleiben historisch

Der Gesamtlauf auf `83174f54` zeigte fehlende Übersetzungen, veraltete
Plugin-Testannahmen und einen Desktop-Dispatcher-Konflikt. Ein damaliger
gefilterter Lauf behebt keinen Vollsuite-Konflikt. Der Metadatenvergleich des
persönlichen Logordners war wegen einer parallel laufenden persönlichen App
kein belastbarer Isolationsnachweis; diese App wurde nicht verändert.

Die älteren [1.0.3-Gate-Belege](release-1.0.3-closure-gate-2026-11-11.md) bleiben
commitgebunden. Ihre Aussage „nur Plattformbelege offen“ gilt nicht für den
später erweiterten 1.1.0-Umfang.
