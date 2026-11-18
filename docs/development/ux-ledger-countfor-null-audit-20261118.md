# Package-3-Verifikationsaudit: `CountFor(null)`

**Auditdatum:** 2026-11-18
**Audit-Worktree:** `/home/tomka/.worktrees/ochan-ledger/worker-3-audit`
**Audit-Basis:** `origin/goal/gesamtprogramm-profile-a` (`866ad8ea2`)
**Geprüfte Implementierung:** `origin/codex/pkg3-countfor-null-20261117` (`73bb24ead`)

## Ergebnis

Die Package-3-Implementierung ist semantisch richtig und behebt einen belegten
Restfehler. `CountFor(null)` muss den ungruppierten Scope zählen, nicht die
Gesamtqueue. Die Audit-Basis enthält noch die alte Sonderbehandlung
`groupId == null ? Count : ...`; der geprüfte Package-3-Stand ersetzt sie durch
den gleichen Prädikatsvergleich wie für benannte Gruppen:

```csharp
Items.Count(item => item.GroupId == groupId)
```

Damit bedeutet `null` konsistent `GroupId == null`. Die Gesamtqueue bleibt über
`TagQueueSnapshot.Count` bzw. über `TaggingQueueView.InGroup(..., null,
ungrouped: false)` erreichbar; sie darf nicht implizit aus `CountFor(null)`
abgeleitet werden.

## Call-Site-Inventar

Die Suche nach `CountFor` und den verwandten Scope-Helfern wurde auf dem Audit-
Stand ohne `bin/`, `obj/` und `.git/` ausgeführt.

- `Desktop/Views/Tagging/TaggingEmptyStates.cs:21`
  - Benannte Gruppe: `groupId != null` und `CountFor(groupId)`.
  - „Ohne Gruppe“: `ungrouped` und `CountFor(null)`.
  - „Alle“ erreicht diesen Zweig wegen der Kurzschlussbedingungen nicht.
- `tests/MeowChan.Tests/Tags/TagQueueStoreTests.cs:386`
  - Prüft `CountFor` für eine benannte Gruppe.
- Keine weitere `TagQueueSnapshot.CountFor`-Call-Site gefunden.
- `OpenFor`, `DoneFor` und `SkippedFor` haben auf dem Audit-Stand keine
  produktiven Call-Sites. Die Package-3-Änderung behandelt sie trotzdem
  konsistent als Scope-Helfer (`item.GroupId == groupId`), ohne eine bestehende
  Gesamtqueue-API zu verändern.
- Gleichnamige Treffer in `BooruB9AcceptanceSupport` und
  `FilterTermPicker` gehören zu anderen Typen und sind nicht Teil dieses
  Audits.

Die umgebende Darstellung bestätigt dieselbe Unterscheidung: `TaggingQueueView`
filtert „Ohne Gruppe“ explizit mit `item.GroupId == null`, während „Alle“ keine
Gruppenbedingung setzt. Ein `CountFor(null)` mit Gesamtqueue-Semantik würde
folglich nur den Empty-State-Klassifizierer von der tatsächlich angezeigten
Scope-Semantik abkoppeln.

## Fokussierte Verifikation

Auf dem geprüften Package-3-Stand wurden mit vorhandenen Build-Artefakten
folgende Tests ausgeführt:

```text
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --no-build --no-restore \
  --filter 'FullyQualifiedName~MeowChan.DesktopTests.TaggingPageMenuTests.AGroupedQueueIsEmptyWhenTheUngroupedScopeHasNoItems'
Passed: 1, Failed: 0, Skipped: 0

dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --no-build --no-restore \
  --filter 'FullyQualifiedName~MeowChan.DesktopTests.TaggingPageMenuTests'
Passed: 15, Failed: 0, Skipped: 0
```

Der neue Regressionstest deckt die kritische Konstellation ab: zwei Einträge in
einer benannten Gruppe, `CountFor(null) == 0`, `OpenFor(null) == 0` und
`TaggingEmptyKind.GroupEmpty` für den Scope „Ohne Gruppe“.

## Restfix-Entscheidung

Kein weiterer Codefix im Audit-Worktree. Die bestehende Package-3-Änderung ist
klein, lokal und durch die Call-Site- und Scope-Analyse belegt. Eine zusätzliche
Änderung an `TaggingEmptyStates` oder an der bestehenden `CountFor`-
Implementierung würde nur einen bereits gelösten Codepfad duplizieren und
unnötigen Konflikt mit der Package-3-Implementierung erzeugen.

Die Audit-Dokumentation ist die einzige Änderung dieses Worktrees. Keine
`DesktopSmoke`-, `.github/workflows`-, Baseline-, Lockfile- oder Build-Infra-
Datei wurde geändert.
