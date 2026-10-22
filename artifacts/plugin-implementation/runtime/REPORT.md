# Runtime / Host / Worker — Ergebnis

Stand: 22.10.2026. Implementierungsrolle `build_runtime`, isolierter Worktree
`/home/tomka/Desktop/oChan-1.0-workers/runtime`, Basis `dc053f8`.

## Liefercommits

In dieser Reihenfolge übernehmen:

1. `205c049` — optionales Hostprojekt, Artefakt-/Client-/Serviceverträge.
2. `829d9db` — private Duplexruntime, Worker, HostHttp, Prozess-/Aufrufbudgets.
3. `4a1b961` — echte Childprozessfixtures, Prozessbaumführung, Fehlerklassifikation,
   gemeinsames temporäres Budget und erste Racekorrektur.
4. `8612d72` — geprüfte Lifecycle-/Admission-/Reviewkorrekturen, HostHttpgrenzen,
   Paketmetadaten/README und abschließende Regressionen.

Fremde APIabhängigkeiten wurden nur für Prüfungen übernommen und dürfen nicht
nochmals als eigene Lieferung integriert werden:

- Budgets `a773c43` / lokaler Cherry-pick `aaed8f7` (HTTP Deferred).
- SDK `7813465` / lokaler Cherry-pick `9af34c8` (SDK-01–04).

## Implementiert

- `MeowChan.PluginHost` referenziert SDK und libmeowchan; Core bleibt unabhängig.
  SDK enthält keine IPC-/Hostprozessimplementation. Host-Paket hat LICENSE,
  README, Repositorymetadaten und ausdrückliche 1.x-Breaking-Aussage.
- Eigener `MeowChan.PluginWorker`, privates stdin/stdout-Framing mit 32-bit
  little-endian Länge, UTF-8-JSON, Protokollmajor1, maximal16MiB. Console-Out des
  Plugins geht nach stderr; Logs werden als begrenzter Ring behalten.
- Handshake/Describe, Sourcecatalog/-thread, Filter und Settingsvalidierung.
  Genau ein öffentlicher Pluginentrypoint pro Worker. Describe macht keine
  Hostserviceübergabe. SDKvalidierte Beschreibungen/Ergebnisse und explizite
  Fehler statt Teilresultaten, Quellenende oder Buildfallback.
- Korrelation über Request-, Worker-, Ausführungs-, Konfigurations-, Ressourcen-,
  Generations-, Rule- und Grantrevision. Host-HTTP und Imagechunks sind asynchrone
  Gegenanfragen desselben Kanals und an den noch lebenden Hauptaufruf gebunden.
  Callende/Abbruch/Crash cancelt auch bereits laufende Gegenanfragen auf beiden
  Seiten; keine synchrone IPC-Brücke im Session-/UIgate.
- FIFOadmission, global4 Calls, maximal128 wartende Calls und höchstens8 lokale
  Wartende pro Worker; global begrenzte ausstehende Aufträge.4 Produktions-/1
  Testprozess,2 gleichzeitige Starts,5min Idle-Eviction sowie frühere Eviction bei
  Kapazitätsbedarf. Frei werdende Idleworker bedienen wartende andere Plugins
  sofort. Konsistente Reihenfolge von Test-/Startadmission.
- Handshake/Describe10s, Operation45s, kooperativer Abbruch2s. Danach Kill eigener
  Prozessbäume. Unix-Prozessgruppen sowie Windows Kill-on-close-Jobobjects;
  eigene Arbeitsmappen werden beim Shutdown/Fault aufgeräumt. Ein Callfehler wird
  erst nach Cleanup der ausgefallenen Connection an den Filtercaller gegeben.
- Drei automatische Restarts in10min mit1/5/30s; danach `intervention-required`.
  Explizites `RetryAsync` beginnt eine neue begrenzte Episode. Capacity-Eviction
  eines gestorbenen Workers umgeht weder Verzögerung noch Restartbudget.
- Source-/Filterheap256/512MiB über DOTNET_GCHeapHardLimit, niedrigere Priorität.
  Managed-OOM nur bei explizitem Workermarker plus Exitcode72 als `memory-limit`;
  beliebiger Prozesscrash wird nicht als Speichermangel behauptet.
- Ressourcenmanifest: SHA-256, RIDauswahl, logische Namen, Pfadgrenze und Ablehnung
  von Links; eigene Ausführungsrevision unabhängig vom Compilefingerprint.
- Temporäres Gesamtbudget512MiB; maximal10.000 Dateien UND Verzeichnisse pro
  Worker. Bounded Scan einmal/Sekunde, gecachte Bytes für Stage-/UIzugriffe ohne
  Vollscan pro Imagechunk. `ExternalTemporaryBytes` bindet Stagebytes ein;
  `GetTemporaryBytes` liefert den letzten Scan. Es handelt sich um periodische
  Kontrolle mit Beenden, nicht um OS-Quoten oder eine Sandbox.
- `PluginHostHttp` benutzt die bestehende RequestPolicyState/-Handlerpolicy.
  Pacing/Cooldown wird mit tatsächlichem RetryAt sofort Deferred; belegt keinen
  globalen Workerslot. GET/HEAD, deklarierte Metadatahosts, explizite Redirects,
  geprüfte Header, dekomprimierte Bodies maximal8MiB. Kein Pluginretryloop.
  Injizierte Transporte müssen Autoredirects/zusätzliche Retries deaktivieren;
  der bereitgestellte Transport tut das.

## Ausgeführte Prüfungen

Linux x64, .NET10, ausschließlich eigene temporäre Workspaces und gebaute
Fixturebinaries. Alle Build-/Testläufe unter
`/tmp/meowchan-ui-1.0-build.lock`, Release, NodeReuse deaktiviert.

- Host und Worker separat gebaut: PASS, keine Runtimebuildwarnungen.
- Gesamter Runtime-Testbaum:36/36 PASS,0 übersprungen (52s).
- Nach der letzten Cleanup-/Cachekorrektur:35/35 PASS (15s) plus separat erneut
  der echte Restartbudgettest1/1 PASS (37s). Damit alle36 Tests auf dem finalen
  Code geprüft.
- TRX-Artefakte (nicht ins Git aufgenommen):
  `artifacts/plugin-implementation/runtime/tests/runtime-final.trx`,
  `runtime-final-cleanup.trx`, `runtime-final-restart.trx`.

Die36 Tests umfassen echte Workerprozesse für Sourcecatalog/-thread und echte
Filterbytes/Hostgegenanfragen; malformed JSON/oversize/closedchannel/major;
Generationsmismatch; Timeout, kooperativen Abbruch, Crash, OOMmarker;
Kindprozesscleanup auch nach Workerparent-Crash;100 unmittelbar aufeinander
folgende Serialcalls; Logs/Consoleisolation; Ressourcenhash/-pfad;
Tempgrenze und unmittelbar danach leere eigene Arbeitsmappe; Globalbudget bei
leerem Workerordner; wartende Admission/Cancel; Kapazitätswechsel und
Weiterfortschritt; reales1/5/30sRestartbudget und expliziter Retry;
Backgroundhostcalls bei Return/Crash und wiederholte Service-Slotfreigabe;
null Settingsdiagnostics; kontrollierter Handshake/Describe/Teststart-Lockorder;
HTTPcooldown ohne gehaltenen CallsSlot, deklarierte/unerlaubte Redirects,
GET/HEAD und tatsächliche dekomprimierte8MiB-Grenze. HTTPtransportdaten sind
kontrollierte In-memory-Fixtures; es gab keinen Liveproviderabruf.

## Einbindung und Nachweisgrenzen

- Root integriert Solution, Desktop-Referenzen, tatsächliches Paketlayout,
  Versionsmetadaten sowie frischen NuGet-Consumer. Nachbarrollen verwenden
  Manager/Client/Artefaktverträge und teilen denselben Sessionmanager.
- Aktivierung/Grants/Watchlifetime bleiben bei den zugeordneten Owners. Der
  Caller muss CTS bei Generation-/Rule-/Grantwechsel widerrufen; Hostservices
  müssen eigene Grants und Stageleases vor jeder Übergabe prüfen.
- Windows-Jobobjectcode und macOS-Prozessgruppen sind implementiert, hier aber
  nicht nativ ausgeführt. Fünf entpackte SDKlose Releasepakete und native
  Plattformtests bleiben Root-/Releaseabnahme. Ein Linux-Test-PASS ersetzt das
  nicht.
- Freier lokaler Plugincode hat Benutzerrechte. Protokoll-/Dienstkontrollen,
  verwaltete Heaplimits und beobachtete Arbeitsmappen sind keine OS-Sandbox.
- Kein neuer Plugin-/Build-/Konfigurationsstand wird durch diese Runtime
  aktiviert; es gibt keinen stillen Rückfall auf eine andere Fassung.
