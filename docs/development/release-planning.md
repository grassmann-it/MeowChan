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
