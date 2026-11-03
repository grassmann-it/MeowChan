# Wave 8 – PlainUrlText/ChanPages-Exportgrenzen (GLM)

## Umfang

Nur Tests und dieser statische Bericht ergänzt:

- `tests/MeowChan.Tests/Interop/Links/PlainUrlTextBoundaryRegressionTests.cs`
- `review/wave8-urltext-tests-glm.md`

Keine Produkt-, Projekt-, Zustands-, Snapshot-, Git-, Netzwerk-, Build- oder Teständerung. Es wurden keine persönlichen Daten oder Medien verwendet.

## Vorher gelesen

- `libmeowchan/Interop/Links/PlainUrlText.cs`
- `libmeowchan/Interop/Links/PlainUrlText.ChanPages.cs`
- `tests/MeowChan.Tests/Interop/Links/PlainUrlTextTests.cs`
- `tests/MeowChan.Tests/Interop/Links/SupportedChanPagesTests.cs`
- `tests/MeowChan.Tests/Interop/Links/PlainUrlTextBoundaryRegressionTests.cs`
- `MeowChan.PluginSdk/Routing.cs`
- Route-/Identitätsanker in `plugins/Official/FourChan/FourChan.cs`
- Route-/Identitätsanker in `plugins/Official/EightKun/EightKun.cs`
- GP-53-Vertrag in `artifacts/goal-runs/2026-11-01-profile-a/gp53-worker.json`

## Neue Regressionen

1. **Quellen-/Board-/Thread-Identität:** gleiche Schreibungen wie `g`, `x` und `1` werden nur mit exakter Quellroute und Original-URL als eigenständig erhalten; Source- und Post-Klassifikation bleibt an der vollständigen Seitenform gebunden.
2. **Duplikatzustand:** `seen` gilt nur innerhalb genau eines `Parse`- oder `Export`-Aufrufs; Export ist wiederholbar und idempotent, ohne dass eine frühere Invocation die nächste als Duplikat markiert.
3. **Signierte/Userinfo-URLs:** zusätzliche gebräuchliche Query-Signaturformen sowie Userinfo-Varianten ansonsten kanonischer Chan-URLs werden fail-closed als `Credentials` beziehungsweise `SafetyUnknown` abgelehnt, ohne Geheimnis im Issuemodell. Die öffentliche Theory verwendet nur `string`/`bool`; der erwartete interne Enumwert wird im Testkörper abgeleitet, damit `LinkIssueCode` nicht Teil der öffentlichen Testsignatur wird.
4. **Exakte UTF-8-/Eintragsgrenze:** 128 mehrbyte Einträge belegen exakt `MaxBytes` inklusive finaler LF; ein um genau ein UTF-8-Byte gewachsener letzter Eintrag führt zu globalem `ByteLimit` ohne Teil-Export.
5. **Cancellation/API-Oberfläche:** `Parse` und `Export` nehmen nur den ausgewählten `ReadOnlySpan<byte>`-Slice an und haben ebenso wenig wie die Konstrukt-Delegates einen `CancellationToken`.

## Bewusste Abgrenzung

Vorhandene Tests decken bereits BOM/Zeilenenden, strikt falsches UTF-8, LF/CR/CRLF-Duplikate, URI-Normalisierungsvermeidung, `X-Amz-Signature`/`signature`, einfache Userinfo, ASCII-`MaxBytes`, exakte 4096-Zeilengrenze und die Datenoberfläche ab. Die Ergänzungen wiederholen diese Fälle nicht, sondern schließen die oben genannten Kombinationen ab.

Cooperative Cancellation ist im bestehenden synchronen Span-API nicht vorhanden. Der Test pinnt das; ein künftiger Abbruchvertrag wäre eine geplante API-Erweiterung und wurde nicht erfunden.

## Prüfgrenze

Statische Quelllektüre und Testautorenschrift. Gemäß Auftrag wurden keine Builds, Tests, Commits oder Zustandsaktualisierungen ausgeführt; Compiler-, Analyzer- und Laufzeitergebnisse sind daher noch offen.
