# R10-10 — Plugin-Auslieferung

Stand: 22.10.2026, Rolle `build_activation` im Folgeauftrag Packaging.
Arbeitskopie: `oChan-1.0-workers/packaging`, Branch `impl/1.0-wave2-packaging`.

## Lieferung

`packaging/PluginDelivery.targets` ergänzt den Restore-/Buildgraph um Worker,
Compiler und die fünf ausgelieferten SDK-Plugins. Die Projektkanten haben
`ReferenceOutputAssembly=false`: Helper und Roslyn gelangen nicht in den
Referenz-/Laufzeitgraph des Hauptprozesses. Die zentrale Einbindung im Desktop
csproj sowie Versionsmetadaten, Solution und produktive Lockdateien gehören Root.

Normale Builds benötigen nur das .NET SDK. Ein MSBuild-Inlinetask erzeugt die
Manifeste, prüft gleiche SDK-Bytes zwischen Worker und Compiler und stellt die
Auslieferung unter dem jeweiligen `IntermediateOutputPath` zusammen. Über
`ContentWithTargetPath` gelangt diese auch transitiv in DesktopSmoke/Test-
Ausgaben. Das ist mit einem zweiten Fixtureprojekt praktisch geprüft.

Publish erstellt beide Helper für das gewählte RID self-contained, ungetrimmt
und als mehrere Dateien; sie bleiben außerhalb der Haupt-Singlefile. Es erfolgt
kein impliziter zweiter Restore. Die Plugin-Klassenbibliotheken bleiben RID-neutral.
Die Pfade funktionieren auch mit `--artifacts-path` und Leerzeichen im Zielpfad.

```text
<application>/plugins/*.plugin.json          # genau fünf Manifeste
<application>/plugins/assemblies/*.dll
<application>/plugins/sources/Lainchan.cs
<application>/plugins/sources/Kohlchan.cs
<application>/plugins/sources/ImageDimensionsFilter.cs
<application>/plugin-worker/                 # Apphost und private Runtime
<application>/plugin-compiler/               # Apphost, Runtime, Roslyn, Referenzen,
                                             # toolchain.json und Lizenzen
```

Die Beispielquellen liegen absichtlich unter `sources/`, damit sie keine weiteren
Installationskandidaten im obersten `plugins/`-Verzeichnis bilden. Laufzeitdaten
gehören ins Profil; das Auslieferungsverzeichnis bleibt schreibgeschützt nutzbar.

`scripts/package-plugins.py` verifiziert vorhandene Ausgaben ausschließlich lesend:
native Apphostarchitektur für alle fünf RIDs, Unix-Ausführungsrechte, private
CoreCLR/hostpolicy/hostfxr-Dateien, self-contained runtimeconfig, vollständige
Toolchain-Dateihashes/Fingerprints, SDK-Konsistenz, Pluginhashes, alle drei Quellen,
Lizenzen und unerwünschte Hauptprozessabhängigkeiten. Traversal, Symlinks,
Spezialdateien und zusätzliche oberste Installationskandidaten führen zum Fehler.
`--development` erlaubt bewusst framework-dependent Entwickler-Helper.

Die CI prüft Entwickler- und transitive Smoke-Ausgaben sowie native Publish- und
extrahierte Pakete. `MEOWCHAN_DISABLE_UPDATE_CHECK=1` ist nur im Workflow gesetzt;
die Produkteinstellung wurde nicht geändert.

Die macOS-Signierung erteilt die vorhandenen CoreCLR-Entitlements dem Hauptapphost
und ausschließlich den zwei exakt bekannten Helper-Apphosts. Helper-Abhängigkeiten
werden relativ zu ihrem eigenen `@executable_path` aufgelöst. Architekturen und
Entitlements werden auch für diese beiden Hosts validiert. Andere Binaries erhalten
dadurch keine zusätzlichen Rechte.

## Eigene Commits und Voraussetzungen

- `d573e37`: MSBuild-Auslieferung, Verifier und zwei Build-Fixtureprojekte.
- `bf986c6`: CI-Paketprüfungen, native Verifierfälle, bekannte macOS-Helper signieren.
- `6eccdc8`: RID-neutrale Pluginpfade, Linux-Fixture-Lock und SDK-freier Worker-Probe.
- Der Commit mit diesem Bericht ergänzt hostfxr-Nachweis, exakte Plugin-ID-Assertions
  und die beiden eigenen portablen Release-Lockdateien.

Die folgenden übernommenen Voraussetzungen sind **keine erneut zu integrierende
eigene Lieferung**: Baseline `866fdc7`, Root-Commit `396995d` (lokal `82e4cf7`,
Version/Import) und Root-Commit `3e8503f` (lokal `c70cb54`, zentrale Release-Locks).
Andere Worker besitzen SDK-, Runtime-, Compiler- und Pluginimplementierungen.

## Tatsächlich ausgeführte Prüfungen

Alle Restore-/Build-/Publish-Kommandos liefen unter dem gemeinsamen
`/tmp/meowchan-ui-1.0-build.lock` mit `MSBUILDDISABLENODEREUSE=1` und
`-nodeReuse:false`. Keine persönlichen Profile, Displays oder Downloads benutzt.

### Entwicklergraph und transitive Ausgabe

`PluginDeliverySmoke` und `PluginDeliveryConsumerSmoke`: Release-Build erfolgreich,
Consumer zuletzt nach den zentralen 1.0-Metadaten/Release-Locks erneut erfolgreich
mit 0 Warnungen und 0 Fehlern. Der portable Restore war im Locked Mode erfolgreich.

```sh
dotnet build tests/PluginDeliveryConsumerSmoke/PluginDeliveryConsumerSmoke.csproj \
  -c Release --no-restore -nodeReuse:false
python3 scripts/package-plugins.py \
  --verify tests/PluginDeliveryConsumerSmoke/bin/Release/net10.0 \
  --runtime linux-x64 --development \
  --report artifacts/plugin-implementation/packaging/transitive-payload.json
```

PASS: fünf Plugins, drei Quellen und 171 verifizierte Compilerdateien. Die
Haupt-Fixture-deps.json enthält keine Worker-/Compiler-/Roslyn-Abhängigkeit.

### Echter nativer Linux-Publish

```sh
dotnet restore tests/PluginDeliverySmoke/PluginDeliverySmoke.csproj \
  --artifacts-path artifacts/plugin-delivery-build -p:Configuration=Release \
  -p:RuntimeIdentifier=linux-x64 -p:SelfContained=true -p:PublishSingleFile=true \
  --locked-mode -nodeReuse:false
dotnet publish tests/PluginDeliverySmoke/PluginDeliverySmoke.csproj \
  --artifacts-path artifacts/plugin-delivery-build -c Release -r linux-x64 \
  --self-contained true --no-restore -p:PublishSingleFile=true \
  -o 'artifacts/plugin-delivery-publish/relocated application' -nodeReuse:false
python3 scripts/package-plugins.py \
  --verify 'artifacts/plugin-delivery-publish/relocated application' \
  --runtime linux-x64 --report artifacts/plugin-implementation/packaging/linux-payload.json
```

PASS: echter self-contained Linux-x64-Worker und -Compiler, fünf Plugins,
drei Quellen, 171 Toolchaindateien. Der Verifier wurde nach Ergänzung der
hostfxr-Pflicht erneut erfolgreich auf demselben Paket ausgeführt.
Der anfängliche RID-Pfadfehler der Klassenbibliotheken ist in `6eccdc8` behoben;
der hier dokumentierte Publish lief danach erfolgreich.

### Ohne zugängliches SDK

Der vorhandene Compiler-Probe
`tests/MeowChan.PluginCompiler.Tests/sdk_free_probe.py` kompilierte alle drei
tatsächlich ausgelieferten Quellen aus dem obigen Compilerbundle erfolgreich.
Bubblewrap verbarg SDK und NuGet-HOME, sperrte Netzwerkzugriff, band die Installation
read-only ein und startete aus einem fremden Arbeitsverzeichnis.
Nachweis: `sdk-free-source-probe/RESULT.json` (3/3 PASS, keine Diagnosen).

Der eigene Probe `tests/PluginDeliverySmoke/sdk_free_worker_probe.py` startete
den echten ausgelieferten Worker in vergleichbarer Isolation und eigener
PID-Umgebung. Für jedes Manifest folgten framed Hello, Describe, geschlossene
Standardeingabe und Exitcode 0. Der abschließende Lauf prüfte die exakten Plugin-IDs:

| Manifest | Plugin-ID | Source-ID / Filter-Key |
| --- | --- | --- |
| FourChan | `meowchan.official.4chan` | `4chan` |
| EightKun | `meowchan.official.8kun` | `8kun` |
| Lainchan | `meowchan.source.lainchan` | `lainchan` |
| Kohlchan | `meowchan.source.kohlchan` | `kohlchan` |
| ImageDimensionsFilter | `meowchan.example.image-dimensions` | `dimensions` |

Nachweis: `sdk-free-worker-final/RESULT.json` (5/5 PASS).
Die kleinen JSON-Zusammenfassungen sind beigefügt; Binaries und vollständige
temporäre Probeverzeichnisse werden nicht versioniert.

### Python-Verträge

- `python3 -m unittest discover -s tests -p test_plugin_packaging.py -v`: 13 PASS.
- `python3 -m unittest discover -s tests -p test_macos_signing.py -v`: 9 PASS.
- `python3 -m unittest discover -s tests -p test_macos_packaging.py -v`: 28 PASS,
  1 SKIP (`Requires native Apple signing tools`).
- `git diff --check`: PASS vor Übergabe.

Die Architektur-/Dateiformatfälle für Windows und macOS verwenden Fixtures.
Apple-Signierkommandos werden in den portablen Vertragstests gemockt.

## Grenzen und Integration

Root führt die abschließenden Desktop-Publish-/Paketprüfungen für alle fünf RIDs
durch. Dieser Bericht ist kein Nachweis nativer Windows-/macOS-Ausführung,
realer Apple-Signierung, GUI-Bedienung, HTTP-Quellenparität, Filterwirkung oder
vollständiger Session-Neustarts. Er belegt den Auslieferungsgraph, Paketinhalt,
SDK-freie Compilation und den tatsächlichen Worker-Describe-Pfad auf Linux x64.

Der bekannte macOS-Payloadalias wurde mit dem Adapterowner abgestimmt:
`3a71e6a` wählt in `PluginSession` für das bekannte `Contents/MacOS`-Layout die
vorhandene physische Basis `Contents/Resources/application` für Plugins und beide
Helper. Damit erhält CompilerWire einen physischen ausgelieferten Pfad; allgemeine
Benutzer-/Cache-Symlinks bleiben abgewiesen. Dieser Adaptercommit gehört nicht zu
dieser Lieferung und benötigt weiterhin die native macOS-Gesamtprüfung.
