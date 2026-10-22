# V39–41 — Inspector, Exportmengen und leere Archive

Basis `77e091b`, Branch `worker/sol61-ui-inspector-archive`. Angenommene Vorschläge auf ausdrücklichen Auftrag umgesetzt; ältere Offenmarkierungen in Reviewquellen sind historischer Stand.

- V39: Im bewusst geöffneten ThreadInspector folgen Fehler (weiter selektierbar und bedingt sichtbar) und lokale Lesen-/Galerie-/HTMLaktionen direkt auf Status. Ordner, Browser, Prüfen, Retry, URL, Fortschritt, Pfad und Entfernen bleiben erhalten. Nur XAML-Reihenfolge, keine Commands oder Aktivierungsregeln geändert.
- V40: Favoritenexport benennt die tatsächlich angezeigte Anzahl, unabhängig von Einzelmarkierung. Tooltip und Automation-Hilfe erklären Originaldateien und fehlende Originale. `visible.ToArray()` und nachfolgende Resolution bleiben unverändert. Archivtrefferbutton nennt sichtbar „Treffer → HTML…“; bestehendes vollständiges Menü und Auflösungs-/Snapshotvertrag bleiben erhalten.
- V41: Erfolgreich geladene Nulltreffer unterscheiden anhand der angewandten Such-/Source-/Board-/Quellenstatus-/Capture-/Post-/Workfilter den leeren Bestand vom gefilterten Ergebnis. Nur letzteres bietet explizites Zurücksetzen. Der bestehende Suchpfad lädt anschließend lokal neu und erhält Sortierung; keine automatische Erweiterung. Favoriten zeigen leeren Umfang oder erfolglose Suche; „Suche leeren“ ändert ausschließlich den Text, der Scopewechsel bleibt ausdrücklich. Fehlende Originale bleiben vorhandene Zeilen.

## Integration

Der vereinbarte `ArchiveEmptyState`-/`ArchiveResetFilters`-Hunk in `ArchivePage.axaml` liegt beim Readerworker `/root/ui_reader_v36_47` und ist für diesen Commit erforderlich. Dieser Worker ändert keine ArchivePage.axaml-Datei. Lokalisierung nur eigene Keys in ArchiveCatalog; kein BuiltInCatalog-/Texts-/Style-Eingriff.

## Prüfung

Ausgeführt: Python-XMLparse MainWindow.axaml, eng begrenzte Inspector-Reihenfolgenkontrolle, Snapshot-Quellabgleich; PASS. `git diff --check`: PASS.

Ergänzt, noch nicht ausgeführt (Root führt .NET/GUI seriell im Original aus):

- `ArchiveEmptyStateTests`: Sortierung allein versus jede restriktive angewandte Eingabe.
- `ArchivePageChecks`: echte Nulltreffersuche und Workfilter, explizites Rücksetzen, Sortierungserhalt, Screenshot `smoke-archive-no-search-results.png`.
- `ReaderInteractionChecks.CheckFavoritesOverviewAsync`: Zwei sichtbare Favoriten bei einer Markierung, Nulltreffer und Sucheleeren ohne Scopewechsel, Exportanzahl nach Entfernen, leerer Scope. Der vorhandene Frozen-Originalexporttest bleibt bestehen.

Offen bis zur integrierten Prüfung: Build, tatsächliche Smokeausführung, Sichtung bei Minima/EN/DE/Light/Dark/1×/1.5×/2×, Inspector mit langem Fehler und Tabfolge, Favoritendialog bei 450px. Keine neue GUI- oder native Accessibilityabnahme behauptet. Keine Push-/Releaseaktion.
