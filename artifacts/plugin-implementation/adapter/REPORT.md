# Hostadapter und Desktopintegration — Abschlussbericht

Stand: 22.10.2026. Owner: `/root/build_adapter`.
Arbeitskopie: `/home/tomka/Desktop/oChan-1.0-workers/adapter`.
Dieser Bericht beschreibt tatsächlich ausgeführte Ownerprüfungen; er ist keine
plattformübergreifende Paket- oder Gesamtfreigabe.

## Selektive Liefercommits

Nur diese eigenen Commits übernehmen, importierte Abhängigkeiten nicht erneut:

| Commit | Inhalt |
| --- | --- |
| `6e5ba71` | Passiver lokaler Sourcerouter und asynchrone Board-/Threadadapter auf normaler Registry/Capture/Queue; Registrierung vor Restore. |
| `039b776` | Produktiver Desktop-PluginSession-Lebenszyklus, Compiler/Inventar/Aktivierung, gemeinsame HTTPpolicy, Filterbindung und Updatehooks. |
| `eb79885` | Dauerhafter deklarativer Postsortiervertrag, Archivschema 3 mit atomarer Migration, echte Worker-/Archiv-/Restoretests, Konfliktmenge und getrennte Test-HTTPdienste. |
| `936960e` | Interne raw `(MediaId, Variant)`-Unterscheidung und Abbruchschutz am dauerhaften Quellenendcommit. |
| `3a71e6a` | Deklarative Routenalphabete, HTTP/Port/Slash-Parität, begrenzte Katalogpagination, vollständiger Variantenweg, 8kun-Legacyadoption, Filtereviction- und macOS-Payloadanschluss. |
| `9611017` | Vorhandene Schema-Migrationsregression erwartet das aktuelle Archivschema 3. |
| `74a9e76` | Opaque Source-/Board-/Thread-/Mediaidentitäten auf sichere Pluginpfade abbilden, Extension bis 32 Zeichen, long-Ordinalanschluss, weitere Ende-zu-Ende-Regressionen. |

Der letzte Commit benötigt `c94ea08` vom Runtimeowner: `CapturedPost.Ordinal`
und der zugehörige öffentliche Konstruktor verwenden jetzt `long`. Root hat
diese APIänderung gemäß projektinterner 1.x-APIpolitik ausdrücklich bestätigt
und übernimmt die Breaking-Change-Dokumentation. Es gibt kein int-Clamping.

## Implementiertes Verhalten

- Der normale Desktop verwendet aktivierte Sourceplugins über Childprozesse.
  Explizit injizierte Library-/Testprovider bleiben unterstützt. Es gibt keinen
  stillen direkten 4chan-/8kun-Fallback bei defektem Plugin.
- Routing, Objektanlage und passive Anzeigen benötigen keinen Workeraufruf.
  Der Host übersetzt Sourceergebnisse in vorhandene Watch-, Capture-, Regel-,
  Queue-, Retry- und Mediencommitdienste. Dauerhafte Watchgenerationen bleiben
  erhalten; verspätete Antworten nach Remove oder Pluginabbruch werden verworfen.
- SourceId, BoardId, ThreadId, MediaId und Variant bleiben als rohe fachliche
  Identitäten erhalten. Varianten sind getrennte Queue-/Journal-/Manifestkeys.
  Bestehende Originalpfade bleiben unverändert; Varianten erhalten einen
  separaten gehashten Dateinamensuffix.
- Ein interner Provideranschluss kodiert ausschließlich physische Pluginpfade:
  ASCII-Segmente, die der bestehende StorageLayout-Vertrag akzeptiert, bleiben
  erhalten; andere, zu lange und reservierte Segmente werden deterministisch
  zu `~opaque-<sha256>`. Der Präfix liegt außerhalb des bisherigen Namensraums.
  Öffentliche Legacy-StorageLayout-Validierung bleibt unverändert. Nur der
  Pluginmedienhelper akzeptiert Endungen bis zur SDK-Grenze von 32 Zeichen.
- 4chan-Legacyoriginalnamen und 8kun-`MediaId+Extension` werden nur bei sicheren
  alten Ressourcenpfaden angeboten. Andere Quellen und opaque Board-/Threadids
  lösen keine spekulative Legacyadoption aus.
- Das Archiv speichert den deklarativen Postsortiervertrag unabhängig vom
  Plugin. Schema 1/2 bleiben passiv ohne Schreibmigration lesbar; der Writer
  migriert atomar/idempotent auf Schema 3. Volle 64-Bit-Ordnungswerte passieren
  Sourceadapter, Finalsnapshot, SQL, Restore und Offline-Reader.
- Kataloge werden sequenziell und vollständig unter derselben Metadataadmission
  geladen: höchstens 128 Seiten, SDK-Collectionlimit, 8 MiB aggregierter Text,
  Schleifenerkennung und keine Veröffentlichung unvollständiger Fehlerstände.
- Desktop-Start berücksichtigt stabile Routekonfliktmengen, CandidatePath,
  gemeinsame Filterregel-Fingerprints und tatsächliche Requestinstanzen nach
  Idleeviction. Testworker besitzen getrennte deklarative HTTPdienste.
- Unter macOS wird bei bekanntem `.app/Contents/MacOS` der vorhandene physische
  Payload unter `Contents/Resources/application` verwendet. Das vermeidet
  Appbundle-Aliase beim Worker-/Compiler-/Manifestpfad.

## Ausgeführte Prüfungen

Alle .NET-Befehle liefen seriell unter
`/tmp/meowchan-ui-1.0-build.lock`, mit `MSBUILDDISABLENODEREUSE=1`,
`-nodeReuse:false`, Release und isolierten temporären Profilen. Kein persönliches
Profil, keine laufende Benutzerapp und kein echter Anbieterabruf wurden genutzt.

| Prüfung | Ergebnis |
| --- | --- |
| `MeowChan.SourceAdapter.Tests` auf finalem Adapterstand | **28/28 PASS** |
| Gezielte vorhandene Core-Archiv-/Registry-/Content-/Varianten-/Lebensdauer-/MediaStoretests | **199/199 PASS** |
| Desktop `MeowChan.csproj` Release auf finalem Code | **PASS, 0 Warnungen, 0 Fehler** |

Die Coretests melden zusätzlich die bestehende xUnit2031-Analyzerwarnung in
`BoardAdmissionTests.cs:25`; sie betrifft nicht die Produktkompilierung.
Die erste Coreprüfung hatte ausschließlich die alte Schema-2-Erwartung entdeckt;
nach `9611017` besteht diese unveränderte Migrationsprüfung mit Schema 3.

Die Adaptertests belegen insbesondere:

- Reale offizielle 4chan-Workerassembly im Childprozess → Registry → Capture →
  Queue → exakt erwartete Mediendatei → zweimaliger terminaler Restore ohne
  Worker/Netz → providerfreier Archivreader. Endzeit und Generation bleiben stabil.
- Reale Varianten-Workerassembly mit den früher kollidierenden Paaren
  `(x--preview, original)` und `(x, preview)`: getrennte Bytes, raw Identitäten
  in Capture/Manifest/Journal, beide Erfolge nach zweimaligem Restore.
- Verifizierte vorhandene 8kun-Datei `456.jpg` wird ohne HTTP übernommen,
  obwohl der Uploadname `user-original.jpg` lautet.
- Generische Dezimalpostordnung nach Offline-Neustart; alter Schema-2-Katalog
  wird wiederholt lesbar und idempotent migriert.
- ThreadRead-only-Source ohne Boardroute; eigener HTTP-Port, verpflichtender
  Slash und escaped Literal; vollständige Mehrseitenkataloge und Paginationfehler.
- Raw Unicode-Source, reserviertes Board, 200 Zeichen lange Threadkennung,
  Mediaids mit Slash/reserviertem Namen/Namespacepräfix, opaque Variante und
  32-Zeichen-Endung durch normalen Download, Capture, Manifest und zwei Restores.
  Postordnungen `int.MaxValue`, `int.MaxValue+1` und `long.MaxValue` bleiben exakt.
- Opaque Boardaufnahme und Registryansicht stimmen bei der physischen Ablage
  überein. Sichere 4chan-/8kun-/Lainchan-/Kohlchan-Verzeichnisse bleiben gleich.
  Eine `../escape`-Boardkennung darf keine fremde Legacydatei übernehmen.
- Passives Routing ohne IPC, ungültige Routen, verspätete Antwort nach Remove
  sowie Failure/Deferred ohne terminales Quellenendsignal.

Reproduktionsbefehle aus dieser Arbeitskopie:

```sh
flock --close /tmp/meowchan-ui-1.0-build.lock env MSBUILDDISABLENODEREUSE=1 dotnet test tests/MeowChan.SourceAdapter.Tests/MeowChan.SourceAdapter.Tests.csproj -c Release -nodeReuse:false -p:CustomAfterMicrosoftCommonTargets=/home/tomka/Desktop/oChan-1.0-workers/adapter/plugins/Official/LibraryParsing.targets --nologo
flock --close /tmp/meowchan-ui-1.0-build.lock env MSBUILDDISABLENODEREUSE=1 dotnet test tests/MeowChan.Tests/MeowChan.Tests.csproj -c Release -nodeReuse:false -p:CustomAfterMicrosoftCommonTargets=/home/tomka/Desktop/oChan-1.0-workers/adapter/plugins/Official/LibraryParsing.targets --filter 'FullyQualifiedName~Archive|FullyQualifiedName~Registry|FullyQualifiedName~ContentRuntime|FullyQualifiedName~ContentRevision|FullyQualifiedName~MediaVariant|FullyQualifiedName~LifetimeCancellation|FullyQualifiedName~MediaStore' --nologo
flock --close /tmp/meowchan-ui-1.0-build.lock env MSBUILDDISABLENODEREUSE=1 dotnet build MeowChan.csproj -c Release -nodeReuse:false -p:CustomAfterMicrosoftCommonTargets=/home/tomka/Desktop/oChan-1.0-workers/adapter/plugins/Official/LibraryParsing.targets --nologo
```

## Integration und verbleibende Abnahme

Die finalen Ownerläufe enthalten insbesondere SDK `9a06e5b`, Activation
`08ab818`/`8267ca5`, Compiler `40f79cf`, Official `c922761`, Budgets
`67acdde`/`2a8f571`, Filter `92e5062`/`5347b38`, Runtime `8612d72` sowie den
Ordinalanschluss `c94ea08`. Diese Abhängigkeiten sind keine Adapterliefercommits.

Root integriert Projektgraph, Parserimport, Payloadtargets und Paketlayout.
Die produktive Desktop-Pluginverwaltung und ein tatsächlich ausgeliefertes
Desktoppaket wurden in dieser Arbeitskopie nicht als kompletter GUI-Startupflow
abgenommen. Native Windows-/macOS-Ausführung, signierte Appbundle-Aliase und
plattformübergreifende Releasepakete bleiben Integrations-/Releasegates.
Die tatsächliche vollständige Lainchan-/Kohlchan-Providerparität gehört zu deren
Ownernachweisen; die hier genannten Tests prüfen ihre stabile sichere Ablage.

Keine Pushes, Releases oder Änderungen an Benutzerprofilen durchgeführt.

## Nachtrag: produktive Desktopserviceintegration

Der Folgeauftrag schließt die bisher fehlende echte `DesktopSessionHost`- und
`PluginSession`-Verkettung mit ausgelieferten Lainchan-/Kohlchan-/Filterassemblies,
normalen Downloads, zwei Offline-Restarts und Worker-Shutdown. Drei neue Tests
bestehen zweimal. [Nachweis, Transportrouten und Startupmessungen](DESKTOP-INTEGRATION.md).
Die dort benannten endgültigen Apphost-/SDKfrei-/GUI-/Plattformgates bleiben offen.
