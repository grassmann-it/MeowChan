# Planung nach 1.1.0

Alle drei Linien starten bei 45a45f5bd. Seit dem Release-Tag v1.1.0 sind dort Dokumentation und Screenshots hinzugekommen.

| Branch | Welche Änderungen landen hier? |
| --- | --- |
| 1.2.0 | Hauptentwicklung für kompatible Features, Fehlerbehebungen und Arbeiten an der Validierung. |
| 1.1.1 | Einzeln prüfbare Korrekturen, die auf 1.1.0 funktionieren und bestehende Profile und Plugins weiter unterstützen. |
| 2.0.0 | Änderungen mit Kompatibilitätsbruch. Gemeinsame Weiterentwicklung durch regelmäßiges Mergen von 1.2.0 übernehmen. |

Einen patchfähigen Fix auf 1.2.0 als eigenen Commit mit dem passenden Test landen. Auf 1.1.1 mit `git cherry-pick -x <commit>` übernehmen und den Test dort erneut ausführen. Hängt der Fix an einem neuen Feature, den Fehler zuerst auf 1.1.1 bestätigen und bei Bedarf eine kleinere Korrektur schreiben. Dringende Fixes, die zuerst auf 1.1.1 landen, zeitnah nach 1.2.0 übernehmen. Beim Merge nach 2.0.0 geänderte Schnittstellen und betroffene Tests prüfen.

## Erste Kandidaten

Auf claude/quick-wins-1-1-1-dc08c1 liegen bereits drei kleine Änderungen an Plugin-Beschriftungen, Diagnosemeldung und Shortcut-Dokumentation (695c6bfc9, f22c887d0, 671a4a1df). Als mögliche Backports einzeln sichten.

## Validierung auf 1.2.0

Die Release-Notizen zu 1.1.0 dokumentieren den Paketbau und den ausgelassenen Gesamtlauf. Für die erste Bestandsaufnahme je fehlgeschlagenem Job Commit, Plattform, ersten Fehler und kleinsten passenden Wiederholungslauf sammeln. Dafür bietet dotnet-desktop.yml bereits offline-retest für einzelne Lanes und smoke-retest für einzelne Linux-x64-Smokes. Einen reproduzierbaren Fehler als erstes Arbeitspaket auswählen. Danach anhand von Laufzeit und Fehlerursachen entscheiden, welche Prüfungen bei jedem PR und welche vor einem Release laufen sollen.

Der Workflow startet automatisch bei Pushes auf main, bei PRs gegen main und bei Versionstags. Vor der gemeinsamen Arbeit auf GitHub festlegen, welche Checks PRs gegen die drei neuen Branches auslösen sollen.

## Eingang aus dem Recherche-Thread

Hier können auch unsortierte Ausschnitte mit Quellen und offenen Fragen gesammelt werden. Beim Sichten pro Vorschlag Nutzen, betroffene Komponente, Kompatibilität und vorläufige Zielversion ergänzen. Noch offene Ideen dürfen ohne Zielversion stehen bleiben.

### Validate-Befund vom 20. November

Die reinen Doku-PRs #162 und #163 erzeugten je 44 Jobs. Beim Screenshot-PR #163 waren 25 rot, zehn grün und neun übersprungen. Die ausgeführten Jobs summieren sich laut Start- und Endzeiten auf rund 17,5 Jobstunden. Das schließt parallel laufende Jobs ein.

- [Linux lane-a](https://github.com/grassmann-it/MeowChan/actions/runs/44436732593/job/135267348168) meldete sechs Release-Fixture-Fehler mit identischem Manifest-Mismatch. Auf 1.2.0 lokal reproduziert und durch explizite Versionsübergabe behoben. Im selben Job scheitert der separate Preference-Startup-Smoke beim finalen Speichern.
- [Linux lane-b](https://github.com/grassmann-it/MeowChan/actions/runs/44436732593/job/135267348155) meldete denselben Reader-Toolbar-Fehler bei 760 Pixeln wie [PR #162](https://github.com/grassmann-it/MeowChan/actions/runs/44432779726/job/135254044377). Als Nächstes die sichtbaren Aktionen und ihre Priorität gegen das beabsichtigte Overflow-Verhalten prüfen.
- Windows lane-d meldete einen Fehler in core_tests, macOS ARM native-05 in mac_tests. Einzelursachen sind noch offen. Abgebrochene Läufe separat zählen.

Vorschlag für PRs gegen die drei Entwicklungslinien
1. Einen kleinen Pflichtjob immer starten. Bei ausschließlich bekannten Doku-/Screenshot-Pfaden lokale Links und Bildreferenzen prüfen. Legal-, Packaging- und eingebettete Ressourcen gesondert behandeln.
2. Bei Produktänderungen Release-Build und betroffene Offline-Suites auf Linux x64 ausführen. Für Reader/Galerie den passenden GUI-Smoke, für plattformspezifische Eingaben oder Wiedergabe auch den betroffenen nativen Runner wählen. Gemeinsame oder unbekannte Pfade breit prüfen.
3. Bei Packaging-/CI-Änderungen Fixtures und Workflow-Verträge sowie die betroffene Paketplattform prüfen. Vollmatrix regelmäßig auf dem Integrationsstand und als stable-candidate vor Veröffentlichung laufen lassen.
4. Auswahlregeln mit repräsentativen Diffs testen. Der PR-Pflichtjob muss fehlende oder fehlgeschlagene ausgewählte Prüfungen melden. Die vollständige Matrix bleibt separat sichtbar, bis die offenen Ursachen geklärt sind.
