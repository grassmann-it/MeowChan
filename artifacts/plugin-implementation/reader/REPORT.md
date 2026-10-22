# Readernavigation — Umsetzung am 22.10.2026

Basis: `dc053f8`; Produkt-/Prüfcommit: `8c918fc`. Isolierter Worktree `/home/tomka/Desktop/oChan-1.0-workers/reader`.
Umfang: ausdrücklich gewünschter kompakter Reader-Navigationsbereich unten rechts.
Keine zusätzlichen Ideen des parallelen UI-Reviews umgesetzt.

## Änderung

- Das obere 66-DIP-Pfeilkreuz entfällt. Die bestehende obere Toolbar misst jetzt
  eine Zeile mit 32 DIP, auch bei den geprüften B/S/A-Mindestgrößen in EN/DE.
- Die vier bestehenden Buttons erscheinen als zwei flache Paare ↑↓ und ←→
  in einer 154 × 42 DIP großen, themeabhängigen Fläche unten rechts.
  Commands, lokalisierte Tooltips/Automation, native Buttons, Aktivierung,
  Tastatur-/Dialogschutz und deaktivierte Endzustände bleiben erhalten.
- Im breiten Reader bleibt die bestehende maximale Postbreite 1120 DIP erhalten.
  Freier Seitenraum nimmt die Navigation auf; die Lesehöhe bleibt vollständig
  verfügbar. Rechts werden 154 DIP Navigation + 24 DIP Außenabstand + 8 DIP
  Mindestabstand zum Inhalt berücksichtigt.
- Bei kleineren Fenstern wird ein 42-DIP-Band außerhalb des Scrollviewports
  reserviert. Auch gescrollte Posts können nicht unter den Buttons liegen.
  Die Navigation sitzt 8 DIP oberhalb der vorhandenen, getrennten Pagebar.
  Ein bloßer Spacer am Listenende wird nicht verwendet.
- Die Navigation bleibt ausschließlich in der großen Readeransicht sichtbar,
  dort auch auf Medien/Verweisen. Der kompakte Archivreader erhält weder die
  Navigation noch reservierte Ränder. Detach/Attach aktualisiert dies explizit.
- Export bleibt über der Navigation (vorhandener ZIndex 20); echte Hit-Tests
  bestätigen, dass der Dialog Pointereingaben vor den Pfeilen erhält.

Geänderte Produktdateien: `ArchivePage.axaml`, `ArchivePage.Reader.cs`.
Prüfungen: neue `ReaderNavigationOverlayChecks.cs` sowie gezielter Aufruf und
Rückkehrprüfung in `ReaderUxChecks.cs`.

## Lesefläche und visuelle Prüfung

| Zustand bei 1× | Post-Viewport | Toolbar | Reservierung |
| --- | --- | --- | --- |
| Simple 1440 × 920 | 1120 × 620 DIP | 32 DIP | vorhandener Seitenraum |
| Simple/Advanced 760 × 500 | 718 × 173 DIP | 32 DIP | 42 DIP unten |
| Barebones 640 × 480 | 598 × 153 DIP | 32 DIP | 42 DIP unten |

Der vorhandene Vorher-Beleg `gui-reader-01/.../smoke-reader-initial.png`
zeigt bei 1440 × 920 den Postbereich von y238 bis y824, also 586 DIP.
Der neue gemessene Bereich reicht von y204 bis y824: **34 DIP mehr Lesehöhe**.
Bei engem Fenster beträgt die aus Toolbar- und Bandmaßen abgeleitete Bilanz
34 − 42 = −8 DIP; die tatsächlichen neuen Minima sind oben gemessen und liegen
über der vorhandenen 100-DIP-Anforderung. Es gibt dort keinen permanenten
rechten Inhaltsstreifen. Breiten unmittelbar unter/über der Umschaltschwelle
(1346 und 1350 DIP Fensterbreite) sind eigenständige Geometriefälle.

Vorher-Beleg:
`/home/tomka/Desktop/oChan/artifacts/ui-implementation/integration/gui-reader-01/1-reader-ux-1/reader-ux/smoke-reader-initial.png`.
Die endgültigen Nachher-Bilder liegen unter den nachstehenden GUI-Läufen;
Screenshots warten auf dekodierte Original-Fixturebilder. Keine Bildbearbeitung.

## Ausgeführte Prüfungen

Alle Builds und GUIläufe benutzen `/tmp/meowchan-ui-1.0-build.lock` mit
`flock --close`. Der vorhandene Runner startet ausschließlich privates
Xvfb/Openbox/DBus und isolierte temporäre Profile; kein persönlicher Desktop,
kein persönliches Profil, keine Live-Provideranfragen.

Releasebuild (gelocktes Restore, anschließend `--no-restore`):

```sh
flock --close /tmp/meowchan-ui-1.0-build.lock env MSBUILDDISABLENODEREUSE=1 \
  dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj \
  -c Release --no-restore -nodeReuse:false --nologo -v:minimal
```

Ergebnis: 0 Warnungen, 0 Fehler.

GUI-Endstand auf dem unveränderten Code-/Prüfstand von `8c918fc`:

| Skala | Ergebnis | Artefaktordner unter diesem REPORT |
| --- | --- | --- |
| 1× | PASS, vollständiger `reader-ux` | `gui-final-05-scale1/` |
| 1.5× | PASS, vollständiger `reader-ux` | `gui-final-06-scale15/` |
| 2× | PASS, vollständiger `reader-ux` | `gui-final-07-scale2/` |

Je Lauf sind `results.json`, `reader-ux/output.log` und echte PNGs erhalten.
Es gab keine Bindungsfehler; normales Schließen gab jeweils die Profilsperre
frei. `git diff --check` ist ebenfalls sauber. Reproduktion (Skala und neuen
Ausgabeordner entsprechend wählen):

```sh
flock --close /tmp/meowchan-ui-1.0-build.lock python3 scripts/run-desktop-smoke.py \
  --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll \
  --mode reader-ux --scale 1 --output artifacts/plugin-implementation/reader/new-run
```

Ausgewählte finale Nachher-Bilder (absolut im Workerartefaktordner):

- `gui-final-05-scale1/reader-ux/smoke-reader-navigation-simple-1440-en-dark.png`
- `gui-final-05-scale1/reader-ux/smoke-reader-navigation-barebones-640-de-light.png`
- `gui-final-06-scale15/reader-ux/smoke-reader-navigation-simple-1440-en-dark.png`
- `gui-final-07-scale2/reader-ux/smoke-reader-navigation-barebones-640-de-light.png`

Die Dateien wurden direkt aus dem echten Avaloniafenster aufgenommen; breite
und schmale Fassungen wurden visuell angesehen. Der Endstand umfasst insgesamt
48 Bildfälle der neuen Matrix sowie sechs zusätzliche Schwellenfälle über
alle drei Skalen.

Der komplette bestehende `reader-ux`-Pfad prüft außerdem unter anderem V02
(native Pfeilbereiche), V15 (Readerkürzel/Inputs/Dialogschutz), V18
(Zitat-/Historyanker), Postseitengrenzen, eingefrorene Threadreihenfolge,
Galerierückkehr, Fokus/Tab-Zyklus, Export in Mindestfenstern und normales
Schließen mit freigegebener Profilsperre. Die neue Matrix ergänzt je Skala
16 EN/DE-/Light/Dark-/Größenfälle, jeweils vor und nach Scrollen zum Post1003,
zwei Umschaltbreiten, Seiten-/Scrollbarkollisionen, vier benannte Buttons,
sekundäre Tabs im Mindestfenster und die Dialog-Hitprüfung.

## Unabhängige Nachprüfung

`ux_reader` hat beide endgültigen 1×-Bilder (1440/EN/Dark und 640/DE/Light)
unabhängig tatsächlich angesehen und den engen visuellen Umbau als PASS
bewertet: ruhige getrennte Pfeilpaare, vollständige Headeraktionen, keine
Überdeckung von Inhalt/Scrollbar/Pagebar. Das schmale Unterband wurde als
vertretbare responsive Lösung bestätigt. Zusätzlich wurden gezielter
Readercode und neue Prüfungen gegengelesen; keine visuelle Blockade gefunden.
Diese Bildprüfung behauptet keine native Fokus-/Screenreaderabnahme.

## Frühere Zwischenstände

- `gui-flat-01`: alle 16 Layoutfälle bestanden; der neue Dialog-Hittest wurde
  vor der ersten gerenderten Dialogfläche ausgewertet und schlug fehl. Die
  Prüfung wartet nun auf Layout und tatsächlichen Dialog-Hit. Keine Assertion
  oder Timeoutgrenze gelockert. Dieser Lauf ist ausdrücklich kein PASS.
- `gui-adaptive-02`: kompletter `reader-ux` bei 1× PASS.
- `gui-adaptive-03-scale15`: kompletter `reader-ux` bei 1.5× PASS.
- `gui-final-04-scale2`: kompletter `reader-ux` bei 2× PASS; vor dem abschließenden
  8-DIP-Mindestabstand direkt an der Umschaltschwelle.

## Grenzen

Linux-Quellbuild mit Avalonia und privatem Openbox/Xvfb; keine native
KWin-/Windows-/macOS-/Screenreader- oder veröffentlichte Paketabnahme.
Solche Gesamtfreigaben bleiben beim Integrator. Für diesen engen Produktwunsch
besteht nach den abgeschlossenen Läufen kein Implementierungsrest.
