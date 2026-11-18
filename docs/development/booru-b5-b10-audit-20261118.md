# Package 5A — Booru B5–B10 Audit / Verifikation

**Stand:** 2026-11-18  
**Audit-Worktree:** `/home/tomka/.worktrees/ochan-ledger/worker-5a`  
**Audit-Branch:** `worker/5a-booru-b5-b10-audit-20261118`  
**Baseline:** `origin/goal/gesamtprogramm-profile-a` @ `866ad8ea24a09d57e9e8e5e3452d4de6beaa100a`  
**Geprüfter Implementierungsstand:** `feature/booru-b5-b10-20261117` @ `1d48734c3b13103058b86e10849f4e286f4a091e`

## Scope und Grenzen

- Geprüft wurden ausschließlich Booru B5–B10 sowie die zugehörigen normalen Desktop-, Plugin-Host- und Official-Plugin-Tests.
- `tests/MeowChan.DesktopSmoke` wurde **nicht geändert und nicht ausgeführt**.
- CI-, Build- und Packaging-Infrastruktur wurde **nicht geändert**.
- Es wurde kein Merge durchgeführt und kein Kandidaten-Featurebranch umgeschrieben.
- Der geprüfte Featurebranch ist nicht direkt auf die aktuelle Zielbasis aufsetzbar: `git rev-list --left-right --count origin/goal/gesamtprogramm-profile-a...feature/booru-b5-b10-20261117` ergibt `332 21`. Eine direkte PR-/Cherry-Pick-Empfehlung wäre deshalb ohne Rebase/Portierung nicht belastbar.

## Normaltest-Evidenz

Die folgenden Läufe wurden im separaten, sauberen Feature-Worktree ausgeführt; alle Filter waren normale Tests, kein DesktopSmoke:

| Bereich | Ergebnis |
|---|---:|
| `MeowChan.DesktopTests`, gezielte B5–B9-Verträge/Modelle/Fixtures/Workbench | **187 passed, 0 failed, 0 skipped** |
| `MeowChan.OfficialPlugins.Tests`, `DanbooruSourceTests` | **185 passed, 0 failed, 0 skipped** |
| `MeowChan.PluginRuntime.Tests`, isolierte Remote-HTTP-Verträge | **22 passed** |
| `MeowChan.PluginRuntime.Tests`, isolierte Preview-/Host-/Search-Verträge | **98 passed** |
| `MeowChan.PluginRuntime.Tests`, isolierte CDN-Fixture-Fälle ohne den gemeinsamen-Fence-Fall | **5 passed** |

Der zusammengefasste PluginRuntime-Lauf war **127 passed, 1 failed, 0 skipped (128 total)**. Der eine Fehler ist reproduzierbar als Test-Isolationsfehler, nicht als Produktfehler:

- `tests/MeowChan.PluginRuntime.Tests/BooruCdnFixtureTests.cs:50-83` iteriert mehrere CDN-Fehlerfälle über **eine** `BooruPreviewHttp`-Instanz.
- Der Fall `throttled-seconds` setzt korrekt den gemeinsamen Media-Fence.
- Der direkt folgende Fall `throttled-date` wird deshalb korrekt ohne Transport-Request als `Fenced` beantwortet; die Testzeile 82 erwartet trotzdem genau einen Request.
- Das Produktverhalten ist in `MeowChan.PluginHost/Http/BooruRemoteImageHttp.cs:223-224` und `:335-338` dokumentiert/implementiert: 429/503 heben den Fence; während des Fence wird kein Request gesendet.

**Konsequenz:** Der Kandidatenstand darf erst dann als vollständig grün bezeichnet werden, wenn der CDN-Fixture-Test pro Fehlerfall einen frischen Preview-Client verwendet oder den Fence zwischen Fällen zurücksetzt. Diese Audit-Branch ändert den fremden Featurebranch dafür nicht.

## B5–B10-Befund

| Paket | Befund | Beleg / Restarbeit |
|---|---|---|
| **B5 Browse-/Settings-Persistenz** | **Teilweise verifiziert; echte offene Lücke** | `BooruBrowsePersistenceTests` deckt `ShowBooruTools`, `BooruCatalogLimitMiB` und `BooruRetryAfterCooldownHours` inklusive Export/Restart ab; die Request-Replay-Prüfung ist grün. Die Remote-Image-Limits sind dagegen nicht in `Desktop/Config.cs` vorhanden und werden in `Desktop/Booru/BooruSessionComposition.cs:157-163` weiterhin mit `limits: null` erzeugt. `BooruRemoteImageHttp.cs:39-58` sagt selbst, dass die Settings mit B5 kommen sollen. Offen sind daher Persistenz/Validierung/Settings-Anbindung für Preview-Cache, Sample-Cache, maximale Sample-Größe und Parallelität sowie die Laufzeit-Anwendung über `ApplyLimits`. |
| **B6 Viewer-/Download-Bindung** | **Verifiziert auf Model-/Vertragsebene** | `BooruViewerContractTests` und der gezielte Desktop-Lauf sind grün. Sample/Download bleiben an das geöffnete Ergebnis gebunden; kein Produktpatch aus dem Audit abgeleitet. Native Smoke-Akzeptanz bleibt wegen Scope ausstehend. |
| **B7 Recovery** | **Verifiziert auf Model-/Vertragsebene** | `BooruRecoveryContractTests` und `BooruRemoteStateTests` sind grün. Abbruch/Fehler führen zu expliziten Recovery-Zuständen/Aktionen; kein unbelegter Produktfehler gefunden. |
| **B8 Paging / Lower Bounds** | **Verifiziert auf Model-/Vertragsebene** | `BooruPagingContractTests` ist grün. Page-Jumps sind begrenzt; Lower Bounds werden nicht als exakte Gesamtzahl ausgegeben; Recovery-/Eligibility-Fälle sind abgedeckt. |
| **B9 Auswahl / Batch-Recovery** | **Verifiziert auf Model-/Vertragsebene** | `BooruSelectionContractTests` ist grün. Auswahl ist auf 10 begrenzt, Batch-Reihenfolge/serielle Abarbeitung und explizite Recovery sind abgedeckt. |
| **B10 Workbench-Smoke-Wiring** | **Nicht Bestandteil dieses Audits** | Der Kandidatenbranch enthält Änderungen an `tests/MeowChan.DesktopSmoke`; diese wurden bewusst nicht übernommen, nicht geändert und nicht ausgeführt. Deshalb keine B10-Smoke-Freigabe aus diesem Audit. |

## Konkrete offene Punkte

### 1. B5: Remote-Image-Settings sind nicht verdrahtet

Die Host-Komponente stellt bereits `BooruRemoteImageLimits` und `ApplyLimits` bereit, aber die Produktions-Komposition verwendet dauerhaft die Defaults:

```text
Desktop/Booru/BooruSessionComposition.cs:161-163
new(binding.Transport, binding.Source, limits: null, ...)
```

Gleichzeitig enthält `Desktop/Config.cs` nur die vorhandenen drei Booru-Präferenzen, nicht die vier Remote-Image-Limits. Das ist eine echte Implementierungslücke für den B5-Settingsumfang, sofern die B5-Spezifikation diese Limits einschließt (der Quellkommentar behauptet genau diesen Umfang). Erforderliche Folgearbeiten sind:

1. benannte Config-Felder mit Min/Max/Default und Export-/Copy-/Load-Validierung;
2. Settings-/UI-Anbindung ohne Request-Replay;
3. Übergabe der Werte beim Erzeugen der Session und/oder kontrolliertes `ApplyLimits` bei laufender Session;
4. normale Persistenz-, Clamp-, Live-Apply- und Restart-Tests.

Ich habe das nicht spekulativ in die Audit-Basis implementiert, weil der genaue öffentliche Config-/UI-Vertrag in `origin/goal/gesamtprogramm-profile-a` nicht vorliegt und der geprüfte Featurebranch 21 Commits vor bzw. 332 Commits neben der aktuellen Zielbasis liegt.

### 2. CDN-Fixture-Test isolieren

Der oben beschriebene gemeinsame Fence ist ein konkreter Testblocker. Das ist eine kleine, evidenzbasierte Testkorrektur im PluginRuntime-Test, aber sie gehört auf den Feature-/Integrationsbranch, auf dem die CDN-Fixtures eingeführt wurden; sie wurde nicht in einen fremden, divergierenden Stand kopiert.

## Entscheidung

Auf der Audit-Branch wurde **nur diese Audit-Dokumentation** ergänzt. Es gab keinen sicheren, kleinen Produktpatch, der zugleich auf der aktuellen Zielbasis liegt und ohne den fehlenden B5-Config-Vertrag verantwortbar wäre. Die Branch ist damit für Review/Weitergabe geeignet, aber nicht als B5–B10-Abnahme:

- B6–B9: normale Vertrags-/Modeltests grün, native Smoke-Akzeptanz offen;
- B5: vorhandene Browse-Persistenz grün, Remote-Image-Settings noch offen;
- B10: bewusst nicht geprüft/geändert;
- Kandidatenbranch: vor einer PR rebasen/portieren und den CDN-Fixture-Isolationsfehler beheben.

## Abschlusslauf auf der Audit-Basis

Auf der Audit-Basis selbst wurden die fokussierten Läufe ebenfalls angestoßen:

- Ein `--no-restore`-Lauf war wegen fehlender `obj/project.assets.json` nicht ausführbar.
- Ein anschließender normaler Desktop-Testlauf stellte Restore und Kompilierung an, lief aber wegen der parallel ausgelasteten Build-/Testumgebung über mehrere Minuten ohne Testresultat und wurde sauber abgebrochen; es gab keinen Testfehler aus diesem Lauf.
- Der fokussierte `DanbooruSourceTests`-Lauf stellte Restore und die Danbooru-Testassembly her, erreichte wegen derselben Umgebungsauslastung aber ebenfalls kein Testresultat und wurde sauber abgebrochen.

Das ist ein Umgebungs-/Ausführungsblocker für einen neuen Testlauf auf der Audit-Basis, kein Befund gegen die dokumentierten Kandidaten-Testergebnisse. Die belastbaren Kandidatenläufe oben bleiben die Grundlage des B5–B10-Audits.
