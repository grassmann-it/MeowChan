# R10-11 Autorendokumentation — build_examples Folgeauftrag

Stand 2026-10-22, Worktree `oChan-1.0-workers/examples`, nach eigenem
Pluginliefercommit `58357cc`. Keine Produktcode-/Graphänderung und keine
Build-/Restore-/GUIaufrufe für diesen Dokumentationsauftrag.

## Lieferung

Sechs neue öffentliche Seiten unter `docs/plugins/`:

- README: Einstieg, .NET/SDK/APIpolitik, maintained Source-/Filter-/Settingsbeispiele.
- using-plugins: tatsächlicher Pluginordner und Verwaltungsaktionen, Editor,
  Metadatenprobe, aktive/ausstehende/letzte gute/angeheftete Fassungen.
- source-plugins: SDKentry, IDs, deklarative URL-/Identifierregeln, HostHttp,
  Validatoren, Presence/Partial-/End-/Mehrfachdateisemantik, Abbruch.
- filters-and-settings: Schema-/Konfigurations-/Rulescopes, passive Infos,
  Domainvalidierung, scoped Grants, Originalasset-Token, Bytes/Commit/Fehler.
- hosting-and-resources: optionaler Host ohne Corepflicht, tatsächliche APIs,
  vorgebaute Manifestbündel, Compiler-/Executionfingerprint, Resources/RID/Helper.
- validation: realer Paketconsumerweg, Tests/E2E-Abgrenzung, Quellenbefunde,
  Diagnose und Runtimegrenzen.

README.md korrigiert nur „Dynamic plugin loading … later work“ und verlinkt den
neuen Autorenweg. libmeowchan/README.md korrigiert den veralteten 0.9-Zweigtext
und erklärt den optionalen Host. MeowChan.PluginSdk/README.md bekommt nur den
abgestimmten Link; SDKowner hat die Änderung ausdrücklich freigegeben.
Versionen/Releasepaketierung/Release-Notes-Umstellung bleiben Root.

Keine neue Beispielcodekopie: echte Lainchan.cs/Kohlchan.cs,
ImageDimensionsFilter.cs und SyntheticPlugin.cs/SDKconsumer verlinkt. OCR ist
explizit zurückgestellt. Der Compilerimport benötigt laut echter Implementierung
auch für prebuilt SDKabgleich mit dem Toolchainmanifest; kein irreführendes
Versprechen, die Lieferung ohne diese Daten sei vollständig.

## Gelesene und abgestimmte Quellen

R10-11, Plugin-/Filtervertrag und APIpolitik gelesen. Öffentliche SDK-/Host-/
Compiler-/Activation-/Grantverträge sowie Implementierungen im Root gelesen;
Management UI/PluginCatalog und ImageDimensionsFilter im zugewiesenen
Ownerworktree gelesen. Tatsächliche Desktop/PluginSession-Pfade im Adapterbaum
bestätigt: `<profile>/plugins`, `plugin-cache`, Worker-/Compiler-Appordner.

Managementowner bestätigte konkrete Plugins-/Editor-/Test-/Settings-/Rückfall-
und Rulegrantaktionen. Compilerowner bestätigte C#14/net10, keine impliziten
usings/NuGet/MSBuild/Generatoren/unsafe und Resource-/Fingerprintbedingungen.
SDKowner bestätigte neue RouteIdentifierKind-API samt optionalen init-Properties
BoardIdentifierKind/ThreadIdentifierKind und Default PortableSegment. Autoren-
doku folgt exakt dieser abgestimmten Erweiterung; kein SDKcode wurde geändert.

## Ausgeführte Checks

- 51 relative Links in den sechs neuen Seiten auf Existenz geprüft, Markdown-
  Zielanker geprüft: PASS. Ziele gegen eigenen Worktree, Root und die benannten
  Management-/Filterowner geprüft; `link-check.json` nennt Integrationsziele.
- 12 EN/DE-Zeilen (13 einzelne Aktionslabelpaare einschließlich Enable/Disable)
  exakt gegen Management `PluginCatalog.cs`: PASS.
- Nativer Compilerprobe-RESULT des Compilerowners gelesen und die beiden
  Source-SHA256 gegen aktuelle Lainchan.cs/Kohlchan.cs verglichen: beide identisch.
  Die Doku schreibt daher präzise Compiler-only/Linux-x64, kein volles E2E.
- Contractclaims gegen echte Typen/Defaults/Implementierung abgeglichen:
  SDKentry, Paths, Bundleimport, Resourceauswahl, State-/Grantrevisionen,
  Runtimecounts/deadlines, settings/filterbytes. Keine erfundenen GUIlabels.
- `git diff --check`: PASS.

Die in validation.md angeführten 48 Exampletests und die Linux-Compilerprobe
sind die bereits ausgeführten Nachweise der vorigen Rollenlieferung; im
Dokumentationsauftrag wurden sie nicht erneut gestartet oder zu Produkt-/
Plattformabnahme umetikettiert.

## Integrations-/Abnahmereste

Neue Links auf PluginCatalog.cs und ImageDimensionsFilter.cs benötigen die
Management-/Filterlieferung im Root. Weitere Host-/Compiler-/Official-Ziele
existieren bereits im Root, nur nicht im alten eigenen Workerbasisstand.
Die neue IdentifierKind-Dokumentation benötigt den gerade vom SDKowner
angekündigten APIcommit; dessen finale XMLdeklaration muss in Root enthalten sein.

Keine aktuelle GUIaufnahme, SDKpaketconsumer-Neuabnahme, komplette Autoren-
Durchlaufabnahme im frischen Profil, volle Quellen-/Filter-E2E, native
Releasepaketabnahme oder neue Release Notes in diesem Auftrag. Diese bleiben
Root/entsprechender Integrationsrolle. Öffentliche Doku behauptet keine
NuGetpublikation und keine vollständige R10-11-Abnahme. Keine privaten
Architekturordner oder lokalen persönlichen Profile werden öffentlich verlinkt.
