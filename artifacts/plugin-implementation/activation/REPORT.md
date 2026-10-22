# R10-06 Aktivierung und Konfiguration — 22.10.2026

Arbeitskopie: `/home/tomka/Desktop/oChan-1.0-workers/activation`, Basis `dc053f8`.
Fremde Voraussetzung: Runtime-Contract `205c049`, hier als `33ba91b` übernommen.
Eigene Liefercommits: `77179a9` (früher Vertrag), `5e5b8b1` (Implementierung/Tests).
Keine Änderungen an Root-STATUS, Desktop, Core-Regelstore, Runtime oder zentralen Projektdateien.

## Ergebnis

- `PluginProfileStore(profileDirectory)` führt `plugin-state/state.json` als einzigen Hostwertebestand für Auswahl, aktive/pending/letzte gute/gepinnte Releases, Plugin-/Sourcekonfiguration und Grants. Der frühere unveröffentlichte Entwurf `plugins/state.json` wurde vor Nutzerprofilverwendung getrennt, damit das Drop-in-Inventar keine Metadaten als Kandidaten scannt.
- Ein Release bindet Artefakt und geprüfte komplette Konfiguration zusammen. Build-, Schema-, Konfigurations-, Lifecycle-, Profil- und scopierte Grantrevisionen bleiben getrennt.
- Konsistentes Startinventar aktiviert erst beim Appstart. Testbeobachtung und normale Einstellungsänderung beeinflussen laufende Releases nicht. Defekte/fehlende ausgewählte Updates führen zu erklärter Unverfügbarkeit; kein stiller Rückfall.
- Expliziter Rückfall pinnt den erhaltenen Build mit dessen passender Konfiguration dauerhaft. Auch editierte Einstellungen eines Pins bleiben über wiederholte Starts erhalten. Auswahl neuer Kandidaten löst den Pin ausdrücklich.
- Doppelte Kandidaten bleiben sichtbar. Ohne Auswahl wird kein Dateireihenfolgesieger gewählt. Vorher aktive Sourcezuständigkeit bleibt bei Konflikten erhalten; explizite Kandidatenauswahl speichert Sourcezuständigkeiten für den nächsten Start.
- SDK-Schemata werden validiert. Additive Defaults erhalten bestehende Werte; entfernte, typgeänderte oder ungültig gewordene Werte verlangen Korrektur, alte Releases bleiben erhalten. Ungültige Drafts schreiben nichts.
- Schreiben nutzt globale Profilrevision/CAS unter einer stabilen exklusiven Dateisperre; Prüfsumme, Größenlimit, Flush und atomarer Austausch im selben Verzeichnis. Beschädigte/inkompatible Dateien werden nicht still initialisiert. Abgebrochene temporäre Dateien sind kein Lesefallback.
- Enable/Disable schreibt sofort und meldet `LifecycleChanged`. Active/Pending werden dabei nicht vertauscht. Grants sind pro Plugin/Capability/Source-/Board-/Regelscope gespeichert, auch ein Widerruf behält einen revisionierten Eintrag. `GrantChanged` meldet den dauerhaften neuen Stand sofort.
- `PluginGrantAuthorization.Resolve` vereinigt ausschließlich passende explizite Grants. Der stabile Revisionstoken enthält deren Scope, Zugriff und Revision; Revoke/Regrant entwerten alte Übergaben, andere Boards bleiben unabhängig.
- Snapshots sind passive, vom Store abgelöste Daten. Beschreibungen/Informationswerte bleiben in ihrer SDK-Herkunft; es gibt keine Netzabfrage beim Lesen und keine erfundenen Host-/Onlinebeobachtungen.
- RetainedExecutionRevisions/RetainedCompilationFingerprints schützen Active, Pending, LastGood, Pin und gespeicherte Kandidaten auch im deaktivierten Zustand für die Compilerbereinigung.

## Zuständigkeitsabstimmung

Filterparameter gehören atomar mit den anderen Regelbestandteilen in den bestehenden **Core MediaRuleStore** (Filterowner, `d01a843`). Die frühe, noch unintegrierte `SaveFilterRuleAsync`-/`FilterRules`-Skizze wurde deshalb in `5e5b8b1` entfernt. Der Host bietet dafür nur pure `ValidateFilterRuleSettings`; keine zweite Konfigurationsdatenbank. Management und Filter wurden direkt informiert. Stabile RuleId-Erzeugung liegt beim Filterowner und wird von der UI wiederverwendet.

Runtime/Adapter konsumieren `PluginReleaseSnapshot.Artifact`, `Description`, `Configuration`. Disabled behält die gültige Active-Fassung; Missing/Broken setzt Active null und erhält bekannte Beschreibungen für unverfügbare lokale Adapter. `LifecycleChanged` verlangt Abbruch/Invalidierung laufender Aufrufe; Reenable verwendet ausschließlich Active. Grants werden vor Übergabe/Read/Commit erneut verglichen. Diese Produktionsanbindung gehört den entsprechenden Nachbarrollen.

## Ausgeführte Prüfungen

Linux, .NET SDK `10.0.111`, ausschließlich neu erzeugte temporäre Testprofile. Alle Restore-/Testaufrufe unter `/tmp/meowchan-ui-1.0-build.lock`, MSBuild NodeReuse deaktiviert.

```
dotnet restore tests/MeowChan.PluginHost.Configuration.Tests/MeowChan.PluginHost.Configuration.Tests.csproj -p:Configuration=Release --locked-mode -nodeReuse:false
dotnet test tests/MeowChan.PluginHost.Configuration.Tests/MeowChan.PluginHost.Configuration.Tests.csproj -c Release --no-restore -nodeReuse:false
```

PASS: **16 Tests**, keine übersprungenen Tests. Die Tests lesen und schreiben tatsächliche isolierte Dateien; Neustartfälle öffnen neue Storeinstanzen ohne übernommenen Speicherzustand.

Geprüft: Pending/Restart und deaktivieren/reaktivieren ohne Hot-Swap; kaputtes Update ohne Fallback; persistenter Rückfallpin; zum Build passende alte Werte; additive und inkompatible Schemata; alle fünf Pflichttype und fehlende Pflichtwerte; ungültiger Draft ohne Revisionserhöhung; gleichzeitige unabhängige Storeinstanzen mit genau einem erfolgreichen CAS-Save; beschädigte Datei und verwaiste Tempdatei; deaktivierte/fehlende Plugins mit erhaltenen Referenzen; Boardgrants und Widerrufstoken; pure Regelparameterprüfung ohne zweiten Store; Kandidatenkonflikte; explizite Crossplugin-Sourceauswahl; parallele Leser bei atomaren Revisionen; abgelöste Snapshots; editierbarer Pin bei defektem Update über wiederholte Starts.

`git diff --check`: PASS. Eigenes Testprojekt besitzt `packages.lock.json`. Der beim Restore erzeugte Hostlock wurde bewusst nicht übernommen; Hostprojekt/Hostlock gehören dem Runtimeowner.

## Nachweisgrenzen

Diese Lieferung belegt Store-/Auswahl-/Migrationsverhalten, keine vollständige R10-06-Produktabnahme. Tatsächliches Workerbeenden, verworfene verspätete Antworten, Watch-/Archiv-/Fristenkontinuität und globale Pause benötigen die Adapter-/Runtimeintegration. Artefaktbytes werden beim Compiler-/Runtimeladen erneut geprüft; der Store baut oder lädt selbst keinen Code. Die lokalen Tests verwenden dafür passive Artefaktverweise. Kein GUI-, Site-, persönlicher Profil- oder Plattformpaketlauf wurde ausgeführt.

Dateiaustausch ist atomar auf demselben Dateisystem; vollständige Stromausfallsicherheit des Verzeichnisjournals wird damit nicht behauptet. Eventabonnenten verwenden dieselbe produktive Storeinstanz; Änderungen aus einem zweiten laufenden Appprozess benötigen dessen übergeordnete Profilownership. Die Dateisperre und CAS verhindern dabei stille Schreibüberschreibungen.
