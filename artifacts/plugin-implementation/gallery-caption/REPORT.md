# Galeriecaption — enger Folgefix, 22.10.2026

Worktree: `/home/tomka/Desktop/oChan-1.0-workers/reader`.
Produkt-/Prüfcommit: `9a6c4ae` (auf der bereits separat gelieferten Readerarbeit).
Auftrag nach der abgeschlossenen Readerlieferung: den belegten Übergriff der
angehobenen Videocaption auf Postkontext und „Show in thread“ zunächst zur
Laufzeit nachweisen, dann gezielt beheben. Keine weiteren Reviewvorschläge
V36–47 oder allgemeine Galerieneugestaltung umgesetzt.

## Vorher-Nachweis

Der erste neue Lauf erfolgte mit unverändertem `Desktop/GalleryViewer.cs`:
`before-01-scale1/video-contrast/output.log`, mit erwarteter Fehlerabnahme.

- Echte synthetische VP8-Datei, verifizierter Archivpfad, produktiver
  LibVLC-/SoftwareVideoSurface-Decoder: Zustand Playing, Time 267 ms,
  21 dekodierte Frames, 5 angezeigte Frames, 4 Softwareoberflächen-Frames
  und unterschiedliche Bildpixel.
- 850 × 530, Light, Kontext offen: Caption `x10..840, y281..343`, Postkontext
  `x530..850, y68..406`. Caption schneidet sowohl Kontext als auch die
  „Show in thread“-Schaltfläche. Gemessener TransportBottomInset: 128,8 DIP.
- Der tatsächliche Vorher-PNG zeigt verdunkelten Posttext und Button:
  `before-01-scale1/video-contrast/smoke-gallery-caption-video-light-850-True-normal.png`.
  Der neue Geometriecheck schlägt exakt auf diese Überdeckung fehl.

## Änderung

`UpdateInfoOverlayPosition` begrenzt bei offenem, geteiltem Kontext die rechte
Captionkante auf den tatsächlich angeordneten Medienbereich. Die bestehende
vertikale Reservierung für den Videotransport bleibt erhalten. Größenänderungen
des Medienviewports aktualisieren auch diese Captionposition.

Kompakter Kontext blendet den Medienbereich wie bisher aus und behält seine
vorhandene untere Captionreservierung. Native Windows-Videooverlays besitzen
bereits ein eigenes Medienfenster; der Fallback besitzt eine separate Zeile.
Diese beiden Fälle erhalten keine doppelte rechte Einrückung. Neue Modi,
Controls, Tastenkürzel, Zielmengen und Medien-/Zoomzustände werden nicht eingeführt.

Bei derselben 850×530-Szene endet die Caption jetzt bei x512, innerhalb des
Medienviewports bis x522 und mit Abstand zum Kontext ab x530. Posttext und
„Show in thread“ bleiben frei. Der Transportabstand ist unverändert.

Produktdiff: ausschließlich `Desktop/GalleryViewer.cs` (13 geänderte Zeilen).
Gezielte neue `GalleryCaptionChecks.cs`, aufgerufen aus vorhandenen
`VideoContrastChecks.cs` und `ReaderUxChecks.cs`. Keine neue Harness-Betriebsart
oder Änderung an `Program.cs` erforderlich.

## Prüfumgebung und echte Mediendatei

Alle Builds/GUIläufe seriell über `/tmp/meowchan-ui-1.0-build.lock` mit
`flock --close`, Releasebuild und `MSBUILDDISABLENODEREUSE=1` /
`-nodeReuse:false`. Build: 0 Warnungen, 0 Fehler. Der vorhandene Runner nutzt
privates Xvfb/Openbox/DBus und temporäre Profile; kein persönliches Profil oder
Display, kein Providerabruf.

Video ist ein lokal gekürzter Ausschnitt der eingecheckten synthetischen
CC0-Fixture `tests/Fixtures/gallery-playback-vp8.webm`; Provenienz und Originalhash
stehen in `tests/Fixtures/gallery-playback-manifest.json`. Erzeugung:

```sh
ffmpeg -nostdin -hide_banner -loglevel error \
  -i tests/Fixtures/gallery-playback-vp8.webm -t 2 -c copy \
  artifacts/plugin-implementation/gallery-caption/fixture.webm
```

SHA-256 der tatsächlich geprüften Ableitung:
`f549bdaf6fba4161b046232c55588eabde1c8703192cc4bb9372ac8c0878d141`.
Sie liegt nur als Laufartefakt vor. Der bestehende `video-contrast`-Modus erhält
sie über `MEOWCHAN_VIDEO_FIXTURE`; er prüft zusätzlich Wiedergabezeit, Ende,
korruptes Medium und die vorhandene Toolbar-/Textkontrastmatrix.

## Abnahme

Die Captionmatrix misst 849/850/851 DIP Fensterbreite bei 530 DIP Höhe,
Light/Dark, Kontext geöffnet/geschlossen, normalen und absichtlich umgebrochenen
Status sowie die automatisch ausgeblendete Toolbar. Nach Scrollen bis zum Ende
bleibt „Show in thread“ erreichbar und außerhalb der Caption. Caption, Kontext,
Mediengrenze und Videotransport werden als reale Laufzeitrechtecke verglichen;
bei nativen separaten Fenstern unterstützen die Prüfhelfer Bildschirmkoordinaten.
Jeder Fall erhält ein echtes PNG. Keine Bildbearbeitung.

Der Produktstand ist seit `after-02-scale1` unverändert; alle sechs finalen
Läufe bestehen vollständig. Die abschließende Harness-Breitenprüfung berücksichtigt
auch eine separat unter dem Medium angeordnete Fallbackzeile; die Kollisionsprüfung
mit dem Kontext gilt weiterhin unabhängig davon.

| Skala | Medium / vollständiger Modus | Ergebnis | Artefaktordner |
| --- | --- | --- | --- |
| 1× | Video / `video-contrast` | PASS | `after-03-scale1/` |
| 1× | Bild / `reader-ux` | PASS | `image-04-scale1/` |
| 1.5× | Video / `video-contrast` | PASS | `video-05-scale15/` |
| 1.5× | Bild / `reader-ux` | PASS | `image-06-scale15/` |
| 2× | Video / `video-contrast` | PASS | `video-07-scale2/` |
| 2× | Bild / `reader-ux` | PASS | `image-08-scale2/` |

Je Lauf sind `results.json`, Log und PNGs erhalten. Die neue Captionmatrix liefert
jeweils 19 Bilder, insgesamt 114 Bildfälle. Releasebuild und `git diff --check`
sind sauber; keine Avalonia-Bindungsfehler. Die Profilsperren wurden nach normalem
Schließen freigegeben. Vorher/Nachher bei 1× sowie kompakte/wrappende Szenen bei
1×/1.5×/2× wurden tatsächlich visuell angesehen.

Reproduktion, jeweils mit neuem Ausgabeordner und passender Skala:

```sh
flock --close /tmp/meowchan-ui-1.0-build.lock env \
  MEOWCHAN_VIDEO_FIXTURE=/home/tomka/Desktop/oChan-1.0-workers/reader/artifacts/plugin-implementation/gallery-caption/fixture.webm \
  python3 scripts/run-desktop-smoke.py \
  --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll \
  --mode video-contrast --scale 1 --output artifacts/plugin-implementation/gallery-caption/new-video

flock --close /tmp/meowchan-ui-1.0-build.lock python3 scripts/run-desktop-smoke.py \
  --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll \
  --mode reader-ux --scale 1 --output artifacts/plugin-implementation/gallery-caption/new-image
```

Ausgewählte Nachherbilder:

- `after-03-scale1/video-contrast/smoke-gallery-caption-video-light-850-True-normal.png`
- `video-05-scale15/video-contrast/smoke-gallery-caption-video-dark-850-True-wrapped.png`
- `image-04-scale1/reader-ux/smoke-gallery-caption-image-light-849-True-wrapped.png`
- `image-08-scale2/reader-ux/smoke-gallery-caption-image-dark-850-True-normal.png`


`reader-ux` führt außerdem sämtliche vorhandenen Reader-/Galerierückkehr-,
Fokus-, Fit/1:1-/Zoom-, Postseiten-/Threadreihenfolgen- und Shutdownprüfungen
weiter aus, einschließlich der zuvor gelieferten Readeroverlaymatrix.

## Unabhängige Bildprüfung

`ux_archive` hat den tatsächlichen Vorher- und Nachher-PNG der 850×530-Light-
Videoszene unabhängig angesehen: Caption endet nachher vor dem Kontext,
Posttext und Button sind lesbar, Position „1/2“ bleibt in der Caption und der
Videotransport sichtbar unverändert. Keine neue sichtbare Regression in dieser
Szene. Die Bildprüfung behauptet keine Prüfung anderer Größen oder nativer Pfade.

## Aufbewahrte Zwischenläufe und Grenzen

- `before-01-scale1`: erwartetes FAIL, echter Produktfehler vor Änderung.
- `after-02-scale1`: neue Captionmatrix PASS; anschließende vorhandene
  Toolbar-Kontrastprüfung scheiterte, weil meine neue Fixture die Toolbar
  ausgeblendet zurückließ. Der Fixture-Teardown stellt nun über den bestehenden
  Eingabepfad die Toolbar wieder her. Keine Produktumgehung, keine gelockerte
  Assertion und kein längerer Timeout; bestehende Kontrastprüfung unverändert.

Dies sind Linux-Quellharnessnachweise mit echtem Softwarevideodecode. Windows-
Native-Overlay-/Fallback-, macOS-, Screenreader- und veröffentlichte
Apphostabnahme bleiben eigenständige Nachweise beim Integrator.
