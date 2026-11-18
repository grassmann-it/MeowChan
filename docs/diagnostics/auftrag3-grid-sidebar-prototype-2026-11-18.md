# Auftrag 3 — Grid + Medienleiste UX-Prototyp

## Ziel

Die bestehende Medienseite bekommt eine eindeutig benannte, reproduzierbare Ansicht für den vom UX-Review gewünschten Ablauf:

- dichtes Medienraster ohne zusätzliche Filter-/Statuszeilen,
- Auswahl bleibt im Raster tastatur- und mausbedienbar,
- das ausgewählte Medium erscheint groß in der vorhandenen Medienleiste daneben,
- kein zweiter Viewer, kein neuer Decoder und keine neue Auswahlsemantik.

## Umsetzung

Der Prototyp ist ein zusätzlich geschärftes Layout-Preset: **Grid + media sidebar** / **Raster + Medienleiste**.

Wiederverwendete Komponenten:

- `MediaPage` und `MediaLibraryTileView` für Raster, Auswahl und bestehende Tile-Aktionen.
- `ReaderMediaSidebar` für Vorschau, Header, Open-in-gallery-Aktion, Fokus und Live-Region.
- `MediaPage.Layout.cs` für die bestehenden Spalten, Resize-Kante und responsive Ausblendung.
- `MediaLayoutMenu` für Toolbar, More-Menü und Kontextmenü aus derselben Row-Definition.

Das Preset setzt nur die sichtbaren Grid-Chrome zurück (Tag-Liste, Filter-Chips, Statuszeile, Untertitel und Tile-Captions) und aktiviert die Medienleiste explizit. Die gespeicherte Seitenwahl, Breite und Seite der Medienleiste bleiben erhalten. Dadurch ist der Wechsel auch dann eindeutig, wenn die Leiste vorher manuell geschlossen war.

## UX-Entscheidungen

1. **Expliziter Name statt „Grid only“:** „Grid only“ war missverständlich, weil die Seite bereits eine Medienleiste haben kann. Der neue Name beschreibt die Kombination, die der Nutzer tatsächlich auswählt.
2. **Keine neue Panel-UI:** Die vorhandene Medienleiste hat bereits leeren Zustand, Auswahlfolge, Open-in-gallery, Video-Slot, Resize-Handle und Accessibility-Region. Ein paralleler Prototyp würde diese Zustände unnötig duplizieren.
3. **Responsive bleibt unverändert:** Unterhalb der vorhandenen Mindestbreite wird die Medienleiste automatisch ausgeblendet; die Auswahl und das Raster bleiben verfügbar. Die Layout-Wahl bleibt gespeichert und kommt bei ausreichender Breite zurück.
4. **Accessibility bleibt im bestehenden Pfad:** Die Layout-Row ist über Toolbar, More und Kontextmenü erreichbar; das Panel behält seine Region, Fokus-Reihenfolge und Resize-Hilfe. Es gibt keine Pointer-only-Aktion.

## Begrenzung

Nicht Bestandteil dieses Prototyps sind Drag-and-drop, ein zweites unabhängiges Medienmodell, neue Filterlogik, Gallery-Navigation innerhalb der Sidebar oder eine Änderung der Download-/Original-Aktionen. Diese Dinge würden eigene Produktentscheidungen und zusätzliche Regression-Surfaces erfordern.

## Validierung

- Pure Layout-Tests prüfen Preset, Panel-Aktivierung, gespeicherte Breite/Seite, Matching-Preset und Lokalisierung.
- Media-Page-Smoke prüft, dass das Raster kompakt bleibt und die Medienleiste bei vorher deaktiviertem Panel wieder sichtbar wird.
- Die bestehenden Panel- und Resize-Smokes bleiben der Integrationspfad für Rendering, Responsive-Verhalten und Fokus.

