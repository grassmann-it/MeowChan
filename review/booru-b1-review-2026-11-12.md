# Booru B1 Host-HTTP – unabhängige Abnahme (12.11.2026)

Basis: Branch `claude/booru-b1-review-20261112`, HEAD `3c675786` (B1: `413ffc43`, `1569768a`).
Regeln: Brief `booru-b1c-brief.md`, Arbeitsplan §2.2, Sparring-Index (Danbooru-Durchstich akzeptiert).
Keine Live-Requests; nur Fakes/Loopback.

## Ergebnis je Prüffeld
| Feld | Befund |
|---|---|
| Booru-UA überschreibt Transport-UA, ohne `+build`, optionaler User `(user #id)` | PASS (BooruUserAgent, `RequestUserAgentOverridesTransportDefault`); Chan-Pfad leitet unverändert an Inner |
| Allowlist `/posts.json`, `/posts/<id>.json`, GET-only | **FAIL im HEAD** (nur Host/Methode geprüft) → behoben |
| Keine Redirects, keine Bodybytes vor 3xx | **FAIL im HEAD** (Body wurde vor Statusentscheid gelesen) → behoben; 429/erster 502/503 lesen ebenfalls keinen Body mehr |
| Einzel-Sendung, ≥1 s Start-Abstand, 20 Sends, 120 s Deadline, Cancel in Waits | PASS (SendGate, NextStart, BooruJob, DelayUntilAsync) |
| 502/503 ein Retry ≥2 s; Transportfehler ein Retry; 401/403/404/410 kein Retry | PASS |
| 429 Cooldown ≥60 s / Retry-After, kein Netzzugriff im Cooldown | PASS (auch Façade-Vorabcheck) |
| 2 MiB wire / 4 MiB decoded, gzip/deflate/br, ohne Content-Length; 15 s Deadline | PASS |
| Query-/Seiten-/Limit-Admission | Façade PASS; **HTTP-Ebene fehlte** → behoben (nur `tags` via DanbooruTagQuery, `page` 1–5, `limit` 1–20, keine Duplikate/Fremdparameter, https, Default-Port) |
| Job-Lifetime/PluginOperationContext | PASS (`booru-job:<guid>`, geschlossene Jobs → `booru-job`) |
| Chan-Pfad / Desktop / PluginSession | unverändert (Diff nur in BooruHostHttp.cs + Test) |

## Korrektur
Nur `MeowChan.PluginHost/Http/BooruHostHttp.cs` und `tests/MeowChan.PluginRuntime.Tests/BooruHostHttpTests.cs`:
`IsAllowedEndpoint` (+ Query-Admission), Statusentscheid vor `ReadBodyAsync`. Neue Tests: 17 Negativ-URLs,
3 positive URLs, 3xx/429 ohne Body-Zugriff.

## Differenz zu Root-WIP (nicht übernommen)
Root hat untracked `MeowChan.PluginHost/Http/BooruMetadataHttp.cs` (368 Z.) als abweichende Parallelimplementierung
(eigene Fehlercodes `booru-redirect`, `metadata-wire-too-large`) sowie dirty `PluginHostHttp.cs`/`PluginOriginalMediaHttp.cs`.
Deren Allowlist-Logik war Referenz, der Code wurde neu geschrieben; Dateien nicht übernommen. Beim Zusammenführen
muss entschieden werden, welche der beiden Booru-HTTP-Klassen gilt (Fehlercodes unterscheiden sich).

## Rest-Risiken
- Schließen eines Jobs bricht laufende Waits nicht aktiv ab (nur Cancel-Token des Aufrufers/Deadline).
- 502/503 mit `Retry-After` über Jobdeadline liefert `booru-budget` statt der 503-Antwort.
- Allowlist-Pfade sind für `danbooru` fest verdrahtet; weitere PostSearch-Provider bekommen nur Scheme/Port-Prüfung.
- Live-Danbooru nicht geprüft.
