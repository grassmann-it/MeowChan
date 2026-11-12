# B9 synthetische Acceptance-Gates — Claude, 12.11.2026

Bezugsstand: `ab9f99e5` („Run Danbooru watchers through a serial paced scheduler“),
Branch `claude/booru-b9-gates-20261112`. Nur neue Test-/Supportdateien, kein
Produktcode geändert, keine Live-Danbooru-Requests, kein Push/Tag/Release.

Dies ist ein **Teilbeleg** für B9 aus dem
[Booru-Arbeitsplan](../docs/architecture/gesamtprogramm-2026-10-31/pakete/1.1.0-booru-arbeitsplan-2026-11-12.md)
§B9: Host-/Core-Kette mit Stub/Fixture belegt, **kein** UI-/Desktop-Smoke, **kein**
echter 1.0.2-Profillauf, **kein** Release-PASS.

## Was gebaut wurde

| Datei | Inhalt |
| --- | --- |
| `tests/MeowChan.PluginRuntime.Tests/BooruB9AcceptanceSupport.cs` | Harness: `B9World` (Temp-Root, manuelle Uhr, Provider-Stub, CDN), `B9Session` (ein App-Lauf auf einem Profil; Dispose = geordneter Shutdown, neue Instanz = Neustart), `InProcessDanbooruWorker` (echtes Danbooru-Plugin in-process), `B9Provider`, `B9Cdn`, `B9Clock` |
| `tests/MeowChan.PluginRuntime.Tests/BooruB9AcceptanceTests.cs` | 15 Gates (27 Testfälle inkl. 13er-Fehlermatrix), eines davon als Befund F1 übersprungen |

Verdrahtung pro Gate (alles real außer Netz und Uhr):

```text
echtes Danbooru-Plugin (Build-Ausgabe, in-process; gleiche Konfiguration wie Testassembly,
  Abbruch bei fehlendem oder veraltetem Build)
  → BooruSearchService → BooruHostServices → BooruHostHttp (Pacing, Retry, Cooldown, Limits, UA)
  → B9Provider (rendert Danbooru-JSON aus erfundenen Posts; zählt jeden Wire-Request)
  → BooruClaimMapper → BooruOriginalQueue (B4) → DownloadQueue (geteilt) → B9Cdn
  → BooruCatalogStore (B3) + MediaStore-Manifest (verifizierter Offline-Read per SHA-256)
  → BooruWatcherSearchPort → DanbooruWatcherHost/Scheduler (B5) → DanbooruWatcherQueueAdmission
```

Gegenüber den vorhandenen Tests neu: Bisher prüften Host-Tests den Scheduler mit
einer Zähl-Admission (`RecordingAdmission`) und einem „plugin-ähnlichen“ Worker,
Core-Tests die echte Queue mit `FakeDanbooruSearch`. Die B9-Gates schließen beide
Hälften zusammen und nutzen den **echten Plugin-Parser**. Die Prozessgrenze
(Worker) decken weiterhin `BooruRealWorkerTests`/`BooruWatcherRealWorkerTests` ab
(benötigen Release-Builds).

## Szenarioabdeckung (Plan §B9)

| # | Szenario | Gate(s) | Stand |
| --- | --- | --- | --- |
| 1 | Suche mit mehreren Provider-Tags, kein versteckter Ratingfilter | `S1_SearchPreviewAndExplicitSelection…` (exakter Wire-Request `tags=cat_ears solo&page=1&limit=20`; g/s/e kommen alle zurück) | belegt (Host/Core) |
| 2 | Provider-Rating/Quelltags getrennt von eigenen Tags | `S1_…` (Rating/Tags im Claim, keine `private-tags.json`, kein Journal-Eintrag), `S7_…` (offline erhalten) | belegt bis Speicher; Anzeige-Trennung offen (B6/B7) |
| 3 | Einzelpost-URL lädt genau ein Original oder meldet fehlende Berechtigung | `S3_OneShotPostLink…` (strikte URL, genau `/posts/4001.json`, ein CDN-Transfer, eingeschränkt → `Unavailable` ohne Transfer, 404 → `NotFound`, Wiederholung → `AlreadyLinked`) | belegt |
| 4 | Derselbe Post aus Suche, One-Shot und zwei Watchern nur einmal admitted | `S4_SamePostFromSearchOneShotAndTwoWatchers…` (1 CDN-Transfer, 4 Fundstellen, Watcher melden `AlreadyQueuedOrLinked`) | belegt |
| 5 | Watcher: Anlegen, Baseline, neue Posts, Pause, manueller Check, Cooldown, Restart, Entfernen | `S5_WatcherBaselineNewPosts…`, `S5_S9_Provider429…CooldownSurvivesARestart` | belegt |
| 6 | Queue-Pause/Cancel/Restart, beschädigte Bytes | `S6_PausedQueueAcrossRestart…`, `S6_UserCancelIsDurable…`, `S6_DamagedBytesNeverBecomeASuccess…`; `S6_AppShutdownDuringATransfer…` **rot → Befund F1** | belegt bis auf **F1** |
| 7 | Neustart ohne Netz: Medien/Metadaten/Watcher lesbar | `S7_OfflineRestartKeeps…` (echter `DanbooruWatcherHost.StartAsync` mit Loop, 0 Metadaten-Requests beim Start, verifizierte Offline-Reads, Offline-Suche = sichtbarer `http-network`-Fehler, manueller Check = Cooldown statt Fehler/Löschung, ausstehendes Original bleibt pending und wird online nachgeholt) | belegt auf Daten-/Hostebene; Galerie/Favoriten-UI offen (B7) |
| 8 | Migration eines Altbestands ohne Booru-Daten | `S8_ProfileWithoutBooruData…` (Start offline ohne einen Byte-Unterschied an bestehenden Dateien, keine neue Datei; erste Nutzung fügt nur `booru-catalog.json` und `media/danbooru/<id>/…` hinzu; Neustart offline lesbar) | synthetisch belegt; **kein echtes 1.0.2-Profil** |
| 9 | leer, eingeschränkt, gelöscht, 3xx, 401/403, 404/410, 429, 502/503, Timeout, zu groß | `S9_ProviderFailures…` (13 Fälle mit exakten stabilen Codes, Watcher bleibt, nichts admitted, ≤1 Host-Retry, 59 s Ruhe), `S9_EmptyResultIsABaseline…` (leer, gelöscht/gebannt), `S9_SlowMetadataBodyTimesOut…` (`http-timeout` → Cooldown) | belegt (Host/Plugin/Scheduler); UI-Zustände offen (B6) |
| 10 | Paketierung, Lokalisierung, Hilfe, keine Credentials/privaten Tags in Requests | `S10_ProviderRequestsCarryOnlyTheUserQuery…` + `B9World.AssertCleanWire()` in jedem Gate (nur GET, kein `Authorization`/`Cookie`, exakter UA, nur deklarierte Endpunkte, `limit=20`, max. 1 paralleler Request) | Wire-Teil belegt; Paketierung/Lokalisierung/Hilfe weiter über bestehende Tests (hier nicht erneut ausgeführt) |

Stabile Fehlercodes, die die Matrix jetzt festschreibt (Watcher-Checkebene):
3xx `http-redirect` · 401/403 `access-denied` · 404 `not-found` · 410 `page-limit` ·
HTML `not-json` · falsche Form/abgeschnitten `malformed` · >2 MiB `metadata-too-large` ·
502/503 `provider-unavailable` (nach genau einem Host-Retry) · 429 `booru-cooldown`
(Host-Cooldown, nicht der Plugin-Code `provider-throttled`) · langsamer Body `http-timeout`.

## Befund F1 — Muss vor 1.1.0: App-Shutdown während eines Transfers verwirft den Originalwunsch

**Beobachtung (reproduzierbar, `S6_AppShutdownDuringATransfer…` ohne Skip):** Läuft beim
geordneten Beenden (`DanbooruWatcherHost.StopAsync` → `DownloadQueue.StopAllAsync`) gerade
der Transfer eines Booru-Originals, steht danach im Katalog
`OriginalIntent = Cancelled`. Der nächste Start (`RecoverPendingAsync`) setzt ihn nicht
fort; der Nutzer sieht keinen Fehler, das Original fehlt still. Ein zum selben Zeitpunkt
nur *wartender* Job bleibt korrekt `Requested` und wird nachgeholt. Ein Watcher, der den
Post später erneut findet, liefert `KeptCancelled` und lädt ebenfalls nicht.

**Ursache:**

- `libmeowchan/Downloader/DownloadQueue.cs:1350–1369` behandelt Thread-Cancel **und** Stop
  als „real cancellation“ und ruft für externe Owner `OnMediaCancelled` auf.
- `libmeowchan/Booru/BooruOriginalQueue.cs` (`PostOwner.OnMediaCancelled`) ruft daraufhin
  `MarkCancelled` → persistentes `BooruOriginalIntent.Cancelled`.
- Der `IExternalMediaWorkOwner`-Vertrag (`ExternalMediaWorkOwner.cs:31`) kennt nur ein
  Signal für Nutzerabbruch und Shutdown.

**Widerspricht:** Plan §3.3 („vorhandene … Queuearbeiten bleiben nach dem normalen
Queuevertrag erhalten“), B4-Abnahme („Abbruch … nicht zu falschem Erfolg; nach Neustart“),
Doku von `DanbooruWatcherHost` („queued downloads stay with the queue's own pause/restart
contract“) und B9 Szenario 6. Der vorhandene Test
`BooruOriginalQueueTests.StoppingTheQueueCompletesTheExternalOwnerInsteadOfHanging` prüft
nur, dass die Completion nicht hängt, nicht den dauerhaften Intent nach Neustart.

**Fixrichtungen (Entscheidung beim B4-Owner/Root, nicht umgesetzt):**

1. Minimal: `PostOwner.OnMediaCancelled` schreibt **kein** `Cancelled` mehr; nur
   `BooruOriginalQueue.CancelAsync` (expliziter Nutzerabbruch) tut das — das macht es
   heute bereits explizit. Zu prüfen: der Pfad `RemovePendingJobsLocked` (Cancel eines
   noch nicht gestarteten Jobs) läuft ebenfalls über `CancelAsync`.
2. Sauberer: Grund im Vertrag unterscheiden (z. B. `OnMediaCancelled(item, reason)` mit
   `UserCancelled`/`QueueStopped`) oder die Queue während `StopAllAsync` als „stopping“
   abfragbar machen.

**Reaktivierung:** `[Fact(Skip = …)]` am Gate entfernen; das Gate prüft laufenden und
wartenden Job und deren einmaligen Transfer nach dem Neustart.

## Lücken, für die keine API existiert (keine Tests geschrieben)

- **Kompositionsroot fehlt (G1):** Bei `ab9f99e5` konstruiert der Desktop weder
  `BooruSearchService` noch `BooruOriginalQueue`/`DanbooruWatcherHost`
  (`Desktop/PluginSession.cs:205` überspringt PostSearch-Quellen nur; `PluginHostHttp`
  routet Booru-Metadaten durch `BooruMetadataHttp`). Die Gates verwenden die in
  `DanbooruWatcherHost` dokumentierte Startreihenfolge. Desktop-Smokes für Suche →
  Vorschau → Queue sind erst nach der Root-/B6-Verdrahtung möglich.
- **B6/B7 (UI, MediaPage/Galerie/Favoriten):** keine Booru-spezifischen Desktop-Typen
  vorhanden. Szenario 2 (Anzeige-Trennung) und 7 (Galerie/Favoriten offline) sind hier
  nur auf Daten-/MediaStore-Ebene belegt.
- **Echtes 1.0.2-Profil (Szenario 8):** `Released102RestPackageUpgradeTests` ist
  env-gated, laut `tests/fixtures/upgrade-v1.0.2/README.md` ohne erfolgreiche Generierung
  und nicht booru-bewusst. Das B9-Gate S8 nutzt ein mit den aktuellen Nicht-Booru-Stores
  erzeugtes Profil plus Fremddateien — additiv belegt, aber kein Release-Profilbeweis.
- **Vorschau-Bytes:** Das Laden von Preview-/Sample-Bildern ist UI-Sache (B6); es gibt
  keinen Core-/Host-Pfad, den man testen könnte. S1 belegt, dass Suche/Auswahl selbst
  kein Preview/Sample lädt.

## Beobachtungen ohne Handlungsbedarf

- Offline-Suche: zwei Verbindungsversuche (Erstversuch + ein Host-Retry), dann
  `http-network`. Offline-Start ohne fällige Watcher: null Metadatenversuche.
- 429 ohne `Retry-After` → Host-Mindestcooldown; der Watcher-Code ist `booru-cooldown`.
  Für B6-Texte relevant: der Plugin-Code `provider-throttled` erscheint auf Watcherebene
  nicht, weil der Host 429 vor dem Plugin abfängt.
- Nach einem 429 übersteht die Sperre einen Neustart nur über den persistierten
  Watcher-Retryzeitpunkt; die In-Memory-Cooldown des neuen Transports ist leer. Suche
  und One-Shot nach einem Neustart innerhalb der Retryzeit würden also wieder einen
  Request senden (aus `BooruSearchService`/`BooruHostHttp` abgeleitet, nicht als Gate
  geprüft) — vom Plan gedeckt (nur „keine aggressiven Folgeanfragen“), aber als
  Option für B6 notierenswert.
- `docs/development/booru-test-matrix.md` (B8) listet die neuen Gates noch nicht; Root
  sollte die Zeilen 3–9 beim Integrieren um die `BooruB9AcceptanceTests`-Namen ergänzen.

## Ausgeführte Kommandos

```sh
dotnet build plugins/Official/Danbooru/MeowChan.Plugin.Danbooru.csproj -c Debug
dotnet build tests/MeowChan.PluginRuntime.Tests/MeowChan.PluginRuntime.Tests.csproj -c Debug
dotnet build tests/MeowChan.Tests/MeowChan.Tests.csproj -c Debug
dotnet test tests/MeowChan.PluginRuntime.Tests -c Debug --no-build --filter "FullyQualifiedName~BooruB9AcceptanceTests"
#   5 Läufe hintereinander: je Passed 26, Skipped 1 (F1), Failed 0, 2–3 s
dotnet test tests/MeowChan.PluginRuntime.Tests -c Debug --no-build --filter "FullyQualifiedName~Booru&FullyQualifiedName!~RealWorker"
#   Passed 86, Skipped 1, Failed 0
dotnet test tests/MeowChan.Tests -c Debug --no-build --filter "FullyQualifiedName~MeowChan.Tests.Booru|FullyQualifiedName~Danbooru"
#   Passed 330, Failed 0
```

Nicht ausgeführt: `BooruRealWorkerTests`/`BooruWatcherRealWorkerTests` (Release-Builds
nötig, unverändert), Desktop-/Paketierungs-/Lokalisierungstests, Gesamtsuite.

## Fortschritt

- Synthetische B9-Gates, soweit APIs reichen: **~90 %** (offen nur die F1-Reaktivierung).
- B9-Gesamtabnahme laut Plan: **~55 %** — Host/Core-Kette für 1, 3–6, 9 belegt; 2, 7, 8, 10
  teilweise; Desktop-Smokes, Kompositionsroot, B6/B7 und echtes 1.0.2-Profil offen.
- ETA: F1-Fix + Gate reaktivieren ≈ 0,5–1 h. Desktop-Smokes erst nach Root-/B6-/B7-
  Verdrahtung (abhängig von deren Stand, eher Tage als Stunden). 1.0.2-Profil: blockiert,
  solange der Fixture-Generator nicht erfolgreich lief.
