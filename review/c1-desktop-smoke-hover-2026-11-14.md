# C1 — Desktop-Smoke: deterministischer Command-Hover

**Status:** C1-Hover-Fix implementiert; das Abnahmeziel „normal 3× grün“ ist
noch **nicht erreicht**. Der erste vollständige Lauf scheitert nach bestandenem
Kontrastteil an einer separaten Reader-/Archiv-Prüfung (siehe unten).

## Scope und Basis

- Basis: `a8e9600454f1ff157d7cc6d0db260c9659ef6720`
  (`integration/ux-wave5-20261114` zum Übergabezeitpunkt).
- Worker-Branch: `codex/api-c1-smoke-hover-20261114`.
- Getesteter Implementierungscommit: `2f903f226a227d75bb783eaae004343a1ed3a605`.
- Nur `CommandButtonContrastChecks`, dessen Harness-Dokumentation und diese Evidence.
  Keine Produktänderung, keine Änderungen an root/goal, kein Push/Tag.

## Befund und Änderung

Der unveränderte Basis-Build war erfolgreich. Ein anschließender normaler Smoke
reproduzierte den Timeout an `CancelSettingsButton/command/pointerover`:
`over=False`, `pressed=False`, `enabled=True`. Der Hit-Test am Zielpunkt `(682,424)`
traf einen `TextBlock` unter genau diesem Button. Das Fenster war aktiv und
eingeschaltet, `openConfirmations` und `ownedWindows` waren leer.

Der bisherige Check erzeugte einen eigenen `MouseDevice` und schickte dessen
Raw-Ereignisse direkt an `IInputManager.ProcessInput`, unter Umgehung des nativen
Top-Level-Eingangs. Der Patch verwendet stattdessen den vorhandenen, abgeschirmten
`MediaNativeInput`-Harness (hier: XTest auf dem privaten Xvfb). Damit gehören
native Pointerposition und Avalonia-Eingabe zum selben Eingabepfad. Dies ist ein
Harness-Fix, **kein Nachweis eines Avalonia-Produktfehlers**.

- Vor dem Hover: Dispatcher/Layout abarbeiten und tatsächliches Hit-Ziel prüfen.
- Echte Bewegung und Press/Release, keine Zuweisung von `IsPointerOver`,
  `IsPressed` oder Pseudoklassen und kein ausgelöstes Fenster-`PointerMoved`.
- Bestehende Zustands-, Light/Dark- und Pixelkontrastprüfungen bleiben erhalten.
- Loslassen abseits des Kommandos; jeder unerwartete Klick lässt den Check scheitern.
- Keine Eingabe-Retry-Schleife und kein künstliches Erzwingen eines Hoverzustands.

Der erste gezielte Lauf mit nativem Input kam über die betroffenen Produktions-
Buttons (Add, Save, Cancel, SaveRule) hinaus. Er deckte anschließend eine veraltete
Annahme **im selben Check** auf: `ArchiveFavoriteButton` benutzt auf der Basis
bereits `FavoriteStar` mit einem Vektor-`Path`, nicht mehr einen OpacityMask-Border.
Die Pixelsuche berücksichtigt nun diesen tatsächlichen Produktionsinhalt; die
markierte Variante wird gegen `Workspace.Favorite`, die unmarkierte gegen den
geerbten Vordergrund geprüft. Keine Umgestaltung oder Ersetzung des Produktinhalts.

Ein weiterer gezielter Lauf wies den Mittelpunkt dieser speziellen Stern-Fixture
als nicht auf den Button hit-testbar zurück. Für sie wird deshalb ausdrücklich
nur der echte Inhalt in markiert/unmarkiert × normal/disabled geprüft, ohne
künstlichen Hintergrund, Ersatz-Template oder Hit-Test-Schalter. Ihre native
Hover-/Press-Akzeptanz ist **nicht** Teil dieses Checks und wird im Smoke-Log als
Coverage-Grenze ausgegeben. Die betroffenen Produktionskommandos und der normale
Command-Toggle behalten alle vier Zustände inklusive echtem Hover/Press. Die
unveränderte Basis scheiterte bereits früher am Command-Hover; eine native
Akzeptanz für den Spezialstern wird hier nicht behauptet.

## Validierung

- Basis-Build: erfolgreich, 0 Fehler / 30 vorhandene Warnungen.
- Abschließender Release-Konfigurations-Build des Smoke-Projekts:
  erfolgreich, 0 Fehler / 26 vorhandene Warnungen; keine neue Paketabfrage.
- `MEOWCHAN_COMMAND_CONTRAST_ONLY=1`: grün; 56 Pixel-/Zustandsmessungen,
  je Light und Dark, inklusive Save/Cancel/SaveRule mit echtem Hover und Press.
- Vollständiger `normal`-Lauf 1: **rot**, aber alle 56 Kontrastmessungen bestanden.
  Danach `R08dPresentationChecks.cs:210`, über
  `PresentationChecks.RunPreferencesAsync`, mit
  `The reader's visible OS full-screen action is missing.` Kein Timeout und
  keine Binding-Fehler. Der Reader-/Archiv-Check und Produktcode wurden nicht
  verändert. Seine genaue Ursache bzw. ein eigenständiger Basis-Rot-Nachweis
  bleiben offen; der Fehler wird nicht dem Pointer-Fix zugeschrieben oder
  als Produktregression ausgegeben.
- Die normalen Läufe 2/3 wurden nach diesem Fehler nicht gestartet: kein
  Wiederholen bis Grün und keine Umgehung der fehlgeschlagenen Assertion.
- Zwei zusätzliche gezielte Kontrastläufe: **beide grün**. Damit drei gezielte
  Kontrastläufe mit demselben Build grün, jeweils alle 56 Messungen und
  erwartete native Hover-/Pressed-/Enabled-Zustände. Das ist ausdrücklich
  **kein Ersatznachweis für drei grüne vollständige normale Läufe**.
- Offline-Auswertung aller vier Abschlussreports: Szenario-/Exitcode-/
  Timeout-Prüfung sowie Vollständigkeit und Zustände aller 56 Matrixeinträge
  je Lauf bestanden.

Alle Prüfungen: Linux, tatsächliches RenderScaling 1.0, privater Xvfb
(3200×2000), Openbox, privater DBus, Software-Rendering. Der Runner verlangt die
exakten Szenario-IDs (`normal+MEOWCHAN_COMMAND_CONTRAST_ONLY` bzw. `normal`).

Rohlogs, Reports und PNGs liegen unter:
`/home/tomka/.cache/claude-ssd-tmp/codex-wave-20261114-1625/api-c1-smoke-hover/validation/`
mit `build-baseline.log`, `baseline-normal/`, `build-native.log`,
`native-command-contrast/`, `build-final.log`, `final-command-contrast/`,
`build-verified.log`, `verified-command-contrast/`, `verified-command-contrast-2/`,
`verified-command-contrast-3/` und `verified-normal-1/`.
Die beiden früheren gezielten roten Varianten sind erhalten, nicht überschrieben.

SHA-256 der Abschluss-`results.json`-Reports:

- `verified-command-contrast`: `3ab421867793cf2eb42d9fe512b2a06ef1d4c3574a1665590c75659e1f1b03e6`
- `verified-command-contrast-2`: `b85dfc7b3a5b3c0187a78511afa419cca6c8563d9f8b7ff63f750576ae8b9a4f`
- `verified-command-contrast-3`: `db4a9ec6d60c69342816ba9cd8836c3572c906e9d16b4463ad6ebe31aec3a629`
- `verified-normal-1`: `9eb32b6caef18a48e74d65d41250d5de18035826ebc32c1e613856375604c68f`

Codebindung des abschließenden Builds, SHA-256 von
`tests/MeowChan.DesktopSmoke/CommandButtonContrastChecks.cs`:
`7a1d7148ed006e4bd2197392e94a519daf937a8e61e14083726ac07ff20f81d2`.

SHA-256 der geprüften Smoke-Assembly:
`bbb88c04bae6662621dcd99c64fac5f71d0b67c5f3ba2137dc695a81eb10cc47`.

Reproduktionskern (in privater HOME/TMP/XDG-Umgebung und gemeinsam unter dem
Heavy-Lock; nach Restore mit leerer privater NuGet-Quellenkonfiguration):

```sh
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj \
  -c Release --no-restore -p:NuGetAudit=false -p:UseSharedCompilation=false \
  -m:1 -nodeReuse:false
MEOWCHAN_COMMAND_CONTRAST_ONLY=1 python3 scripts/run-desktop-smoke.py \
  --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll \
  --output "$FRESH_OUTPUT/contrast" --mode normal \
  --expect-scenario normal+MEOWCHAN_COMMAND_CONTRAST_ONLY --timeout 120
python3 scripts/run-desktop-smoke.py \
  --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll \
  --output "$FRESH_OUTPUT/normal" --mode normal --expect-scenario normal --timeout 240
```

## Grenzen

Source-built Avalonia-Harness unter Linux, privater Xvfb/Openbox/DBus und Software-
Rendering, nicht veröffentlichter Apphost, GPU- oder Windows/macOS-Abnahme.
Keine Live-Providerrequests; synthetische Fixtures und private Profile. HOME,
TMP/TEMP/TMPDIR, DOTNET_CLI_HOME und XDG-Verzeichnisse liegen im privaten Worker-
Validierungsverzeichnis. NuGet nutzt ausschließlich den vorhandenen Paketcache
mit leerer privater Quellenkonfiguration und ausgeschaltetem Audit beim Build.
Alle Builds und Smokes sind durch
`flock /home/tomka/.cache/claude-ssd-tmp/heavy.lock` serialisiert. Vorhandene
Build-Warnungen sind keine Release-Freigabe. Kein Release-Readiness-Claim.
