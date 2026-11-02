# GP12/13 — erster additiver Bestandsmedienmerge

## Umfang dieses internen Schnitts

Ein validierter, gehaltener Native-Eingang mit genau einem bereits vorhandenen
Thread und ausschließlich enthaltenen aktuellen Originalen. Kein Desktopbutton
und keine Quelle wird dadurch aktiviert. Die bisherige New-thread-API und ihre
Receipts bleiben separat. Das Gesamtprogramm und GP12/13 bleiben offen.

- Zielquelle/Board/Thread/Post/Slot und Revisionkey sind Identität; SHA-256 allein
  ist keine logische Identität. Bestehende Pfade, Asset-/Binding-IDs und primäre
  Wurzel bleiben erhalten. Ein NotFound-/404-Thread wird nicht wieder aktiviert.
- Sichere neue Posts/Slots erhalten lokale keyed Referenzen und eine neue passive
  Wurzel. Eindeutig bestehende gleiche Revisionen werden nicht erneut aktiviert.
  Gleicher Slot mit anderer Revision oder gleichen Keys mit anderen Bytes bleibt
  Konflikt; alte Originale werden nicht überschrieben.
- Fehlende skalare Presencefelder können ergänzt werden. Explizite Zielwerte,
  einschließlich null/leer/gelöscht, gewinnen. Titel, URL, Sortiervertrag,
  Capturepräferenz und Beobachtungsdaten werden nicht ersetzt.
- Ein vollständiger Eingangskandidat samt Katalogdaten bleibt dauerhaft inaktiv
  erhalten. Nur die getrennt geprüfte sichere Keymenge wird registriert. Bei
  ausschließlich sicheren Inhalten kann dieselbe Wurzel verwendet werden.
  Bei null sicheren Keys gibt es keine neue aktive Wurzel.
- Schema8 ergänzt getrennte Mergeoperationen/Receipts, additive Migration von1–7,
  erst Preparing→Kandidat(en)→Prepared, dann atomarer Content-/Root-/Receiptcommit.
  Replay nach Delete/Recreate meldet historischen Erfolg ohne Wiederherstellung.
- Zielwurzel- und Kandidatenleases bleiben in kanonischer Reihenfolge bis zum
  finalen SQLcommit gehalten. SQLinstance/generation, Rootliste und sämtliche
  Manifestgenerationen müssen zum Plan passen. Bytearbeit läuft außerhalb SQL.
- Read-only Discovery/Inspektion meldet missing/empty/invalid/busy/incomplete und
  passende Ready-Kandidaten; kein stilles Resume, Reparieren, Löschen oder Adoptieren.

## Bewusste enge Grenzen des historischen ersten Schnitts

Die Einschränkungen zu retained Eingängen und Legacy-null werden durch R01/L01
unten erweitert; die übrigen Restarbeiten bleiben offen.

- Retained **Eingänge** sind noch nicht unterstützt und werden als Ganzes abgewiesen.
  Trifft ein neuer aktueller SQLslot auf einen retained Zielkey, wird auch bei
  Bytegleichheit der ganze Plan abgewiesen statt falsche Altbytes zu aktivieren.
- Neue aktive Wurzeln bei bestehenden aktuellen Legacy-null-Assoziationen werden
  vollständig vor Journal und Dateiarbeit abgewiesen. Andernfalls könnten sogar
  bytegleiche neue Fundstellen eine alte Resolverzuordnung mehrdeutig machen.
- Fehlende/defekte Zielmanifeste oder nicht eindeutige bestehende keyed Assoziationen
  werden nicht durch einen zweiten gesunden Root „repariert“.
- Bestehende private Favoriten/Tags/Notizen werden nicht umgeschrieben; deren
  Import/retained-Roundtrip ist damit noch nicht vollständig geliefert.
- Der vollständige inaktive Eingangskandidat benötigt weiterhin Platz, auch bei
  null neuen aktiven Bindings. Gleicher Operationsreplay kopiert nicht erneut;
  paketübergreifende physische Deduplizierung ist ein späterer Schritt.
- Explizite Konfliktauflösung/Verwerfen, Desktopvorschau/-progress,
  Capture/Delete/Cleanup/Move/Shutdown-Hostfences und native Win/macOS-Abnahme
  bleiben offen. Der interne Producer ist noch kein produktiver Importdialog.

## Vertrag und Prüfung

Vertrag: [passive-media-merge-v1](contracts/passive-media-merge-v1.md).
Unabhängiger Vertragsreview MM01–03:
`artifacts/goal-runs/2026-11-01-profile-a/review/passive-media-merge-contract-review-01.md`.
Dessen konservative Erstkorrekturen sind im Vertrag festgehalten; sie ersetzen
nicht die spätere vollständige Legacy-/retained-Unionanalyse.

Neue Tests liegen in:
- `NativeMediaMergeTests.cs`: No-op, Textkonflikt, Replay und Delete/Recreate.
- `NativeMediaMergeRoundtripTests.cs`: echte Writer/Stage/404-Medien1–20+15–30→1–30,
  Restart/normaler Resolver und bytegleiche unterschiedliche Fundstellen.
- `NativeMediaMergeSafetyTests.cs`: Journalcheckpointfehler, Manifeststale,
  Legacy-Assoziationsguard und Schema7-Reader/Migration/Altreceipt.
- `NativeMediaMergeFreshnessTests.cs`: frühe Instance-/Generationabweisung ohne
  Graphmaterialisierung, Prepared-Stale, Receipt-first und Leasefreigabe.
- `NativeMediaMergeReexportTests.cs`: gemischter Konflikt+sichere Ergänzung,
  retained-reiches Ziel und vollständiger realer Native-Reexport; Planbudget gegen
  tatsächlich geschriebene Container-/Katalog-/Manifest-/Payload-/ZIPbytes.
- `NativeMediaMergeBudgetTests.cs`: rechnerische 1GiB+1GiB-/ZIPgrenze ohne große
  Fixtures, kleine reale Writer und vollständige Metadatenbudgetgrenze.

## Gebundene Prüfung — 02.11.2026

C01/C02 des ersten Codereviews sind korrigiert und unabhängig nachgeprüft:
vollständige prospektive Reexportdarstellung statt current-only Serializerprobe;
Freshness ohne Graphload und mit sofortigem Instance-/Generationabbruch. Die
Operationsparent-Erzeugung bleibt nach dem Manifestgenerationvergleich unter
Zielrootlease. Review02/03 unter `artifacts/goal-runs/2026-11-01-profile-a/review/`
finden in diesem engen Schnitt keine offenen konkreten P1/P2-Produktblocker.

Runtimefreeze **6e5882af85d887aff7fe79183fb8548ebec89aeee7f1aebe83ffe9fcd8f516db**,
1.895 Inputs:

- `merge-compile-11`: erfolgreicher Corebuild, bestehende xUnit2031-Warnung.
- `merge-focused-07`: **49/49 PASS** (32 Merge-/17 Writerfälle).
- `merge-core-all-03`: **2.008 PASS + 1 bestehender Opt-in-SKIP**, 0 FAIL.
- `merge-desktop-build-01` erfolgreich; `merge-desktop-focused-01`: **23/23 PASS**
  einschließlich elf HTMLvolltitel- und zweier passiver Provenienzfälle.
- Der zusätzliche retained-near-cap-Test verwendet 24 reale Storebindings und
  gültigen erfassten HTMLtext. Das Ziel ist mit vollständigen retained Daten bei
  16MiB−8KiB tatsächlich exportierbar; der kleine Eingang ist separat gültig.
  Nur die vollständige Union überschreitet den Katalogcap, während current-only
  noch passt. Abweisung erfolgt ohne Plan/Journal/Kandidaten-/Zielmutation.
- Zwei echte Writer→Stage→Merge→Restart→Reexportfälle vergleichen sämtliche fünf
  Budgetkomponenten mit den tatsächlich geschriebenen Bytes. Arithmetische
  2GiB-Grenzfälle benötigen keine mehrgigabytegroßen Fixtures.

Rootreadback `evidence/reconciliation-native-merge-01.json` bindet alle Inputs,
fünf abgeschlossene Receipts, Logs/TRX/DLLs, Review02/03 und unveränderte
Benutzer-WIPs. Der dokumentarische Nachtrag hier entstand **nach** den Tests;
`evidence/reconciliation-native-merge-02-final.json` bindet den anschließenden
Dokufreeze und prüft alle Produkt-/Test-/Buildinputs bytegleich zum Runtimefreeze.
Kein neuer GUI-/Vollsuite-Desktop-/Windows-/macOS- oder vollständiger
Favoriten-/retained-Importnachweis wird daraus abgeleitet.

Historische Fehlversuche bleiben erhalten: Compile05 verwendete einen alten
Freeze; Focus05 scheiterte an einem unzulässigen langen Fixturedateinamen,
Focus06 an einem übergroßen Fixturemanifest. Compile08/10 waren ungenutzte
Übergangsbuilds. Keiner davon ist finaler Testbeleg. Der korrigierte Grenzfall
lockert keine Produktlimits. GP12/13 und der Gesamtgoal bleiben **offen**.

## R01-Erweiterung implementiert und geprüft — 02.11.2026

Der current-only Prüfstand oben bleibt historisch gebunden. Der neue interne
Producer ergänzt **contained retained Eingänge** und **exakte Ziel-retained
Wiederverwendung** gemäß R01 im Vertrag. Das ist keine vollständige GP12/13-Abnahme.

- Retained-only ergänzt ausschließlich historische Manifestbindings, ohne neue
  Posts oder aktuelle SQLattachments zu erfinden. Mehrere historische Revisionen
  desselben Slots können neben einer expliziten aktuellen Revision bestehen.
- Ein expliziter aktueller Eingang kann bei tatsächlich freiem SQLslot dieselbe
  eindeutig aufgelöste, frisch verifizierte Zielbindung referenzieren. Keine neue
  aktive Wurzel, Asset-ID, Binding-ID oder Provenienz für dieses Reuse. Sowohl
  zuvor fehlender als auch bereits vorhandener expliziter Post ist geprüft.
- Gleicher Key mit anderen Bytes bleibt inaktiver Konflikt, während unabhängige
  sichere retained Ergänzungen aktiviert werden. Ein belegter aktueller Slot
  gewinnt weiterhin bei Widerspruch. 404-/Capture-/Zielmetadaten bleiben erhalten.
- Reexportbudget umfasst die vollständige current/retained Union, global sortierte
  retained Keys und die **Zielhistorie** beim Reuse, nicht fremde Eingangshistorie.
- Nach Astra-P2 wird die physische Summe aktiver Originaldateien **getrennt** vom
  SHA-deduplizierten Exportbudget geprüft: alte Roots je Pfad zählen, neue Safe-
  Payloads nur innerhalb dieses Kandidaten nach SHA deduplizieren. Kein Abzug
  gleicher SHA in anderen Roots. Unverändertes 2GiB-Verifikationsbudget vor Claim.

### Aktuelle Lauf- und Reviewbelege

Runtimefreeze **60883982d7dccadf814751c321a22e717458fdf69faea7a9233f9cb341178a13**,
1.898 Inputs, sämtliche Produkt-/Testdateien danach unverändert:

- `r01-compile-02`: Corebuild erfolgreich; drei neue Nullable-Testwarnungen und
  eine vorhandene xUnit2031-Warnung, keine abgeschaltete Prüfung.
- `r01-focused-01`: **69/69 PASS**, darin 52 Merge- und 17 Writerfälle. Die 20 neuen
  R01fälle umfassen sechs retained Fälle, fünf Reuse-/Freshnessfälle und neun
  physische Budgetarithmetikfälle.
- `r01-core-all-01`: **2.028 PASS + 1 bestehender Opt-in-SKIP**, 0 FAIL,
  2.029 Gesamtfälle, 3m39. TRX-Zeilen bestätigen den Skip trotz `notExecuted=0`
  im aggregierten Counter; nicht als 2.029/2.029 ausgegeben.
- `r01-desktop-build-01`: erfolgreich, 14 bestehende Warnungen.
  `r01-desktop-focused-01`: **23/23 PASS**, einschließlich elf HTMLvolltitelfällen
  und zwei passiven Provenienzfällen. Kein neuer GUI-/Vollsuite-Desktop-PASS.
- Astra `review/retained-merge-review-01.md` schließt den physischen P2 am exakten
  Produktstand. `review/retained-merge-review-02.md` bindet fünf Produkt-/Testhashes
  und prüft die abgeschlossenen Fokus-/Core-TRX einschließlich aller 20 R01fälle.
- Root `evidence/reconciliation-retained-merge-01.json` prüft alle 1.898 Live-/
  Freezeinputs, fünf abgeschlossene Receipts, Logs/TRX/DLLs, beide Reviews, Vertrag
  und unveränderten ursprünglichen Benutzer-WIP. Dieser Ergebnisnachtrag entstand
  **nach** den Läufen; `reconciliation-retained-merge-02-final.json` bestätigt den
  anschließenden Doc-only-Delta bei bytegleichen Produkt-/Test-/Buildinputs.

Die physischen GiB-Grenzen sind rein arithmetisch geprüft; reale kleine Writer→
Stage→Merge→Restart→Resolver→Reexport-Läufe prüfen die Producerintegration. Kein
Multi-GiB-Datei-/Datenträgerstressbeweis. `r01-compile` bleibt als früherer
Übergangsbuild auf altem Freeze erhalten und ist kein finaler Laufbeleg.
Root musste in Testentwürfen Presenceflags und den ownergebundenen Replayversuch
korrigieren; Validator/Planownergrenzen wurden dafür nicht gelockert.

**Offen bleiben:** Legacy-null-sichere Union, private Favoriten-/Tags-/Notizen-
Übernahme, explizite Konfliktauswahl/Verwerfen, weitere Medienrollen/-zustände und
MultiThread, gemeinsame Host-Lifecyclefences, Desktopimport sowie native Windows/
macOS. Alle 40 gewählten Pakete und der Gesamtgoal bleiben aktiv. Keine persönlichen
Datenfixtures, weitere Bereinigung, Gitwrites, Remote-CI oder Publikation.


## L01 — Legacy-null-sichere additive Union geprüft, 02.11.2026

Der pauschale Legacy-null-Guard ist durch einen resolverstabilen additiven Nachweis
ersetzt. Alte aktuelle SQLattachments mit null Revisionkey werden nicht re-keyed;
ihr Root, Asset, Pfad und ihre Herkunft bleiben unverändert. Vorhandene Zuordnungen
müssen im frisch byteverifizierten Zielrootset bereits eindeutig auflösbar sein.
Neue current-/retained SafeAssets dürfen keinen zusätzlichen Treffer nach dem
unveränderten gemeinsamen Legacy-Resolverprädikat bilden. Auch identische Bytes
mit zusätzlicher passender Assetidentität werden konservativ abgewiesen;
disjunkte oder nachweislich nicht passende Ergänzungen sind erlaubt.

- Die unveränderte Million-Legacyvergleichsgrenze und das Reexportbudget zählen
  prospektiv auch neue SafeAssets. Physisches Zielbudget bleibt separat.
- Admission jetzt zweimal: Receipt/eigene Operation/resourceweiter unresolved
  Blocker/Freshness vor Rootarbeit; danach unter geordneten, byteverifizierten
  Rootleases dieselbe SQLprüfung und erst dort der dauerhafte Preparingclaim.
  Manifest-/Byteänderung vor neuem Claim lässt keinen neuen Operationssatz zurück.
- Der Resourceprecheck prüft beide Journalfamilien mit Preparing/Prepared, auch bei
  fremder Operation-ID; die atomare Claimfence bleibt zusätzlich bestehen. Kein
  fremder Vorgang wird resumed, gelöscht oder ersetzt. Ein erst nach dem Vorcheck
  auftretender Konkurrent ist nicht durch diesen Vorcheck global ausgeschlossen.
- Astra Review01/02/03: zwei konkrete Admission-P2 korrigiert, im finalen engen
  Scope kein offener P0/P1/P2. Keine private Favoriten-/Tags-/Notizmutation.

### Tatsächliche Laufbelege

Runtimefreeze **a1a026499566a4c12862ddd1c459cf961b546fe9c8fb2306e9edc8f9d8a18b9d**,
1.900 Inputs, alle fünf Läufe auf genau diesem Stand:

- `l01-compile-06`: erfolgreich, vier bestehende Testwarnungen.
- `l01-focused-03`: **87/87 PASS**, 68 Merge-/19 Legacyprädikatfälle.
  Darunter neue current/retained Legacy-Union mit echtem Restart/Resolver/Reexport
  und exakter Budgetgleichheit, konservative gleiche-Bytes-Abweisung, URL-/Hash-
  und nicht auflösbare Legacyfälle, Zielmutation vor Claim und Receiptpriorität.
  Zwei A/B-Regressionsfälle prüfen fremde Preparing-/Prepared-Mergeoperationen
  vor kaputten Zielbytes. Importjournal und echtes Zwischenfensterrace sind dadurch
  nicht separat laufzeitgetestet; beide SQLprädikate sind source-reviewed.
- `l01-core-all-02`: **2.063 PASS + 1 bestehender Opt-in-SKIP**, 0 FAIL,
  2.064 tatsächliche TRX-Zeilen, 3m20. Skip bleibt der private Zwei-Prozess-
  Opt-in-Treiber `ExactAcceptedRevisionSurvivesARealOfflineProcessRestart`.
- `l01-desktop-build-01`: erfolgreich, 14 bestehende Warnungen;
  `l01-desktop-focused-01`: **23/23 PASS**, einschließlich elf HTMLvolltitel-
  und zweier passiver Provenienzfälle. Kein neuer GUI-/Desktopvollsuite-PASS.

Rootbeleg `artifacts/goal-runs/2026-11-01-profile-a/evidence/reconciliation-legacy-union-01.json`
bindet Runtime-/finale Dokumentinputs, Logs, echte TRX-Zeilen, Core-/Desktop-DLLs,
Review03-Sourcehashes und ursprünglichen User-WIP. Nach den Tests wurde nur dieser
Dokunachtrag im Sourcefreeze geändert; kein neuer Produkt-/Test-/Buildinput.

Compile01-Testcompilefehler, Focus01-82PASS/3FAIL und die Übergangsläufe bleiben
historisch erhalten. Zwei alte Preparing-Erwartungen wurden der stärkeren
Pre-Claim-Garantie angepasst, eine ungültige 4cdn-Test-URL durch eine gültige ersetzt.
Keine Produktvalidatorlockerung. Compile05/Fokus02/Coreall01 gehören zum früheren
98738e55-Freeze und belegen nicht den abschließenden Resourceblockerpatch.

**Nächster Ownerblock:** explizite default-off private Favoriten-/Tags-/Notizen-
Auswahl und deren dauerhafte Recovery-/Storekoordination. Nicht ausgewählte private
Kategorien dürfen auch nicht über vollständige Konfliktkataloge dauerhaft gespeichert
werden. Danach bzw. getrennt bleiben übrige Medienrollen/-zustände, MultiThread,
Konfliktauswahl/Verwerfen, Hostfences und Desktopimport sowie native Windows/macOS.
Alle 40 Pakete bleiben gewählt, GP12/13 und Gesamtgoal aktiv. Keine persönlichen
Datenfixtures, weitere Bereinigung, Gitwrites, Remote-CI oder Publikation.
