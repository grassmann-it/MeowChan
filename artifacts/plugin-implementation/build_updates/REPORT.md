# build_updates — R10-15-Anschluss und Settings

Code: c8797ae → 5424529 → 04c5d7e → 8a8e058, auf dc053f8 im isolierten Worktree
`/home/tomka/Desktop/oChan-1.0-workers/updates`.

## Lieferumfang

- Bestehenden UpdateService/UpdateStateStore mit einer profileigenen UpdateSession
  an die reale DesktopSessionHost-Lebensdauer angeschlossen: Cache beim Start,
  erste Hintergrundprüfung nach 30 Sekunden, weitere tägliche Prüfung;
  existierende 24h-Drossel bleibt maßgeblich. Abbruch/Drain vor Profilfreigabe.
- Standard automatisch an, Vorabversionen aus; vollständige Config-Clone-/Copy-/
  Settings-Merge-/JSON-Pfade. Manuell bleibt bei deaktivierter Automatik möglich.
  Distribution-Override erklärt in Settings/About und unterdrückt Requests/Hinweis.
- Kleiner Versionslink in der Statuszeile in B/S/A; manueller App-/Hilfemenüeintrag,
  About-Abschnitt mit letzter Prüfung, Ergebnis/Fehler, Release Notes, Download,
  Überspringen. Ausschließlich bestehender Browserlauncher, kein Installer.
- EN/DE im separaten UpdateCatalog. Automatische Fehler ausschließlich im Log;
  manuelle Fehler im About-Abschnitt, einschließlich laufendem Sprachwechsel.
- Portable Release-RIDs aus Betriebssystem/Prozessarchitektur: Manjaros .NET
  meldet `arch-x64`; damit wird jetzt korrekt das eindeutige `linux-x64`-Asset
  gewählt. Mehrdeutige/fremde Assets behalten den bestehenden Releasefallback.
- Downloadsreserve 1 GiB (>=0) und vier Advanced-Pluginlimits mit Defaults und
  validierten Bereichen in Settings/Config. Pluginlimits weisen ausdrücklich
  auf Anwendung beim nächsten Programmstart hin. Runtime-/Queueanschluss besitzt
  adapter, Vertrag ist mit budgets/runtime abgestimmt.
- Plugin-Editor-Executable/Argumentarray in Config für management; tiefe Kopien,
  begrenzte Validierung. Ein alter allgemeiner Settingsdraft überschreibt keine
  zwischenzeitlichen direkten Editoränderungen der Pluginseite.

## Abgestimmte Integrationsstellen

- adapter liefert die wirklichen DesktopSessionHost-Hooks in 039b776, einschließlich
  injizierbarer UpdateSession für Tests, Start nach Ready, Configure nach Apply,
  Dispose vor Startupdrain/Profilfreigabe und Downloadsreserve.
- management hängt UpdateCatalog.Entries in beide BuiltInCatalog-Sprachen,
  ruft InitializeUpdates()/DisposeUpdates() in Workspacepages auf und setzt
  `_settingsEditor.Updates = _session.Updates`.
- Root: MainWindow.axaml.cs OnAboutMenuItemClick verwendet
  `new AboutWindow(_session.Updates)`; Standard-Smokerunner setzt
  `MEOWCHAN_DISABLE_UPDATE_CHECK=1`, damit andere lange Tests keine echte
  GitHub-Anfrage auslösen. Die Updatefixture injiziert ihren eigenen Transport.
- Lokale äquivalente Hooks für diese Prüfungen liegen ausschließlich als
  `isolated-test-integration.patch` vor; sie wurden nicht als fremde Ownerdateien
  mitcommittet. Root soll die genannten Ownercommits integrieren, nicht den
  Testpatch zusätzlich auf den neuen Host anwenden.

## Ausgeführte Prüfungen

- Alle Restore-/Build-/Test-/GUIbefehle global seriell über
  `/tmp/meowchan-ui-1.0-build.lock`, Release, ohne Buildserver-Reuse.
- Locked Restore der bestehenden DesktopTests-/DesktopSmoke-Projekte PASS.
- DesktopTests `Update|ConfigTests|Localization`: 184 PASS, 0 FAIL, 0 SKIP.
  `unit-tests.log` enthält den ausgeführten Lauf. Enthält bestehende 63
  Updatebackendfälle und neue Tests für echte Profillebensdauer, Start-/Tagesuhr,
  Cache/Skip/Neustart, Settings/Prereleases, automatische/manuelle Fehler,
  Env-Override, HTTP-Abbruch vor Leasefreigabe und portable RID-Auswahl.
- DesktopSmoke-Releasebuild: 0 Warnungen, 0 Fehler (`build.log`).
- GUI läuft ausschließlich auf privatem Xvfb/Openbox/DBus mit isoliertem Profil,
  HttpMessageHandler-Releasefixture und selbst erzeugtem Fake-Firefox. Keine
  echte GitHub-Anfrage, kein echter Browser. Einstieg ist vorhandenes
  `--mode about-links`; UpdateGuiChecks ergänzt diesen gezielt.

- Finales GUI: `gui-100-verified`, `gui-150-verified` und `gui-200-verified`
  jeweils PASS, tatsächliche RenderScaling 1 / 1.5 / 2 laut results.json.
  Je 12 tatsächliche EN/DE × Dark/Light × Barebones/Simple/Advanced-Zustände,
  B 640×480 und S/A 760×500 DIP. Notice-Bounds und zugänglicher Name geprüft;
  About-Status und geerbtes Theme geprüft. 30 Update-PNGs je Skalierung.
- Echte Fixturebrowserübergabe für Release und eindeutiges Linuxasset,
  Settingsdraft ohne Liveänderung, Verwerfen, Speichern/Profilreload,
  manueller 403-Fehler mit laufendem Sprachwechsel, Skip-Persistenz und
  Umgebungsdeaktivierung geprüft. Die automatische Fehlerstille wird separat
  im Unitfixture mit tatsächlich fehlschlagender HTTPantwort geprüft.
- Repräsentative finale EN/Dark- und DE/Light-Bilder von About, Status und
  gescrolltem Update-Settingsabschnitt visuell gesichtet; auch 1.5×-Status
  geprüft. Die abschließenden Captures zeigen die behaupteten Sprachen/Themes.
- Kontrollierter Parallelfall bestätigt: manueller Klick hinter einem laufenden
  Auto-HTTPrequest setzt sofort Busy; weitere Klicks erzeugen keinen dritten
  Request. Unit-Testprozess deaktiviert Produktnetzwerk per ModuleInitializer;
  explizite Updatefixtures behalten ihren injizierten Offline-Transport.

## Nachweisgrenzen

- Native WM-/Accessibility-, Windows-/macOS- und Paket-/Apphostabnahme bleiben
  Integratoraufgabe. Diese Prüfung ist ein quellgebauter Avalonia-Lauf.
- Finale Plugin-/Queuewirkung der neuen Budgetwerte gehört zu adapter/runtime/
  budgets und wurde hier nicht durch reine Configtests als Runtimepass gewertet.
- Frühere GUIläufe sind Diagnoseartefakte: zuerst unpassender Fixtureassetname,
  dann der tatsächliche Arch-RID-Befund; anschließend ein globalprofilabhängiger
  Testhelper sowie eine durch Moduswechsel zurückgesetzte Theme-/Sprachmatrix.
  Diese wurden korrigiert, keine Assertion oder Wartefrist abgeschwächt. Nur
  abschließende Läufe mit expliziten Ist-Sprache-/Ist-Theme-Assertions zählen.

## Reproduktion

```sh
flock --close /tmp/meowchan-ui-1.0-build.lock env -u MEOWCHAN_DISABLE_UPDATE_CHECK MSBUILDDISABLENODEREUSE=1 dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -c Release --no-restore -nodeReuse:false --filter 'FullyQualifiedName~Update|FullyQualifiedName~ConfigTests|FullyQualifiedName~Localization'
flock --close /tmp/meowchan-ui-1.0-build.lock env MSBUILDDISABLENODEREUSE=1 dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj -c Release --no-restore -nodeReuse:false
flock --close /tmp/meowchan-ui-1.0-build.lock env MEOWCHAN_DISABLE_UPDATE_CHECK=1 MSBUILDDISABLENODEREUSE=1 python3 scripts/run-desktop-smoke.py --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll --mode about-links --scale 1 --timeout 90 --output artifacts/plugin-implementation/build_updates/gui-100-verified
```

Die GUIbefehle wurden zusätzlich mit `--scale 1.5` und `--scale 2` und den
oben genannten getrennten Ausgabeordnern ausgeführt. Keine Wartefrist wurde
bei einem Fehler erhöht. Der Timeout 90 gehört zum gesamten kombinierten
About-/Update-Szenario; die bestehenden Einzelzustandswartefristen bleiben 5 s.

Uncommittete Arbeitskopieänderungen beschränken sich nach Lieferung auf die vier
zentralen lokalen Testhookdateien des beigefügten Patches; deren Owner liefern
stattdessen die tatsächliche integrierte Variante. Keine fremde Änderung ist
in den vier selektiven Codecommits enthalten.
