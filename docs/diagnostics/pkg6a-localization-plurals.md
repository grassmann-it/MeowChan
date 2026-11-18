# Paket 6A – Localization Plurals: Audit

Stand: **18.11.2026**, Worktree `worker-6a`, Basis
`origin/goal/gesamtprogramm-profile-a` at `866ad8ea2`.

## Ergebnis

Es wurden **keine Übersetzungen ergänzt oder künstlich erzeugt**. Die
Sprachpakete sind als unreviewte Vorschauen markiert; fehlende russische,
tschechische oder polnische Formen brauchen autorisierte Übersetzungen, nicht
einen mechanischen Text.

Die einzige belegte Code-Lücke war ein Randfall im Plural-Resolver:
`Math.Abs(long.MinValue)` warf `OverflowException`, obwohl `Category` negative
Ganzzahlen akzeptiert und bereits mit negativen Testwerten arbeitet. Der
Resolver berechnet die Betrag-Magnitude jetzt overflow-sicher als `ulong`.
Der Regelumfang bleibt unverändert.

## Pluralregeln für Ganzzahlen

`Desktop/Localization/PluralRules.cs` entscheidet nur über ganze `long`-Werte;
Bruchkategorien erreichen `Texts.Count` nicht.

| Sprache(n) | `one` | weitere Kategorien | Basisform (`other`/`many`) |
| --- | --- | --- | --- |
| `ru`, `uk`, `be` | 1, 21, 101, … | `few` für Endziffer 2–4 außer 12–14 | `many` |
| `pl` | 1 | `few` für Endziffer 2–4 außer 12–14 | `many` |
| `cs`, `sk` | 1 | `few` für 2–4 | `other` für 0 und 5+ |
| `fr` | 0 und 1 | – | `other` |
| `pt`/`pt-BR` | 0 und 1 | – | `other` |
| `pt-PT` | 1 | – | `other` |
| `ja`, `zh`, `ko`, `vi`, `th`, `id`, `ms` | – | – | `other` |
| übrige unterstützte Codes | 1 | – | `other` |

Der Resolver entfernt weiterhin eine Region am Bindestrich und verwendet für
unbekannte Codes die englische `one`/`other`-Regel. `Texts.Count` nimmt für die
gewählte Sprache die Form `<key>.<category>` und fällt bei fehlender Form auf
den Base-Key zurück; bei Sprachen ohne eigene Base-Übersetzung wird die
englische Regel verwendet.

## Resource-Key-Abdeckung

Die zehn eingebetteten Packs werden über die Assembly-Ressourcennamen entdeckt.
Die JSON-Keyzahlen in diesem Worktree sind:

| Pack | Keys gesamt | `.one` | `.few` | `.many`/`.zero`/`.two` |
| --- | ---: | ---: | ---: | ---: |
| `cs` | 5.240 | 107 | 42 | 0 |
| `es` | 5.192 | 101 | 0 | 0 |
| `fr` | 5.192 | 101 | 0 | 0 |
| `it` | 5.198 | 107 | 0 | 0 |
| `ja` | 5.091 | 0 | 0 | 0 |
| `ko` | 5.091 | 0 | 0 | 0 |
| `pl` | 5.241 | 107 | 43 | 0 |
| `pt-BR` | 5.198 | 107 | 0 | 0 |
| `ru` | 5.218 | 107 | 20 | 0 |
| `zh-Hans` | 5.091 | 0 | 0 | 0 |

Die vorhandenen Tests belegen die strukturelle Abdeckung über den normalen
Produkt-Loader, nicht über eine vereinfachte Literal-Grep-Liste:

- `BundledLanguagePackCompletenessTests` verlangt für jeden nichtleeren
  englischen Schlüssel genau die Formen, die die jeweilige Sprache tatsächlich
  auswählen kann.
- Zusätzliche Varianten müssen eine reale englische Base-Familie besitzen und
  in `PluralRules` erreichbar sein; unbekannte oder nicht auswählbare Suffixe
  werden abgewiesen.
- Doppelte JSON-Schlüssel, Placeholder-Abweichungen, falsche Codes/Namen und
  Loader-Warnungen werden geprüft.
- Die zwei leeren englischen Sentinels
  `savedSearch.confirm.None` und `savedSearch.prompt.None` sind absichtlich
  nicht in den Packs: Der Pack-Loader lehnt leere Packwerte ab, und die
  Laufzeit liefert den leeren Built-in-Wert korrekt.
- `ja`, `ko` und `zh-Hans` haben keine `.one`-Formen. Das ist keine
  Abdeckungslücke: Ihre Regel liefert für jede Ganzzahl `other`, daher kommt
  die übersetzte Base-Form auch bei 0 und 1 zum Einsatz.

Damit gibt es in der **strukturellen Pack-Abdeckung** keine belegte fehlende
oder unbekannte Resource-Form. Keine Pack-Datei wurde geändert.

## Echte Formrisiken, die nicht automatisch übersetzt werden dürfen

Die Abdeckungstests beweisen nur Schlüssel, Placeholder und Resolver-Verhalten;
sie beweisen keine grammatische Qualität. Bei `ru`, `cs` und `pl` existieren
Familien mit `.one`, aber ohne `.few`. Viele davon sind absichtlich
zählneutrale Label-/Einheitenformen. Die nichtidentischen Base/`.one`-Paare
sind jedoch belegte Kandidaten für eine fehlende reviewed Form:

| Pack | Paare Base vs. `.one`, ohne `.few` | Beispiele |
| --- | ---: | --- |
| `ru` | 12 | `priority.highAccepted`, `priority.normalAccepted`, `work.retryAccepted`, `work.retryPaused` |
| `cs` | 8 | `reader.view.mediaHidden`, `setup.parallelEffective`, `export.scope.selection` |
| `pl` | 7 | `stats.col.retained`, `export.scope.selection` |

Das ist bewusst als **Review-Blocker**, nicht als Übersetzungsauftrag,
dokumentiert: Bei 2–4 kann die Laufzeit dort auf die Base-Form zurückfallen,
obwohl die `.one`-Form eine andere Flexion zeigt. Ohne qualifizierte
Sprachprüfung wäre das Ergänzen von `.few`-Texten eine künstliche
Übersetzung. Für einen Folge-PR müssen die sichtbaren Texte pro Schlüssel und
Sprache fachlich freigegeben werden; erst dann dürfen die autorisierten
`.few`-Werte ergänzt und mit gezielten Count-Tests abgesichert werden.

## Änderungen

- `Desktop/Localization/PluralRules.cs`: sichere Magnitude für
  `long.MinValue`, keine Änderung der CLDR-Kategorien.
- `tests/MeowChan.DesktopTests/CountAndSizeTextTests.cs`: Regressionen für
  `long.MinValue` in `ru`, `pl` und `cs`.
- Dieses Dokument: aktuelle Key-Abdeckung, absichtlich fehlende Formen und
  sprachliche Risiken.

Nicht angefasst: `DesktopSmoke`, `.github/workflows`, Baselines, Lockfiles,
Build-Infrastruktur, Merge- oder Integrationsdateien.

## Validierung

Ausgeführt im Worktree:

```sh
dotnet restore tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --disable-parallel
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore --no-build \
  --filter 'FullyQualifiedName~CountAndSizeTextTests|FullyQualifiedName~PluralCountTextTests' \
  -m:1 --logger 'console;verbosity=minimal'
# Ergebnis: Passed 182, Failed 0, Skipped 0

dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore --no-build \
  --filter 'FullyQualifiedName~LanguagePackAuditRegressionTests' \
  -m:1 --logger 'console;verbosity=minimal'
# Ergebnis: Passed 23, Failed 0, Skipped 0

dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore --no-build \
  --filter 'FullyQualifiedName~BundledLanguagePackCompletenessTests' \
  -m:1 --logger 'console;verbosity=minimal'
# Ergebnis: Passed 2, Failed 10, Skipped 0
```

Der belegte Testblocker betrifft alle zehn Sprachparameter des Tests
`BundledPackIsDiscoverableUntestedAndCompleteForNonEmptyEnglishKeys`: Die
Packs enthalten die neu erwarteten englischen Schlüssel
`reader.media.activation.contextChanged` und
`reader.media.activation.missing` nicht; der Fehler liegt bei
`BundledLanguagePackCompletenessTests.cs:124` in der bestehenden Pack-Basis.
Die übrigen zwei Tests dieser Klasse (Discovery/README) bestehen. Die Packs
wurden deshalb nicht mit erfundenen Werten aufgefüllt; der Blocker bleibt als
separater Folgeauftrag für autorisierte Übersetzungen bestehen.

Bekannte nicht-blockierende Compiler-/Analyzer-Warnungen außerhalb des Pakets
werden nicht als 6A-Befund gewertet. Es wurde kein Desktop-Smoke und kein
Full-Solution-Release-Build angefasst.
