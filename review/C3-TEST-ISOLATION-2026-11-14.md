# C3 – Testisolation auf dem festen wave5-Stand

## Basis und Umfang

- Basis: `a8e9600454f1ff157d7cc6d0db260c9659ef6720`, nicht der inzwischen weitergewanderte Integrationstip.
- Eigener Branch: `codex/c3-test-isolation-20261114`.
- Nur zwei Testdateien geändert; kein Produktcode, keine Features anderer Pakete.
- Root/goal nicht verändert, kein Push/Tag. Private HOME/TMP/XDG-Verzeichnisse,
  kein Anwendungsstart und keine Live-Providerrequests. Restore ausschließlich aus
  dem vorhandenen öffentlichen NuGet-Paketcache mit leerem lokalen Feed und ausgeschaltetem Audit.

## Ursache und Abgrenzung

### WorkViewActionStateGuardTests

Die Meldung stammt aus dem Medien-Booru-Zweig auf `67541b0a`. Dort fehlt
`2cb1d09c` (owned UI dispatcher); die beauftragte Basis enthält diesen Fix bereits.
Das erklärt den Unterschied zwischen dem alten breiteren Lauf und dem aktuellen Stand.
Auch die Koordinationsnotiz `UX-WAVE5-STAPEL-2026-11-14.md` vermerkt bereits einen grünen
WorkView-/Galerie-Lauf auf `a8e96004`.

Der geteilte Zustand ist Avalonia 12.1.4s **prozessweites `Dispatcher.s_uiThread`**.
Der zuerst berührende xUnit-Worker-Thread behält die primäre Dispatcher-Zuordnung;
später serialisierte Tests können auf einem anderen Worker laufen. Die bestehende
nichtparallele Collection verhindert gleichzeitige Zugriffe, nicht diesen First-Touch-Effekt.
Reale Menü-ItemCollections prüfen den primären UI-Thread. Der bereits vorhandene
`OnOwnedDispatcher` richtet ihn für den synchronen Testkörper auf `CurrentDispatcher`
aus und stellt den vorherigen Wert in `finally` wieder her.

Lokal sind **alle 16 aktuellen Guard-Fälle** auf der unveränderten Basis grün,
einschließlich der historisch gemeldeten 12. Kein zusätzlicher Produkt- oder
Shortcut-Registry-Fehler wurde in den gezielten Läufen nachgewiesen; insbesondere
werden keine fremden Observer gelöscht und keine globalen Collections geleert.

Neu sind zwei deterministische Regressionstestfälle (normal / synthetische Exception):

1. Einen anderen Dispatcher auf einem separaten Thread erzeugen (Join auf fünf Sekunden begrenzt).
2. Ihn explizit als primären Dispatcher installieren; ungeschützte reale Menüerzeugung
   muss als **negative Kontrolle** mit `InvalidOperationException` scheitern.
3. Dieselbe Menü-/Guard-Strecke über den vorhandenen Owned-Scope ausführen:
   genau ein Klick, danach keine Weiterleitung bei deaktivierter Oberfläche.
4. Identische Wiederherstellung des fremden primären Dispatchers auch bei Exception prüfen;
   der äußere `finally` stellt den Zustand vor dem Test wieder her.

Damit ist kein zufälliger Vorgängertest für die Reproduktion nötig. Keine neuen Orderer,
Skips, Sleeps, GC-Tricks oder Änderungen an der Collection-/Parallelisierungsstrategie.
Die bereits vorhandene nichtparallele Collection schützt den prozessweiten Test-Seam
weiterhin auch bei eingeschalteter Runner-Parallelisierung.

### SurfaceStyleTests.BooruHitsAndWatchersAreCardsAndTheirThumbnailIsFlat

**Auf der festen Basis tatsächlich rot**, sowohl im engen als auch im Störlauf:
`Assert.Contains` findet `BooruResult:` nicht im alten `BooruWorkbenchWindow.cs` (Zeile 65).
Das Fenster ist nach der Workspace-Extraktion nur noch ein Host. Karten und flache
Thumbnail-Platzhalter werden in `BooruWorkbenchView.cs` gebaut.

Fix ausschließlich im Test: tatsächlichen Implementierungsbesitzer lesen. Alle
bestehenden positiven Karten-/Thumbnail- und negativen `raised`-Assertions bleiben erhalten.
Kein Style-Produktpatch und kein übersprungener Vertrag.

### GalleryMarkingTests.TheViewerMarksThroughTheControllerAndTheNamedFilesStayUntouched

Schon auf der festen Basis grün. Sie enthält `b061c99d`: nur der V1-Seekbar-Bezeichner
`_seekMarks` wird vor der unveränderten No-Tag-Marks-Prüfung ausgenommen.
Kein neuer Befund, deshalb keine Änderung an Test oder Galerie-Produktdateien.

## Ausgeführte Prüfungen

| Lauf | Ergebnis |
|---|---|
| Basis: DesktopTests compile-first, Release, `-m:1`, SharedCompilation aus | 0 Fehler, 162 bestehende Warnungen |
| Basis: Guard-Klasse + SurfaceStyle-Klasse + genannter Galerie-Vertrag | 26/27 grün; nur der oben belegte SurfaceStyle-Basisfehler |
| Basis: gezielter Störlauf, paralleler Runner mit vier Threads | 406/407 grün; derselbe einzelne SurfaceStyle-Basisfehler |
| Nach Testpatch: inkrementeller Testbuild, Produktreferenzen unverändert | 0 Fehler, 157 bestehende Warnungen |
| Nach Testpatch: enger Lauf inklusive beider Fremd-Dispatcher-Fälle | 29/29 grün, 0 übersprungen |
| Nach Testpatch: gezielter Störlauf, Default-Runner | 409/409 grün, 0 übersprungen |
| Nach Testpatch: derselbe Störlauf, vier Threads, zwei fest geplante Läufe | jeweils 409/409 grün, 0 übersprungen |

Keine vollständige Suite, keine native GUI-/Release-Abnahme; daraus folgt kein Release-Readiness-Claim.
Schwere Builds liefen unter `flock /home/tomka/.cache/claude-ssd-tmp/heavy.lock`.
Der erste Lock-Wait brach nach 240 Sekunden ab, der zweite war auf 900 Sekunden begrenzt;
keine Sperre umgangen und keine unbeschränkten Build-/Test-Retries.

## Reproducer und Evidence

Alle Rohdaten liegen im eigenen Task-Verzeichnis neben diesem Worktree:

```
/home/tomka/.cache/claude-ssd-tmp/codex-wave-20261114-1625/api-c3-test-order/c3-run/
```

- `env.sh`: privates HOME/TMP/XDG, Telemetrie/Workload-Update aus, keine Desktop-/Session-Verbindung.
- `logs/02-base-build.log`, `logs/03-base-targeted.log`, `logs/04-base-interference.log`.
- `results/03-base-targeted.trx`, `results/04-base-interference.trx`: Fallnamen und Basisfehler mit Stacktrace.
- `results/04b-base-targeted-private-xdg.trx`: derselbe Basisbefund erneut mit auch bereinigten XDG-Suchpfaden (26/27).
- `base-filter.txt`, `interference-filter.txt`, `parallel.runsettings`: exakte Auswahl und Runner-Konfiguration.
- `logs/05-fix-build.log`, `results/06-fix-targeted.trx`, `results/07-fix-interference-default.trx`,
  `results/08-fix-interference-parallel-1.trx`, `results/09-fix-interference-parallel-2.trx`.
- `summary.json` enthält die maschinenlesbaren Zähler; `evidence.sha256` die Hashes der Rohdaten.

Eigenständige Wiederholung aus einem Checkout dieses Branches (Bash; nur vorhandene Pakete):

```sh
set -euo pipefail
RUN=$(mktemp -d /home/tomka/.cache/claude-ssd-tmp/c3-repro.XXXXXXXX)
mkdir -p "$RUN"/{home,tmp,config,cache,data,state,runtime,empty-feed}
chmod 700 "$RUN/runtime"
export HOME="$RUN/home" TMPDIR="$RUN/tmp" TMP="$RUN/tmp" TEMP="$RUN/tmp"
export XDG_CONFIG_HOME="$RUN/config" XDG_CACHE_HOME="$RUN/cache"
export XDG_DATA_HOME="$RUN/data" XDG_STATE_HOME="$RUN/state" XDG_RUNTIME_DIR="$RUN/runtime"
export XDG_CONFIG_DIRS="$RUN/config" XDG_DATA_DIRS="$RUN/data:/usr/local/share:/usr/share"
export DOTNET_CLI_HOME="$HOME" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
export NUGET_PACKAGES=/home/tomka/.nuget/packages
unset DISPLAY WAYLAND_DISPLAY DBUS_SESSION_BUS_ADDRESS XDG_SESSION_PATH XDG_SEAT_PATH
flock -w 900 /home/tomka/.cache/claude-ssd-tmp/heavy.lock timeout 240 \
  dotnet build tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  -c Release -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --source "$RUN/empty-feed" &&
timeout 120 dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  -c Release --no-build --no-restore \
  --filter 'FullyQualifiedName~MeowChan.DesktopTests.WorkViewActionStateGuardTests|FullyQualifiedName~MeowChan.DesktopTests.SurfaceStyleTests|FullyQualifiedName=MeowChan.DesktopTests.GalleryMarkingTests.TheViewerMarksThroughTheControllerAndTheNamedFilesStayUntouched' \
  --logger 'trx;LogFileName=reproducer.trx' --results-directory "$RUN/results"
```

Für den exakt gleichen Störlauf liegen die vollständige Klassen-ODER-Auswahl in
`interference-filter.txt`, die Vier-Thread-Konfiguration in `parallel.runsettings`
und der begrenzte `--no-build --no-restore`-Runner in `test-targeted.sh` im obigen Evidence-Verzeichnis.

Die engste deterministische Dispatcher-Reproduktion ist der Filter
`FullyQualifiedName~AForeignPrimaryDispatcherIsIsolatedAndRestoredEvenWhenTheBodyThrows`.
Er enthält die rote ungeschützte Kontrollstrecke und die grüne Scoped-Strecke in
jedem einzelnen Testfall, ohne einen zweiten Checkout oder eine Testreihenfolge vorauszusetzen.
