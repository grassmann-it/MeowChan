# Pluginverwaltung / Filterregeln / Zielwarteanzeige – Rolle 10

Stand: 2026-10-22. Worktree `oChan-1.0-workers/management`, Branch `impl/1.0-wave2-management`.

## Lieferung

- Hauptcommit `0ef951d`: gemeinsame Pluginübersicht und Detailseite, Sources-Verknüpfung, Hostzustände, Kandidatenwahl/Enable/Rollback, Settingseditor, Editorübergabe, echte getrennte Build-/Test-/Metadatenprobe, Filterregelparameter und Grants, Downloadziel-Diagnosen, lokalisierte Navigation und gezielter Smoke-Modus.
- Folgecommit `13136ab`: enthält die erweiterten realen Filterregressionsfälle, dynamische Statuslokalisierung, erhaltene ungültige Alt-Booleans und umbrochene Compilerdateiaktionen.
- Abhängigkeitscommits der SDK-/Compiler-/Host-/Activation-/Adapter-/Filter-/Update-/Budgetowner sind lokal enthalten, aber **keine eigenen Liefercommits**. Root übernimmt sie aus den Ownerlieferungen.

Die UI nutzt denselben `IPluginProfileStore`, Compiler, Inventory und Runtime wie die Desktop-Session. Es gibt keinen zusätzlichen Plugin- oder Grantstore. Regelparameter persistieren ausschließlich im bestehenden Core-`MediaRuleStore`; die Hostgrants bleiben im gemeinsamen Profilstore. Aktive, ausstehende, angeheftete und letzte gute Fassungen sind separat sichtbar. Fehlende Dateien behalten gespeicherte Referenzen und den Loaderpfad.

Build liest die gespeicherte Quelldatei. Describe und Settingsvalidierung laufen in separaten Workern. Die URL-Probe verwendet nur normalisierte Metadatenoperationen; sie hat keinen Registry-, Archiv- oder Downloadpfad. Test und Navigation ersetzen oder beenden den produktiven Worker nicht. Abbruch und EndLifetime sperren verspätete Ergebnisse. Status/Abbruch sind auch bei geschlossenem Entwicklungsbereich sichtbar.

Der externe Editor erhält ausschließlich hostseitig konfigurierte Executable/ArgumentList-Daten. Dateipfad, Zeile und Spalte werden ohne Shell einzeln substituiert; Platzhalterzeichen innerhalb eines Dateinamens bleiben literal. Standardanwendung und Pluginordner stehen als Fallback bereit.

## Reviewkorrekturen

Alle acht Befunde aus dem unabhängigen Managementreview wurden korrigiert und vom Reviewer quellseitig nachgeprüft:

- Source-/Board-Grantziele bleiben nach Save/Refresh erhalten.
- Grantanzeige aktualisiert sich auch bei einem schmutzigen Plugindraft.
- Loaderpfad und Inventarfehler sind unabhängig vom Kandidatentyp sichtbar.
- Kandidaten zeigen ihren eigenen Aktiv-/Ausstehend-/Auswahlzustand sowie das konkrete Rollbackziel.
- Filterdomainvalidator läuft vor Corepersistenz; Feldfehler überleben den Save-finally-/Previewpfad.
- Entfernte oder inkompatible Altwerte werden nicht stillschweigend verworfen. Schemawechsel braucht ausdrückliche Annahme und anschließendes Speichern.
- Wirksame Rechte entsprechen `PluginGrantAuthorization.Resolve`; breitere Herkunftsgrants haben eigene nachvollziehbare Widerrufaktionen, ohne erfundene Denysemantik.
- Root hat in `8740781` `PluginsPage.EndLifetime` und `RulesPage.EndLifetime` vor dem ersten Shutdownawait/Sessionstop ergänzt. Der lokale Harness prüft zusätzlich den direkten EndLifetime-/Late-result-Fall. Close-to-tray bleibt unverändert.

Reviewbericht: `/home/tomka/Desktop/oChan/artifacts/plugin-implementation/2026-10-22/reviews/management.md`.

## Nachweise

Alle Restore-/Build-/GUI-Aufrufe verwendeten die gemeinsame `flock --close /tmp/meowchan-ui-1.0-build.lock`, Release und `MSBUILDDISABLENODEREUSE=1`/`-nodeReuse:false`. Frühere Locked-Restores der Desktop-, Smoke-, Test-, Compiler- und Workerprojekte bestanden. Keine persönlichen Profile oder Displays verwendet. GUI ausschließlich über `scripts/run-desktop-smoke.py` in privatem Xvfb/Openbox. `MEOWCHAN_DISABLE_UPDATE_CHECK=1` ist sowohl im Runner als auch direkt im Smoke-Entry gesetzt.

| Prüfung | Ergebnis |
| --- | --- |
| Release Desktop + DesktopSmoke | PASS, 0 Warnungen / 0 Fehler |
| PluginManagementTests + DiagnosticsTests | 14/14 PASS |
| Python DesktopSmokeRunnerTests (`unittest`) | 15/15 PASS |
| Plugins GUI 1× | PASS: `gui-04/plugins/output.log` |
| Plugins GUI 1.5× | PASS: `gui-05-scale15/plugins/output.log` |
| Plugins GUI 2× | PASS: `gui-07-scale2/plugins/output.log` und final `gui-08-scale2/plugins/output.log` nach Pfadumbruchkorrektur |

GUI-Matrix pro Skala: EN/DE × Light/Dark × Barebones/Simple/Advanced; 640×480 bzw. 760×500 DIP. Assertions prüfen erhaltene Drafts, schmale Listen-/Detailnavigation und Fokusrückkehr. Reale Compiler-/Workerfixture prüft Build, inert Describe, Settingsvalidierung, kontrollierte Host-HTTP-Metadatenprobe (2 Posts/1 Attachment), keine Watches/Originale, unveränderte aktive Revision/Produktivworkeridentität, Navigationsabbruch, EndLifetime und Syntaxfehler mit Datei-/Zeilenaktion.

Die erweiterte Filterfixture nutzt einen echten Pluginworker und einen echten profilgeleasten Core-RuleStore. Sie prüft fachliche Ablehnung ohne Commit, weiterhin sichtbaren Feldfehler, akzeptierten dauerhaften Commit, Sourcegrant für ein Board, Widerruf des tatsächlich breiteren Grants, erhaltene Scopeeingaben und ausdrückliche Migration einer alten Regel mit entferntem Parameter.

Unitnachweise umfassen fünf Schematypen, erforderliche Felder, stabile Fehlerkeys, Defaults/unset, rohe ungültige Altwerte, Schemaack sowie echte Childprocess-Editorübergabe mit Leerzeichen, Shellzeichen und literalem `{line}/{column}/{file}` im Dateinamen. Downloadzieltests prüfen die UIprojektion von Pfad, Grund und nächstem Prüfzeitpunkt; der Snapshot wird hierfür ausschließlich im Test per internem Konstruktor erzeugt. Die Backend-Datenträgerprüfung ist Budgetowner-Nachweis, kein behaupteter UI-End-to-end-Disk-full-Test.

Fehlversuche bleiben in den Artefakten: `gui-01` lief vor sichtbarem Fixturehost; `gui-03` klickte Save vor Verarbeitung des asynchronen TextChanged; `gui-06-scale2` prüfte vor abgeschlossener nativer Größenänderung. Der Harness wartet jetzt auf verarbeiteten Draft bzw. tatsächliches Layout. Assertions wurden nicht abgeschwächt. Der Versuch, Pythonchecks über pytest zu starten, scheiterte an fehlendem pytest; das vorhandene unittest-Suite lief anschließend vollständig erfolgreich.

## Bildkontrolle und Grenzen

Visuell kontrolliert: Barebones DE Light 1×, Rulefeldfehler 1.5×/2× und Compilerdiagnose 1×/2×. Finale 2×-Aufnahmen zeigen den konkreten Feldfehler beim abgelehnten Wert und die Compilerdiagnose mit vollständiger Dateiaktion einschließlich `:1:43`. Alle Screenshots und `results.json` liegen unter diesem Berichtsordner in den genannten GUIunterordnern; keine Artefaktbilder werden unnötig in Git eingecheckt.

Keine native Windows-/macOS-, Screenreader- oder SDK-freie Paketabnahme in dieser Rolle. Diese Gates bleiben bei Root bzw. den Paket-/Runtimeownern. Der lokale Smoke verwendet die vorhandenen Release-Compiler-/Workerbundles; er ist kein Behauptungsnachweis über fertige Installationspakete. Der komplette Root-Normalflow und integrierte Shutdownhook sind separat in Roots Gesamtprüfung auszuführen.
