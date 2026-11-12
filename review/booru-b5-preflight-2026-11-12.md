# Booru B5 Vorprüfung — Watcher-Ausführung / Scheduler

**Stand:** 12.11.2026 · **Branch:** `claude/booru-b5-preflight-20261112` (Basis `f3bd2434`)
**Autor:** isolierter Sonnet-Worker · **Status:** Vorprüfung + ein kleiner, vollständig disjunkter Patch
(reine Fälligkeitsauswahl). Kein Executor, kein Host-Wiring, keine UI/HTTP/Catalog/Queue-Änderung.
**Gelesen:** `AGENTS.md`, `.architecture-sparring/config.json` + `index.md`, Arbeitsplan
`1.1.0-booru-arbeitsplan-2026-11-12.md` (alle drei liegen nur im Root-Checkout, read-only gelesen),
B1-/B2-Reviews, `DanbooruWatcherDomain.cs`, `BooruCatalog.cs`, `BooruOriginalDownload.cs`,
`BooruHostHttp.cs`, `BooruHostServices.cs`, `BooruSearchService.cs`, `BooruJob.cs`, SDK-`BooruContracts.cs`,
`plugins/Official/Danbooru/Danbooru.cs`, sowie read-only der **uncommitted** B4-WIP
(`booru-b4-downloadqueue`: `BooruOriginalQueue.cs`) und der MUST-1-WIP (`booru-b2-must1`: Diff an
`DanbooruWatcherDomain.cs`). Kein Netzwerk.

## 1. Abhängigkeitsstand (wovon B5 abhängt)

| Baustein | Stand | Folge für B5 |
|---|---|---|
| B1 Host-HTTP (Pacing, Cooldown, Budget, Allowlist, 3xx) | in `f3bd2434` integriert (`8a6491d5`) | nutzbar; Zusatzfeld „Cursor“ im SDK fehlt (G3) |
| B2 Domain | in HEAD, **MUST-1 offen** (in `booru-b2-must1` als uncommitted Diff in Arbeit, Fix = `CheckStartHighWaterMark` als Fence), **MUST-2 offen** (kein Worker-Commit) | B5 darf erst nach beiden fertig verdrahten |
| B3 Katalog | in HEAD; Record verlustbehaftet (MUST-2); kein Domain↔Record-Mapper | Mapper ist B5-Voraussetzung (G2) |
| B4 Queue | **nicht committed**; `BooruOriginalQueue.AdmitAsync(claim, kind, watcherId, query)` (Upsert + Finding + Enqueue, idempotent), `RecoverPendingAsync`, `CancelAsync` liegen als WIP vor | Übergabevertrag ist erkennbar; endgültige API abwarten |

## 2. Geprüfte Vertragsstellen

| Prüffeld | Befund | Beleg |
|---|---|---|
| Fällige aktive Watcher auswählen | **Lücke → jetzt abgedeckt (reine Logik)**: Domain kennt weder Intervall noch „fällig“. `DanbooruWatcherSchedule` liefert `NextCheckAtUtc` / `SelectNext` | neu `libmeowchan/Booru/DanbooruWatcherSchedule.cs`, 14 Tests |
| Genau ein Check je Watcher | Domain: `BeginCheck` lehnt `Checking` ab (`check_already_running`). Reicht nicht als Scheduler-Garantie, da `Checking` persistierbar ist, aber `ResumeState` nicht (G2). Executor braucht zusätzlich In-Memory-Single-Flight je `WatcherId` | `DanbooruWatcherDomain.cs:151`, Test `SelectedWatcherCannot…` |
| Max. 1 aktiver Metadata-Send, ≥ 1 s Start-Abstand | **Erfüllt im Host**: `SendGate` (SemaphoreSlim 1,1) je Quelle, `NextStart = now + MinimumStartSpacing` nach jedem Start inkl. Retries; Jobs teilen den Zustand pro `SourceId`, nicht pro Job. Selektor wählt zusätzlich höchstens einen Check gleichzeitig | `BooruHostHttp.cs:75-94,159,202,218` |
| Bounded pages / Jobbudget | Domain: Page ≤ 20 Posts, Budget ≤ 5 Seiten/Check, danach `BudgetLimited` + `NextPage` weiter; Host: je Job ≤ 20 Sends/120 s; Façade: Page 1–5, Limit 20. **Aber**: jede Seite ist ein eigener Host-Job; ein Check hat keine eigene Gesamtfrist/-sendezahl (nur 5 Seiten × 20 Sends). Akzeptabel, im Executor als Check-Deadline (z. B. 5 × 120 s) festschreiben | `BooruSearchService.cs`, `BooruJob.cs` |
| Persistenter Restart-Fortschritt | **Lücke (MUST-2)**: Record hält State, HWM, NextPage, Baseline, KnownPostIds; nicht `CursorBeforePostId`, `CheckStartHighWaterMark`, `ResumeState`, `PageSize`, `PageBudget`, `BudgetLimited`. Mid-Traversal-Restart ⇒ falscher Abschluss bzw. Pause geht verloren | B2-Review MUST-2; `BooruCatalog.cs:135-148` |
| 429 / Retry-After | Host: 429 setzt providerweiten Cooldown ≥ 60 s bzw. gültiges `Retry-After`, **ohne Retry**, Folge-Sends liefern `HostHttpDeferred("booru-cooldown")` ohne Netz; Façade prüft vorab. Cooldown ist **nur im Speicher**. Plugin klassifiziert 429 als `provider-throttled` mit **eigenem** `RetryAt` (`DefaultThrottleSeconds`) – kann kürzer sein als der Host-Cooldown | `BooruHostHttp.cs:141-145`, `BooruSearchService.cs`, `Danbooru.cs:155` |
| Pause / manueller Check | Domain-Pause und manuelles `BeginCheck` existieren. **Neue Lücke G6**: manueller Check eines pausierten Watchers, der in `Cooldown`/`Error` endet, verliert die Pause (Rückkehrzustand wird aus `State` statt `ResumeState` abgeleitet; Probe bestätigt: danach `Active`) | Test (skipped) `ManualCheckOfAPausedWatcher…` |
| Shutdown | Façade rethrowt `OperationCanceledException` des Aufrufers. Domain hat `CancelCheck` (kein Fortschrittsverlust, kein Fehler). Executor muss Cancel → `CancelCheck` + Persist statt `ApplyError` abbilden (G8) | `BooruSearchService.cs`, `DanbooruWatcherDomain.cs:306` |
| Non-destructive Remove | **Erfüllt**: Domain `Remove` ⇒ `Removed`, HWM/Known bleiben; Store `MarkWatcherRemoved` ändert nur den Watcher-Record (Tombstone), Posts/Claims/Findings/Assets bleiben. Offen: kein Compare-and-Set im Store – ein späteres `UpsertWatcher` mit altem Objekt könnte `Removed` überschreiben (G9) | `BooruCatalog.cs:365-392`, Test `RemovedWatcherKeeps…` |
| Dedup / „genau einmal admitted“ | B4 vorgesehen: Finding-ID idempotent, Queue-Identität `(danbooru, PostId, original)`; mehrere Watcher ⇒ ≤ 1 aktiver Download. Kontrolle erst mit finaler B4-API | B4-WIP `BooruOriginalQueue.cs` |
| Queryreinheit (`g,s`, Privat-Tags) | OK; Watcher-Query ist `DanbooruTagQuery.ProviderQuery` | B2-Review |

## 3. Offene Lücken (priorisiert)

**MUSS vor dem Executor**

- **G1 (= B2 MUST-1)** Seiten 2..5 eines Catch-ups emittieren nichts. Fix liegt uncommitted in `booru-b2-must1`; B5 wartet auf dessen Commit.
- **G2 (= B2 MUST-2)** Record ↔ Domain nicht verlustfrei; es gibt keinen Mapper. Additiv nullable Felder ergänzen (Cursor, CheckStart-HWM, ResumeState, Budget/PageSize, BudgetLimited), Konsistenzregeln (`NextPage>0 ⇒ Cursor & CheckStart`) und ein getesteter `ToRecord`/`FromRecord`-Mapper inkl. `RecoverAfterRestart`. Vor Auslieferung von Schema 1, danach nur noch mit Migration.
- **G3 Seitenverschiebung bei Zahlenpaging.** Adapter ruft `posts.json?page=N&limit=L`; SDK `BooruSearchRequest` hat kein Cursorfeld. Ein neuer Post zwischen zwei Seitenabrufen verschiebt eine ID auf die Folgeseite ⇒ Domain meldet `cursor_did_not_advance` ⇒ `Error`. Fail-closed, aber flatterhaft bei aktiven Tags. Entscheidung nötig: (a) SDK-Feld `before-id`/`b<id>`-Paging (B1/SDK-Vertrag) oder (b) Domain toleriert genau eine Überlappungs-ID gleich dem Cursor. Empfehlung (a); wegen „B1-Adapteränderungen noch nicht integriert“ als Voraussetzung markieren.
- **G4 Hand-off-Reihenfolge.** Pro Seite: `Map` (BooruPost → Claim) → `AdmitAsync` je neuer ID → **danach** Watcher-Record persistieren. Absturz dazwischen ⇒ gleiche IDs werden erneut emittiert, Admission/Finding sind idempotent (at-least-once, nie Verlust). Niemals zuerst den Watcher vorrücken. Fehlerklassen der Admission (`Unavailable`, `MissingIntegrityClaims`, `NotAdmitted`) dürfen den Watcher nicht in `Error` setzen; sie sind pro Post sichtbar, Wiederaufnahme über `RecoverPendingAsync`.
- **G5 Provider-Cooldown nicht restart-fest.** Host-Cooldown liegt im Speicher. Nach Neustart kann ein anderer Watcher innerhalb der Retry-After-Zeit sofort anfragen. Beim Start Provider-Cooldown = max(`CooldownUntilUtc` aller Watcher im Zustand `Cooldown`) setzen (Selektor-Parameter `providerCooldownUntilUtc`), und `max(plugin.RetryAt, host.TryGetCooldown)` für den Watcher verwenden.
- **G6 Pause geht bei Cooldown/Error verloren** (siehe oben). Domainfix: `ApplyDeferred`/`ApplyError`/`BeginCheck` müssen `ResumeState` respektieren; Test ist als `Skip` vorbereitet.

**SOLL**

- **G7 Executor-Architektur.** Mapper `BooruClaimMapper` ist `internal` in `MeowChan.PluginHost`; Domain/Store/B4 liegen in `libmeowchan`. Der Executor gehört daher nach `MeowChan.PluginHost/Sources/Booru/` (neue Datei), der Selektor bleibt in `libmeowchan/Booru/`. Eine einzige serielle Schleife (Actor) mutiert alle Watcher (löst G9 und die Single-Flight-Anforderung).
- **G8 Shutdown/Pause/Remove während eines Checks.** Pro Check ein verlinktes CTS; Pause/Remove/Shutdown cancelt es. Antwort: `CancelCheck` (Shutdown/Cancel), bei Pause/Remove Ergebnis verwerfen (Domain lehnt `ApplyPage` mit `check_not_running` ab; **diese Ablehnung nicht persistieren**, nur `Fail`-Übergänge mit `!Accepted` und geändertem Watcher persistieren). Persistieren in `finally` mit nicht abgebrochenem Token, sonst Fortschrittsverlust.
- **G9 Kein CAS im Store.** Serielle Mutation (G7) oder Versionsfeld; ein Tombstone `Removed` nie überschreiben.
- **G10 `KnownPostIds` unbegrenzt vs. Store-Limit 100 000.** Pruning-Regel (z. B. Ids unterhalb `HWM − Fenster` verwerfen, solange HWM + Cursor als Fence dienen) oder bewusste Fehlerbehandlung; heute `ArgumentException` beim Persistieren.
- **G11 `BeginCheck` akzeptiert `Error` ohne `manual`.** Der Selektor wählt `Error` nie; der Executor darf `BeginCheck` nur für ausgewählte oder manuell angeforderte Watcher aufrufen. Empfehlung: Domain lehnt `Error` ohne `manual` ab.
- **G12 Scheduler-Default / Setting.** Intervall ist kein persistierter Wert und keine Nutzerentscheidung; Selektor nimmt ihn als Parameter (Minimum 1 s = Providerabstand). Empfehlung: Default ≥ 30 min, Settings später ohne Identitätsänderung.
- **G13 5xx-Semantik.** 502/503 landet als `provider-unavailable` + `RetryAt` (Deferred) ⇒ `Cooldown`; andere Fehler ⇒ `Error`, der manuelle Resume braucht. Erwünscht? Für 1.1.0 vertretbar, in der Hilfe benennen (B8).
- **G14 Startreihenfolge.** `RecoverAfterRestart` für alle geladenen Watcher **vor** der ersten Auswahl (sonst blockiert ein stale `Checking` im Selektor alle anderen – absichtlich fail-safe); dann `RecoverPendingAsync` (B4); dann Scheduler starten, erst nach Profil-Lease/Startup-Lifecycle.

## 4. Was dieser Patch liefert (und was nicht)

`libmeowchan/Booru/DanbooruWatcherSchedule.cs` — rein, ohne Zeit/IO/Thread/Netz:

- `NextCheckAtUtc(watcher, interval, now)`: Active ⇒ `LastChecked + interval` (nie geprüft ⇒ `CreatedAt`, d. h. sofort für die Baseline); budget-begrenzter Teilfortschritt (`NextPage>0`) ⇒ sofort fortsetzen; Cooldown ⇒ `CooldownUntilUtc`, nicht das Intervall; Paused/Error/Removed/Checking ⇒ `null`; `LastChecked` in der Zukunft ⇒ sofort fällig (kein Stall).
- `SelectNext(watchers, now, interval, providerCooldownUntilUtc?, inFlightWatcherIds?)`: höchstens **ein** Watcher; `null` bei aktivem Provider-Cooldown, laufendem Check (Set oder `Checking`-Zustand) oder nichts fällig; Reihenfolge nach Fälligkeit, dann `CreatedAt`, dann `WatcherId` (Fairness: ein soeben geprüfter Watcher reiht sich hinten ein, ein Dauer-Teilfortschritt verhungert keine anderen).
- Intervall < 1 s ⇒ `ArgumentOutOfRangeException`.

Nicht enthalten: Executor, Timer, Persistenz, Mapper, Host-Lifecycle, Intervall-Setting, UI. Keine der Dateien anderer Worker berührt (nur `DanbooruWatcher` öffentlich gelesen; bei MUST-1/2-Änderungen bleibt die API stabil, bei neuen Pflichtfeldern ändert sich nur die Testhilfe).

## 5. Testplan für die eigentliche B5-Umsetzung

Alle ohne Netz: `FakeTimeProvider`, Fake-`IPluginWorkerClient`, Loopback-Stub für Host-HTTP, temporäres Profilverzeichnis.

1. **Mapper (nach G2):** Round-Trip Domain→Record→Domain für jeden Zustand inkl. Budget-Pause und Paused-während-Checking; Konsistenzverletzung ⇒ fail-closed; Alt-Schema ohne neue Felder lädt unverändert.
2. **Executor mit Fake-Worker:** Baseline-Check emittiert nichts; zweiter Check mit 1 neuem Post ⇒ genau ein `AdmitAsync`; 7 neue Posts über 3 Seiten ⇒ alle 7 (Regression MUST-1); mehr Treffer als 5×20 ⇒ `BudgetLimited`, Folgelauf setzt bei `NextPage` fort, Jobs insgesamt ≤ 5 Seiten je Lauf.
3. **Pacing/Parallelität (Loopback-Stub mit Zeitstempeln):** zwei fällige Watcher ⇒ nie zwei gleichzeitige Requests, Startabstand ≥ 1 s, höchstens ein Check gleichzeitig, kein Watcher doppelt.
4. **429:** erster Request 429 ⇒ Watcher `Cooldown` mit `max(Retry-After,60 s)`, **kein** weiterer Request an irgendeinen Watcher bis Ablauf (Zähler am Stub); Neustart mitten im Cooldown ⇒ Provider-Cooldown wird aus Records übernommen (G5).
5. **Fehlerklassen:** 3xx, 401/403/404/410, zu großer Body ⇒ `Error`, Watcher bleibt erhalten, kein Auto-Retry; 502/503 ⇒ `Cooldown`.
6. **Pause/Manuell:** Pause während Check cancelt Request, Fortschritt bleibt; manueller Check auf Paused kehrt zu Paused zurück (auch nach Cooldown/Error, G6); manueller Check im Cooldown ⇒ kein Netzzugriff.
7. **Shutdown:** Cancel mitten in Seite 2 ⇒ Seite 1 persistiert, `CancelCheck`, nach Neustart Fortsetzung ohne Doppel-Admission und ohne Verlust.
8. **Absturz zwischen Admission und Persist:** Wiederholung emittiert dieselben IDs; Queue/Finding idempotent (≤ 1 aktiver Eintrag, 1 Finding je Watcher).
9. **Remove:** während Check und im Ruhezustand; Posts/Assets/Quelltags/private Tags unverändert; Tombstone wird nie überschrieben; zugehörige Queue-Jobs laufen nach Queue-Vertrag weiter oder werden nur auf ausdrücklichen Wunsch abgebrochen.
10. **Mehrere Watcher, derselbe Post:** zwei Findings, ein Download.
11. **Pruning/Grenzen (G10):** 100 001 bekannte IDs ⇒ definiertes Verhalten, nie stilles Persistfehlschlagen.

## 6. Integrationsreihenfolge und Schreibgrenzen

1. **Jetzt:** diesen Commit aufnehmen (nur neue Datei + neue Tests + dieser Bericht, kollisionsfrei).
2. B2-Worker: MUST-1 committen (Test un-skippen), danach MUST-2 + Mapper (G2) und G6, G11 in `libmeowchan/Booru/DanbooruWatcherDomain.cs` und `libmeowchan/Archive/BooruCatalog.cs` (+ deren Tests). Un-skip `ManualCheckOfAPausedWatcher…` im selben Commit.
3. SDK/B1: Cursor/`before-id`-Paging (G3) in `MeowChan.PluginSdk/BooruContracts.cs`, `plugins/Official/Danbooru/Danbooru.cs`, `MeowChan.PluginHost/Sources/Booru/BooruSearchService.cs` — Entscheidung durch Root.
4. B4 committen (`BooruOriginalQueue`), API einfrieren.
5. **B5-Executor** (erst nach 2–4): neue Dateien `MeowChan.PluginHost/Sources/Booru/BooruWatcherScheduler.cs` (+ Hosted-Lifecycle-Anbindung) und `tests/MeowChan.PluginRuntime.Tests/BooruWatcherSchedulerTests.cs` bzw. `tests/MeowChan.Tests/Booru/…`; Intervall-Setting nur mit Rootentscheid.
6. B6 (UI) hängt nur an Schedule-/Executor-Lesemodell (`NextCheckAtUtc`, Status), nicht am Persistenzformat.

**Schreibgrenzen B5-Executor:** erlaubt `MeowChan.PluginHost/Sources/Booru/` (neue Dateien), `libmeowchan/Booru/DanbooruWatcherSchedule.cs`, Scheduler-/Recovery-Tests. **Nicht** ohne Absprache: `BooruHostHttp.cs`/`BooruHostServices.cs` (B1), `BooruCatalog.cs`/`DanbooruWatcherDomain.cs` (B2/B3-Worker), `DownloadQueue*`/`BooruOriginalQueue.cs` (B4), UI/Desktop/Lokalisierung (B6/B8).

## 7. Tests und Befehle

```
dotnet test tests/MeowChan.Tests --filter "FullyQualifiedName~MeowChan.Tests.Booru|FullyQualifiedName~DanbooruQueryAdversarial"
  → Passed 229, Failed 0, Skipped 3, Total 232
```
Neu: `tests/MeowChan.Tests/Booru/DanbooruWatcherScheduleTests.cs` (14 grüne Tests + 1 `Skip` für G6, per
temporärem Entfernen von `Skip` als fehlschlagend verifiziert). Skipped insgesamt 3 = 2 B2-MUST-Tests + 1 G6.
PluginRuntime-/Sdk-Tests nicht neu gelaufen (keine Berührung); bekannte Umgebungsvorbedingung der
`BooruRealWorkerTests` (Release-Builds) unverändert.

## 8. Risiken

- Der Selektor nimmt „ein Check gleichzeitig“ an (konservativ). Soll später Parallelität über Metadaten-Sends hinaus nötig werden, bleibt das Host-`SendGate` die eigentliche Garantie.
- Das 120-s-Jobbudget gilt je Seite, nicht je Check (siehe Abschnitt 2).
- Teilfortschritt ohne Wartezeit kann bei Dauer-Überlauf einen Tag-Watcher rund um die Uhr paginieren lassen; Fairness-Reihenfolge verhindert Verhungern anderer, nicht die Providerlast. Falls unerwünscht: Mindestabstand für Fortsetzungen (z. B. 30 s) als Parameter nachziehen.
