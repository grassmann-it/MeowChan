# Compiler / Installation — Implementierungsbericht

Stand: 22.10.2026. Rolle build_compiler. Worktree:
`/home/tomka/Desktop/oChan-1.0-workers/compiler`.

## Lieferstand

Eigene Codecommits, in dieser Reihenfolge:

- `fbc4bb9` — Build-/Inventar-/Diagnose-/Cacheverträge.
- `d83c3fb` — Roslyn-Helfer, geliefertes Referenzpaket, geprüfter Cache,
  generisches Ressourcenmanifest, Prebuilt-Import und erste Integrationstests.
- `40f79cf` — begrenzte Sonderdateileser, FIFO-Regression, Prozess-Shutdown,
  Ausgabelimit, Crosspublish-Manifestgenerator, Linux-RIDlock und SDKfreie Probe.

Voraussetzung ist Runtime-APIcommit `205c049` (lokal als `ccac9a9`
cherry-gepickt). Diesen fremden Commit nicht als Compilerlieferung doppelt
übernehmen. Hostprojektdatei und Hostlock bleiben Runtime/Root-Eigentum.

## Implementierung

- Einzelne `.cs` als konsistenter Bytesnapshot; keine MSBuild-Projekte,
  NuGet-Auflösung, zusätzliche lokale Sourceauflösung, Generatoren, Analyzer oder
  Plugininitialisierung im Nutzerbuild. C#14, net10.0, nullable/release/deterministic,
  unsafe ausgeschaltet. Autorendateien brauchen ihre eigenen using-Direktiven.
- Absoluter Apphost aus dem ausgelieferten Verzeichnis, `ArgumentList`, private
  Pipes, eigenes Stage-Arbeitsverzeichnis. Ein laufender Build und acht wartende
  Operationen, darüber sofort Busy. 60 Sekunden Default, 256 MiB managed Heap,
  4 MiB Source, begrenzte stdout/stderr. Abbruch, Timeout und Dispose beenden und
  erwarten eigene Helferprozesse; Outputüberlauf beendet sofort.
- Bytehashes statt mtime: Source, SDK, Roslyn/Helfer, jede gelieferte Frameworkref,
  TFM und Compileroptionen. Physische Quellpfade sind kein Codekey; Roslyn sieht
  einen stabilen virtuellen Pfad, Diagnosen werden auf Originaldatei/Zeile/Spalte
  zurückgeführt. UI-/Settingswerte sind kein Compilereingang.
- Getrennte CompilationFingerprint/ExecutionRevision. Ressourcenänderungen
  verwenden den Codecache weiter. Ressourcen sind begrenzt auf 128 Einträge,
  32 MiB je Datei und 64 MiB insgesamt. Normalisierte relative Pfade, Hashprüfung,
  RID-/Namensidentität; Traversal/Symlinks und reservierte Cachepfade abweisen.
- Vollständig geschriebene und nochmals verifizierte Stageverzeichnisse werden
  atomar verschoben. Cachelesen prüft DLL und jede Ressource. Beschädigte
  veröffentlichte Revisionen bleiben mit Fehler erhalten, da sie aktiv/gepinnt
  sein können. Eine beschädigte redundante Compilation wird neu gebaut.
- Cleanup erhält explizit alle vom Aktivierungsowner genannten Ausführungs- und
  Compilerrevisionen. Compiler aktiviert keine Fassung und führt keinen Fallback
  aus. Konfiguration, Auswahl, last-good und Pins bleiben allein Activation.
- Begrenztes, nicht rekursives Inventar mit sichtbaren unbekannten/leeren/
  beschädigten Kandidaten; `.plugin.json` wird als begrenzter abbrechbarer
  Snapshot gelesen. POSIX nonblocking/no-follow handles und RandomAccess
  verhindern blockierende FIFO-Kandidaten. Dateiöffnen/Hashing bleiben off-UI.
- Vorgebaute `.plugin.json`-Bündel werden geprüft und ohne Compilerstart oder
  Plugininitialisierung in denselben Artefaktcache importiert.

Öffentliche Anschlussverträge stehen in Host `Compilation/` und `Installation/`.
`PluginCompilationService` implementiert `IPluginCompilationService`,
`IPluginBundleInstaller` und `IAsyncDisposable`. Die owning Session muss den
konkreten Dienst beim Shutdown disposen; Adapterowner wurde informiert.
Der Artefakt-Datentyp stammt aus `Host.Contracts`, dessen Owner Runtime ist.

## Pakete, Herkunft und Lizenzen

`Microsoft.CodeAnalysis.CSharp` und `Microsoft.CodeAnalysis.Common` **5.0.0**,
NuGet-Contenthashes in Compilerlocks. Frameworkreferenzen kommen ausschließlich
vom NuGet-Paket **Microsoft.NETCore.App.Ref 10.0.11**, `ref/net10.0/*.dll`.
Weil NuGet PackageDownload nicht im Lockfile erfasst, prüft der Projektbuild
explizit den gepinnten SHA512 des .nupkg-Archivs:

`tHHeb+axV3SHYPyafZEbcEFuJlvRlO395guslArUZCWr4JX0x2vd0ZE895Vc6+HQnQRQRPZJKYqp4jWrnxMLhA==`

`MeowChan.PluginCompiler/provenance.json` enthält offizielle NuGet-URLs und
Versionen. Build/Publish erzeugen `toolchain.json` mit SHA256 jeder gelieferten
Compiler-/SDK-/Frameworkdatei. MIT-Lizenz und Third-party-Notices aus Roslyn und
.NET sind im tatsächlichen Publish vorhanden und durch die Lieferprobe geprüft.
Keine Runtime-SDK-/PATH-/Home-/NuGet-Ermittlung. Nur der Entwicklerbuild benötigt
SDK/Restore. Die Crosspublish-Manifestgenerierung verwendet ausdrücklich die
native Buildhost-Runtimeconfig mit leerem Depsgraph statt der fremden
self-contained Zielruntime.

Lieferlayout und vollständiges Manifestbeispiel:
`MeowChan.PluginCompiler/README.md`. Vorgesehen:
`<app>/plugin-compiler/MeowChan.PluginCompiler[.exe]` mit gesamtem Output,
`references/`, `licenses/`, `toolchain.json`; Cache im separaten Profilverzeichnis.
Der tatsächliche Profil-/Quellordner wird vom Adapter festgelegt.
Linux-x64 Lock liegt bei. Übrige RIDlocks und globale Releasekonventionen
übernimmt Root laut Abstimmung.

## Ausgeführte Prüfungen

Build/Restore/Test/Publish wurden mit der gemeinsamen flock-Sperre ausgeführt.
Der abschließende Satz ist auf Linux x64 / .NET SDK 10.0.111, Runtime 10.0.11:

- **16/16 Integrationstests PASS, 0 skipped**, rund 20 Sekunden.
- Echte Childprozesse: Cachetreffer über neue Serviceinstanz, Timestampänderung
  ohne Neubau, Byteänderung bei gleichem Timestamp mit Neubau, Originaldiagnosen
  im Pfad mit Leerzeichen, kein Ausführen von ModuleInitializer, Ressourcenwechsel
  ohne Codecompile, DLL-/Metadaten-/Ressourcenkorruption, aktive/gepinnte Cleanup-
  Erhaltung, Cancel/Timeout bei großer blockierender stdin, eingefrorener Snapshot
  während Sourceänderung, fehlende/manipulierte Referenz und SDKänderung,
  leeres/begrenztes Inventar, Prebuilt-Import ohne Compilerbinary, Prozesscrash,
  Admissionlimit, deterministische CallerFilePath-Bytes bei Pfadwechsel,
  Dienst-Shutdown, echte FIFOs, stdout-Übergröße.
- `git diff --check` PASS.
- **Linux-self-contained Publish PASS**, einschließlich Lizenz-/Noticefiles.
- **SDKfreie native Lieferprobe PASS für echte Lainchan.cs und Kohlchan.cs**
  aus Examples-Lieferung `58357cc`. `bwrap` verbirgt `/usr/share/dotnet`, mountet
  kein HOME/NuGet, schaltet Netzwerk ab, setzt PATH/DOTNET_ROOT auf `/no-sdk` und
  bindet das gesamte Compilerpaket readonly nach `/compiler`. Ausführung aus
  separatem `/work`, Quellpfad mit Leerzeichen, begrenzte Bytesnapshotanfrage.
  Beide erzeugen echte gehashte DLLs. Die Probe initialisiert kein Plugin.

Reproduzierbare Commands (im Compilerworktree, jeweils unter der gemeinsamen
Sperre; Restore besitzt die vorhandenen Locks):

```sh
flock --close /tmp/meowchan-ui-1.0-build.lock env MSBUILDDISABLENODEREUSE=1 dotnet test tests/MeowChan.PluginCompiler.Tests/MeowChan.PluginCompiler.Tests.csproj -c Release --no-restore -nodeReuse:false --logger 'trx;LogFileName=compiler-final.trx' --results-directory artifacts/plugin-implementation/compiler/test-results
flock --close /tmp/meowchan-ui-1.0-build.lock env MSBUILDDISABLENODEREUSE=1 dotnet restore MeowChan.PluginCompiler/MeowChan.PluginCompiler.csproj -p:Configuration=Release -r linux-x64 -p:SelfContained=true --locked-mode -nodeReuse:false
flock --close /tmp/meowchan-ui-1.0-build.lock env MSBUILDDISABLENODEREUSE=1 dotnet publish MeowChan.PluginCompiler/MeowChan.PluginCompiler.csproj -c Release -r linux-x64 --self-contained true --no-restore -o 'artifacts/plugin-implementation/compiler/linux bundle final' -nodeReuse:false -v:q
flock --close /tmp/meowchan-ui-1.0-build.lock python3 tests/MeowChan.PluginCompiler.Tests/sdk_free_probe.py 'artifacts/plugin-implementation/compiler/linux bundle final' artifacts/plugin-implementation/compiler/sdk-free-probe-final /home/tomka/Desktop/oChan-1.0-workers/examples/plugins/Examples/Lainchan/Lainchan.cs /home/tomka/Desktop/oChan-1.0-workers/examples/plugins/Examples/Kohlchan/Kohlchan.cs
```

Die Probe verlangt einen neuen Ergebnisordner. Nach Rootintegration können die
beiden Sources aus dem integrierten `plugins/Examples`-Baum genommen werden.
Ohne Sourceargumente erzeugt das Skript ein minimales SDK-Plugin als Fixture.

Lokale Belege unter diesem Worktree:

- `artifacts/plugin-implementation/compiler/test-results/compiler-final.trx`
- `artifacts/plugin-implementation/compiler/sdk-free-probe-final/RESULT.json`
- Darunter je isoliertem Workverzeichnis `response.json`, `host-trace.txt`,
  Sourcebytes und `plugin.dll`.
- `artifacts/plugin-implementation/compiler/linux bundle final/`

Diese Binär-/Prüfartefakte sind nicht committed; REPORT und Prüfscripts sind es.

## Review und noch ausstehende Integration

Unabhängiger Reviewer audit_integration fand CA-02 (FIFO/open und initial nur
statische Manifestgrößenprüfung). Mit 40f79cf durch nichtblockierende Handles,
seek-basierte Reads und dynamische Bytegrenzen behoben; Reviewer hat den Fix
read-only gegengeprüft, tatsächliche Regression ist Bestandteil der 16 Tests.

Der Compilerliefernachweis ist **keine** Desktop-/Worker-/Registry-/Queue-/Store-
E2E-Abnahme. Root/Adapter müssen Session-Disposal und vorgebaute Bündelmanifest-
Erzeugung in das Produkt verdrahten. Aktivierung bleibt allein build_activation.
Andere native Zielsysteme und die vollständigen fünf Releasearchive sind noch
bei Root/Packaging zu prüfen. Der Crosspublishgenerator wurde lokal gegen die
self-contained Linuxlieferung und im normalen Build geprüft; fremde RIDarchive
werden hier nicht als bereits getestet ausgewiesen. Prozessgrenze/Heaplimit
sind keine OS-Sandbox. Host-/SDK-RIDlocks, die beim lokalen Restore entstanden,
sind fremdes Eigentum und wurden nicht in die Compilercommits aufgenommen.
