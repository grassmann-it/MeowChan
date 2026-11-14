# B1 Host-HTTP: Scope-Retirement und Fehlerisolierung

Stand: 14.11.2026, Basis `36514cc6`, Branch `codex/api-b1-current-20261114`.
Nur dieser Worktree; kein Zugriff auf den Root-Checkout oder das Nutzerprofil.
Keine Live-Providerrequests: Fake-HTTP, Fixtures und IPv4-Loopback.

## Vorgaben und Grenze des Abgleichs

`AGENTS.md`, `.architecture-sparring` und
`.claude/coordination/booru-b1c-brief.md` fehlen in diesem Worktree. Kein Rückgriff
auf Root-Dateien. Maßgeblich für diesen Lauf waren der konkrete Arbeitsauftrag,
`review/booru-b1-review-2026-11-12.md` sowie die HTTP-bezogenen Hinweise der
B5-Vorprüfung und B9-Gates. Kein vollständiger Abgleich gegen den fehlenden Brief.

## Reproduziert und behoben

1. **Retirement durfte keinen Einzelrequest-Job erzeugen.** `MetadataHistory.Current`
   blendet geschlossene Verläufe aus. Der Kompatibilitätsadapter interpretierte das
   als fehlenden Scope und eröffnete einen neuen Job. Eine vor Retirement erfasste
   Fortsetzung konnte dadurch erneut HTTP senden, auch wenn vorher noch kein Job
   materialisiert worden war. Der Adapter unterscheidet nun einen tatsächlich
   kontextfreien Aufruf von einem geschlossenen Scope und liefert dort `booru-job`
   ohne Transportzugriff.
2. **Fehlende Resume-Bindung durfte nicht kontextfrei werden.** Eine eigene
   AsyncLocal-Scope-Markierung erhält die Korrelation auch dann, wenn `Resume`
   keinen lebenden Verlauf findet. Auch IHostHttp-Brücken, die innerhalb dieses
   Scopes den kontextfreien Overload verwenden, scheitern geschlossen. Verlauf
   und Markierung werden beim Verlassen gemeinsam wiederhergestellt.
3. **Explizite Operation besitzt ihre Fehlerbeobachtung.** Der Context-Overload
   setzt jetzt vor Admission und Beobachtung den korrelierten Verlauf fort. Ohne
   ambienten Verlauf gingen Fehlerbelege bisher verloren; mit einem fremden
   ambienten Verlauf landeten sie dort. Exakte finale SDK-Echos gehören nun zum
   expliziten Owner. Ungebundene Operationen kontaminieren keinen anderen Verlauf.

Produktänderungen ausschließlich in `PluginHostHttp.cs` und `BooruMetadataHttp.cs`.
`BooruHostHttp` bleibt die gemeinsame begrenzte Transportimplementierung, unverändert.
Öffentliche API-/SDK-Signaturen und Fehlercodes bleiben unverändert. Ein wirklich
kontextfreier Direktaufruf erhält weiterhin seinen begrenzten Einzelrequest-Job.
Chan behält seine Policy, Redirects, HEAD und Identität.

## Belege

Elf neue Fälle in `PluginHostBooruRoutingTests.cs`: zwei Retirement-Fälle, zwei
fehlende/geschlossene Resume-Bindungen, vier Kombinationen expliziter
Fehlerbeobachtung, eine ungebundene Operation und zwei Abbruchvarianten mit einem
wartenden unabhängigen Geschwisterjob. Erst sieben Fälle rot nachgewiesen;
anschließend zwei weitere Resume-Fälle ebenfalls vor ihrem Fix rot nachgewiesen.

Alle Builds/Tests unter `flock /home/tomka/.cache/claude-ssd-tmp/heavy.lock`.
Compile-first: Builds beider Testprojekte vor den abschließenden Tests, jeweils
`--no-restore -p:UseSharedCompilation=false -m:1`. Finale Builds: 0 Fehler,
0 Warnungen. Der erste `--no-restore`-Versuch scheiterte nur an fehlenden Assets;
Restore danach ausschließlich mit `--source /home/tomka/.nuget/packages
--locked-mode -p:NuGetAudit=false`. Beim ersten vollständigen Build erschien die
bestehende CS1573-Warnung in `FolderSourceSuccession.cs` (nicht geändert).

Finale Testläufe (`dotnet test --no-build --no-restore`):

| Projekt / Auswahl | Ergebnis | Lokaler TRX-Beleg unter `artifacts/b1-host-http/` |
| --- | --- | --- |
| PluginRuntime: PluginHostBooruRoutingTests, BooruHostHttp*, BooruJobLifetimeTests, BooruOperationJobIntegrationTests, HostHttpBoundaryTests, PluginTransportMeteringTests | 189 bestanden | `b1-http-final.trx` |
| SourceAdapter: HostHttp*, MetadataHistoryTests, PluginFailureDiagnosticsTests, PluginSourceDiagnosticCausalityTests, PluginSourceRequestDiagnosticContextTests, PluginSourceRequestProgressBridgeTests, SourceAdapterTests, WorkerArchiveTests | 222 bestanden | `source-adapter-final.trx` |
| PluginRuntime: BooruRealWorkerTests, RuntimeProcessTests (Booru*, RealWorkerDescribes*, CancelCooperative*, FinishingOrCrashingCall*, RepeatedBackgroundServices*) | 12 bestanden | `runtime-worker-final.trx` |

Insgesamt **423 bestanden, 0 fehlgeschlagen, 0 übersprungen**. Die erste Auswahl
prüft insbesondere 20 Sends inklusive Retries, Deadline ab Operation-Bindung
(120 s), 15-s-Antwortdeadline, 2 MiB wire / 4 MiB decoded einschließlich fehlender
Content-Length und gzip/deflate/br, 429-Cooldown, begrenzte 502/503-/Transport-Retries,
Redirectverbot und Abbruch in Gate/Spacing/Retry/Header-/Body-Waits. Der echte
Fixture-Worker/Loopback belegt die Operation über mehrere Host-HTTP-Aufrufe.

## Nicht beansprucht

Keine B2-Domain-, B3-Katalog-, B4-Queue-, UI- oder SDK-Änderungen. Keine Live-
Danbooru-Abnahme, keine Gesamtsuite, keine Desktop-/Paketierungs-/Release-Abnahme.
Kein Merge, Reset, Stash, Push, Tag, Release oder Account-/Serviceeingriff.
