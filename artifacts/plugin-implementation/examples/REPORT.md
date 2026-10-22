# build_examples — Lainchan / Kohlchan

Datum: 2026-10-22. Worktree: `/home/tomka/Desktop/oChan-1.0-workers/examples`,
Branch `impl/1.0-wave2-examples`. Startbasis `dc053f8`. Auftrag/AGENTS und
Architekturkontext gelesen, R10-01/04/13 und Pluginvertrag berücksichtigt.
Kein vollständiger Paketabschluss R10-04/R10-13 behauptet.

## Lieferung

Je ein editierbarer vollständiger SDK-only Drop-in mit genau einem öffentlichen
IPlugin-Einstieg, eigenem vorbaubarem net10.0-Projekt und Release-Lock:

| Quelle | SourceId / PluginId | Datei / Typ / Output |
|---|---|---|
| Lainchan | `lainchan` / `meowchan.source.lainchan` | `plugins/Examples/Lainchan/Lainchan.cs`; `MeowChan.Plugins.Examples.Lainchan.LainchanPlugin`; `MeowChan.Plugin.Lainchan.dll` |
| Kohlchan | `kohlchan` / `meowchan.source.kohlchan` | `plugins/Examples/Kohlchan/Kohlchan.cs`; `MeowChan.Plugins.Examples.Kohlchan.KohlchanPlugin`; `MeowChan.Plugin.Kohlchan.dll` |

Projektnamen entsprechen Assemblies, Ausgabe je `bin/Release/net10.0/`.
Keine zentrale Projekt-/Solution-/Core-/Desktopänderung. Keine zusätzlichen
Pluginsettings. Keine Library-/Avalonia-/Hostimplementierungsreferenzen. Voller
Drop-in benötigt keine zusätzliche .cs-Datei. Beschreibungen kennzeichnen beide
als Beispielquelle und erläutern Grenzen, ohne Liveproduktunterstützung zu behaupten.

Thread- und explizite Boardkatalogabrufe laufen nur über HostHttp mit einer
Anfrage pro Operation. Host-Deferred/Failure bleiben erhalten. Statische,
belegte HTTPS-Hosts und HTMLrouten, canonicalisierte Threadreferenzen, geordnete
Mehrfachanhänge, Presence je Feld, HTML/PlainText, Datums-/Namens-/Dateimetadaten.
DecimalPostId, Ordinal=0. MediaId `postId-position` (z.B. `123-0`), Variant
`original`, unabhängig von Dateiname/URL; in Absprache mit Adapter pfadverträglich.
Keine globalen Boardlisten oder Previewfähigkeit. Katalogcoverage konservativ
false, keine stille Pagination-/Vollständigkeitsbehauptung.

304 verlangt akzeptierten Snapshot und HTTPvalidator; unveränderte Antwort
referenziert genau den akzeptierten Validator. Neue Validatoren nur in geparsten,
SDK-validen Snapshots. Nur 404 des exakt angefragten Threadendpunkts bedeutet
NotFound, auch ohne Baseline. 410 ist nicht belegt und wird nicht terminalisiert.
Lainchan locked/cyclical/erfundenes archived bleiben Active. Kohlchan archived
ist im LynxChan-Dokument separat belegt und transportiert den finalen Snapshot;
locked/cyclic/autoSage/trash bedeuten kein Ende/Löschen. Ungültige JSONformen,
Status206, fremde Medienpfade, Übergröße und Sammlungslimits sind Fehler, keine
abgeschnittenen erfolgreichen Snapshots.

## Tatsächlich ausgeführte Quellenprüfungen

Zuerst vorhandene `/home/tomka/Desktop/oChan-1.0.0/artifacts/r10-source-evidence`
gelesen; relevante datierte Belege in Lainchan/Evidence übernommen. Zusätzlich
neun kleine GETs insgesamt (zwei Lainchan, sieben Kohlchan/Dokumentation),
256-KiB-Grenze plus ein Sentinelbyte, keine Cookies/Redirects/automatischen
Retries/Anmeldungen/Medienbytes. Keine fremde Ersatzquelle.

- Lainchan: offizieller APIcode und bekannte Thread-HTMLseite200 bestätigen
  `tim`-Dateitoken inklusive Bindestrichsuffix und `/<board>/src/<tim><ext>`.
  Frühere Katalog-/Thread-/ETag304-/404-Belege bleiben als solche datiert.
- Kohlchan: Homepage200 nennt LynxChan2.8.5 und verlinkt Board-/Threadrouten;
  kleiner dort verlinkter `/int/`-Thread200 (1309 Bytes) und echtes
  If-Modified-Since304. OP-Objekt plus replies-array und same-origin
  `/.media/<file>` beobachtet. Synthetische fehlende Thread-ID0 liefert404.
- Kohlchan `/cyber/catalog.json` und `/bus/catalog.json`: HTTP200 JSON, aber
  jeweils >256KiB und daher bewusst keine erfolgreiche Vollparserbeobachtung.
  Katalogform stammt aus der bereits im Projekt zitierten archivierten
  LynxChan-Forkdokumentation; synthetische Katalogfixtures prüfen sie.
- Doku-Moving-Branch/Fork ist kein Nachweis der eingesetzten Serverrevision.
  Belege speichern Status/Headers/Strukturen/Hashes und kleine Code-/Dokuauszüge,
  keine realen Posttexte/Namen/Originaldateinamen/Medien. Originalbodies sind
  nicht archiviert; deren Hash ist kein unabhängiger Rekonstruktionsnachweis.

## Prüfungen

Alle Restore-/Build-/Testaufrufe mit globaler Sperre:

```sh
flock --close /tmp/meowchan-ui-1.0-build.lock env MSBUILDDISABLENODEREUSE=1 dotnet restore tests/MeowChan.ExamplePlugins.Tests/MeowChan.ExamplePlugins.Tests.csproj -p:Configuration=Release --locked-mode -nodeReuse:false
flock --close /tmp/meowchan-ui-1.0-build.lock env MSBUILDDISABLENODEREUSE=1 dotnet test tests/MeowChan.ExamplePlugins.Tests/MeowChan.ExamplePlugins.Tests.csproj -c Release --no-restore -nodeReuse:false --logger 'console;verbosity=minimal'
```

Locked-mode Restore PASS. Finaler Release-Testlauf gegen den übernommenen SDKfix:
**48/48 PASS, 0 Fehler, 0 übersprungen, keine Buildwarnungen.** Beide
Plugin-DLLs wurden dabei tatsächlich gebaut; `git diff --check` PASS.
Die Testassembly führt öffentliche Pluginaufrufe mit deterministischem HostHttp
und echten SDK-DTO-Validierungen/JSONroundtrips aus: beide Quellen, mehrere Dateien,
Text-only, partielle/fehlende/empty/null Felder, stabile IDs bei URL-/Namenswechsel,
Unicodeboard/kanonische URLs, doppelte Post-/Thread-IDs, falsche Ressource,
terminale Kohlchanfassung, Lainchan nichtterminale Flags,304 mit/ohne Baseline,
404 ohne Baseline, Status206/403/410/429/503, Hostdeferred/-failure, ungültiges JSON,
negative Größen, fremde/traversierende Medienpfade, Body-/Anhanglimits,
unsupported Pagination und Cancellation.

SDK-Owner-Fix `7813465a4cd64924b6956ab17b8b885f2e86d0e1` nach Freigabe
übernommen als lokale Testabhängigkeit `ce92313`. **Nicht erneut liefern/cherry-picken:**
Root besitzt den Original-SDKcommit bereits. Eigener Liefercommit enthält nur
Examples, eigenen Testbaum und diesen Bericht.

## Offene Produktintegration und konkrete Grenzen

1. Root bindet Projekte in Solution/Paketgraph ein und schließt `plugins/**` aus
   dem Desktop-Compileglob aus. Gebaute DLL und editierbare .cs gehören gemeinsam
   ins Apppaket. Keine Buildartefakte wurden committed.
2. Tatsächlicher Compiler → Worker → Registry → Queue → Store → frischer Prozess/
   Reader/Galerie/Export bleibt mit Runtime/Compiler/Adapter durchzuführen.
   Wiederholte Polls, Boardneuzugang, Capture-/Regeln, Medienfehler/-nachlauf,
   Cachetreffer, Syntaxfehler/erklärte Unverfügbarkeit und Offlineerhalt sind durch
   diese Parsertests **nicht** abgenommen. IDs/Typen/Pfade wurden früh gemeldet.
3. Kein echter Medienabruf, kein produktiver ganzer Thread-/Kataloglauf. Voller
   Kohlchan-Livekatalog blieb innerhalb der Beleggrenze unbestätigt. Kein Live-
   Archived-Übergang und keine historische Löschung wurden beobachtet.
4. Threadcoverage ist im aktuellen SDK/Core nicht eigens darstellbar. Nach
   Abstimmung mit SDKowner API unverändert: fehlende Posts/Felder löschen nie,
   Lainchan omitted_posts>0 bleibt eine partielle Beobachtung, kein Vollständigkeits-
   oder Endsignal. Es wurde kein zusätzliches Architekturfeld eingeführt.
5. `PostOrderKind.DecimalPostId` muss im Host/Core generisch anhand der
   SourceDescription gelten; keine IDspezialbehandlung für diese Beispiele.
   Dies wurde mit dem Adapterowner besprochen; dessen Integration ist getrennt.
6. Providerslot-IDs können upstream umsortierte Dateien nicht von Ersatz im
   selben Slot unterscheiden; die geordnete APIform enthält keine unabhängige
   beständige Anhangkennung. Änderungen bleiben unter Host-Medienrevisionen.
