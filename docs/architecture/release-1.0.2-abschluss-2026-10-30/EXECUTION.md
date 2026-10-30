# Ausführung und klare Dateizuständigkeit

Keine neue Umsetzung gestartet. Vorheriger laufender Run hat echteShared-root-
Leases; erst dessen Handoffs integrieren/Ownerwechsel vereinbaren. Obergrenze aus
bisheriger Planung:2gpt-6-astra/6gpt-6.1-sol, nicht Pflicht alleSlots zu füllen.
Diese Planungsrunde verwendet keine neuen Worker.

## Rollen / Reihenfolge

| Phase | Verantwortlich und parallel sinnvoll | Gate |
|---|---|---|
|0 | R0 C102-00 lokal am kritischen Pfad; A2 unabhängig Scope-/Belegreview | vollständiger heutiger WIP, neuere Source14+ prüfen |
|1 | R0/A1 C102-01; S4/A1 C102-02-Legacyvertrag und vorhandenesRepair-UI getrennt; S2/S3 C102-05 | Core-/Profile-/Mediafiles disjunkt leasen, keine parallelen Autoren inHost/Registry |
|2 | A1 C102-03 nach01; S1 liefert getrennte Telemetrie-/Mehrfach-UIcases; S4/Upgrade undS2/S3 können unabhängig Restfehler schließen | DownloadQueue/Metadata/HTTP haben genau einenCoreowner |
|3 | A1+S1 C102-04 nach03 und freigegebenem Priority-V1-Vertrag | kein zweiter konkurrierenderScheduler oder unbegrenzteParameter |
|4 | S6 C102-06 auf eingefrorener Integration; A2reviewt Rohbelege | alle enthaltenen Fixes samtLegacypfad bestanden, keine alten Pässe mischen |
|5 | R0 C102-07 Scope-/Versions-/Commitfreeze, S6nativeCI/Pakete | echte1.0.2Quelle, sämtliche Pflichtplatform-/Upgrade-/Codecgates |
|6 | S6/R0 C102-08 zugänglicherKandidat/Testfahrt, Fehler anOwner zurück | exakter Kandidat; frischeBelege nachCodeänderung |
|7 | R0 C102-09, unabhängigesA2-Votum | regulärerRelease und vollständigeanonymeAssetprüfung |

A1 kann nicht gleichzeitig in zwei Corepaketen dieselbenDateien besitzen. R0
übernimmt unmittelbar blockierende nächsteSchritte selbst, statt sie wegzugeben
und untätig auf Worker zu warten. Nach Rückgabe von Leases kleine überprüfte
Handoffs integrieren. Author-done ist nicht integriert/getestet.

S1/S2/S3/S4 sind Sol: Downloads/ScopedUI, Media, Reader, Setup/Upgrade-UI.
S5 bei Bedarf nur freigegebene Performance-/Fixture-/Packagingteilaufgabe mit
neuen disjunktenDateien, kein OptionalCSVpaket. S6 RoutineQA/Buildslot.
A2 unabhängig, keine eigenen Sicherheitsänderungen selbst freigeben.

## Geteilte Dateien

- Queue/MetadataAdmission/BandwidthLimiter/RequestPolicy/OriginalMediaHttp und
  CoreBaseThread von C102-01→03→04 seriell übernehmen. LibCore bleibt unabhängig
  von PluginHosttypen. Scheduler-/Budgetfehler nicht perUIerrorunterdrückung fixen.
- PluginProfileStore/BundledCandidateMigration/Inventory nur Upgradeowner; Shell/
  Host-/Config-/Lease-/Settingsintegration R0 mit exakterweitergegebenerLease.
- MainWindow.Selection/Menus/ThreadRow, Diagnostics und neuePriorityaktionen S1;
  CommandCatalog/Localizationregistrierung/Harnesswurzeln R0-Lease.
- Gallery-/Video-/GIF-/SoftwareSurface S2, ArchiveReader/Return S3; gemeinsame
  ShellGallery-/NativeInput-/Smokeprojektdateien seriell. Source14-Timing-/Rückweg-
  Assertions nicht zu Featureguardumgehungen abschwächen.
- Versions-/Lock-/PublicAPI-/csproj-/CI-/Notices/Assemblermanifest genaueinRootowner.
  Compiler-/Worker-/Source-/NuGet-/Nativeclosure nach tatsächlichenInputs prüfen,
  keine erfundene literal CoreSourceFilesliste voraussetzen.

## Prüfslot und Freeze

Ein S6oder ausdrücklichR0-Executor unter
`flock --close /tmp/meowchan-ui-1.0-build.lock`;
Buildflags `--disable-build-servers -p:UseSharedCompilation=false -m:2`.
Eigene Outputs,private Profile/Xvfb/DBus,keine persönlicheGUI. Code/Binary/Fixture
vorher/nachherbinden. GlobaleGIT_*-Overrides nicht in allgemeineFixturesvererben;
nur exakte lesende Inventurkommandos gezielt binden. Repo-/Index-/Configguard wie
nach dem bereits dokumentierten Test-Git-Vorfall.

Der Ablauf ist azyklisch: integrierteCodeabnahme(C102-06) → Version/Kandidat(07)
→ Kandidatenprüfung/Testfahrt(08) → Publikation(09). NeueFehler erzeugen eine
neueSourceIteration mit passenderQA, keinen Abhängigkeitszyklus oder altenRunmix.

OffeneLegacy-/Recoveryentscheidungen blockieren nur ihre Codepfade, aber deren
fehlender positiverUpgradebeleg blockiert am Ende den Release. Nicht durch
Weglassen/„bekannterFehler“ veröffentlichen. Bei abweichenderScopeentscheidung
vor Freeze genau den enthaltenenUmfang dokumentieren; Prioritätswunsch nicht
still als erledigt oder nichtmehrgewünscht behandeln.

Auf nativen Windows/macOS-Runners ohne Linux-flock eine gleichwertige joblokale
Serialisierung festlegen. Die freigegebene CI-Matrix darf auf getrennten Hosts
parallel laufen; die globale lokale Sperre schützt den gemeinsamen lokalen
Quell-/Display-/Ressourcenraum, nicht pauschal alle Maschinen weltweit.
