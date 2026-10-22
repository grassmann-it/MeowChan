# Echte Desktop-Pluginintegration — 22.10.2026

Codecommit: `b21dddc`. Arbeitskopie:
`/home/tomka/Desktop/oChan-1.0-workers/adapter-desktop-integration`.
Frische Rootbasis `594e5ba`; Root-APIbaseline `270292e` und SDK-Debuglock
`08e8711` sind importierte Abhängigkeiten, keine eigene Lieferung.

## Geschlossene Nachweislücke

Der bestehende `ApphostWorkflowSmoke` injiziert einen direkten FourChanprovider
und umgeht damit `PluginSession`. Der Plugins-GUI-Smoke besitzt unabhängige
Managementservices; der SDK-freie Packageprobe führt Hello/Describe aus. Diese
Nachweise allein belegten nicht die produktive Desktop-Verkettung.

Der neue interne optionale `Func<HttpClient>`-Anschluss ersetzt ausschließlich
den Transport zwischen `DesktopSessionHost` und `PluginSession`. Der normale
Aufrufer bleibt unverändert. Der erzeugte Client gehört der Session. Keine neue
öffentliche API, Umgebungsvariable, UIoption oder alternative Aktivierung wurde
eingeführt. Die Tests lassen `providers` ausdrücklich ungesetzt.

Die drei neuen Tests verwenden den echten transitiv ausgelieferten Payload in
`AppContext.BaseDirectory`: fünf `.plugin.json`, Worker-/Compiler-Apphosts und
die Pluginassemblies. Kein Test referenziert den Lainchan-/Kohlchan-/Filtertyp
direkt oder ersetzt Compiler, Inventar, Profilstore, Aktivierung, Runtime,
Sourceadapter, Contentbinder oder Filterresolver. Die aktiven Cacheassemblies
werden gegen die ausgelieferten Manifest-/Assemblyhashes geprüft.

## Ausgeführt

Zweimal **3/3 PASS**. Der letzte Lauf baute den Desktop/Test-Payload erneut ohne
Warnung/Fehler und lief anschließend 1:07 Minuten unter Linux x64/.NET 10.0.11.
Maschinenlesbare Ergebnisse und Startzeiten: [RESULT.json](desktop-integration/RESULT.json).
Der vollständige lokale TRX liegt daneben als `desktop-plugin-session.trx`.

```sh
flock --close /tmp/meowchan-ui-1.0-build.lock env MSBUILDDISABLENODEREUSE=1 dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -c Release -nodeReuse:false --filter FullyQualifiedName~DesktopPluginSessionIntegrationTests --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=desktop-plugin-session.trx' --results-directory artifacts/plugin-implementation/adapter/desktop-integration --nologo
```

1. **Produktiver Desktopweg:** echter Startupscan aller fünf Manifeste und
   Describe-Childprozesse; Lainchan und Kohlchan über normale Desktopaufnahme,
   Registry, HostHttp, Capture, Queue, Originalstaging und den ausgelieferten
   Dimensionsfilter. Drei reale Produktionsworker. Pro Quelle zwei gültige PNGs:
   Landscape wird bytegleich veröffentlicht, Portrait liefert dauerhaftes
   `FilterNoMatch/orientation_mismatch` ohne Mediendatei. Raw IDs, Varianten,
   Manifesthashes und Textposts stimmen. Regeln und genaue Boardgrants werden
   über die echten Session-/Profilstorebefehle gespeichert.
2. **Neustart und Offlinebestand:** zwei weitere Desktopstarts mit unveränderten
   Ausführungsrevisionen und Grants, persistierter Pause, null HTTPaufrufen und
   null gestarteten Produktionsworkern. Capture, gespeicherte Bytes und Excluded-
   Zustände bleiben erhalten. Anschließender providerfreier HTMLexport enthält
   pro Quelle zwei Posts, eine Datei und eine als regelbedingt markierte Auslassung.
3. **Katalogvorschau:** beide ausgelieferten Quellen laufen über
   `DesktopSessionHost.PreviewBoardAdmissionAsync`; jeweils ein kanonischer Thread,
   keine Watch-/Journalaufnahme, keine Originalmedien.
4. **Shutdown:** während der echte Lainchanworker auf HostHttp wartet, beendet
   Desktopshutdown den Request mit Cancellation, den eigenen Childprozess und
   die Session. Worker-/Stagingverzeichnisse sind leer; das Profil lässt sich
   wieder exklusiv öffnen und behält seine Watchabsicht.

Die Zahl vier beschreibt Nachweisgruppen; tatsächlich sind es drei Tests,
weil Workflow und Neustarts ein zusammenhängender Test sind.

## Ausschließlich erlaubte Fixture-URLs

Alle Antworten entstehen in einem terminalen `HttpMessageHandler` ohne inneren
Handler/Socket. Jede andere Methode/URL wird gezählt und mit Fehler abgewiesen.
Gültige PNGs entstehen lokal einschließlich IHDR-CRC, IDAT und IEND. Die
JSONformen entsprechen den bestehenden synthetischen Quellenfixtures.

| Zweck | URLs |
| --- | --- |
| Lainchan-Metadaten | `https://lainchan.org/demo/res/123.json`, `https://lainchan.org/demo/catalog.json` |
| Kohlchan-Metadaten | `https://kohlchan.net/demo/res/123.json`, `https://kohlchan.net/demo/catalog.json` |
| Lainchan-Originale | `https://lainchan.org/demo/src/1000-0.png`, `https://lainchan.org/demo/src/1001-1.png` |
| Kohlchan-Originale | `https://kohlchan.net/.media/landscape.png`, `https://kohlchan.net/.media/portrait.png` |

Der Downloadworkflow prüft exakt zwei Metadaten- und vier Medienrequests,
jeweils genau einmal. Der Katalogtest prüft genau zwei Metadatenrequests und
null Medienrequests. Die beiden Neustarts blockieren sämtliche HTTPrequests.
Updateprüfungen sind über die bereits vorhandene interne Testinjektion deaktiviert.

## Gemessene Startzeit und About-Smoke-Befund

Gemessen wird ausschließlich das erwartete `DesktopSessionHost.StartAsync`,
ohne Avaloniafenster oder Bildschirmerfassung. Alle fünf Samples erreichen
`Ready`, null Warnungen und keinen Startupfehler:

| Ausgangszustand | Dauer |
| --- | --- |
| Frisches Profil / keine Cacheartefakte | 11.050 ms |
| Frisches Profil / keine Cacheartefakte | 9.071 ms |
| Frisches Profil / keine Cacheartefakte | 8.069 ms |
| Neustart / fünf Cacheartefakte | 11.038 ms |
| Zweiter Neustart / fünf Cacheartefakte | 11.988 ms |

Damit ist ein erfolgreiches Starten über fünf Sekunden reproduziert. Der
Root-About-Smoke wartete dagegen nur fünf Sekunden auf `AddUrlButton.IsEnabled`;
sein Log zeigt die noch ausstehende Sessionrestauration beim anschließenden
Shutdown. Ein direktes Warten auf den bestehenden Starttask vor den eigentlichen
kurzen UIzustandsprüfungen entspricht dem asynchronen Startvertrag. Hier wurde
weder der Produkttimeout noch ein bestehender GUI-Smoke verändert.

Konkreter Kostenpfad im Code: `PluginSession.StartAsync` führt fünf
ImportBundle-/Describe-Paare sequenziell aus. `ImportBundleCoreAsync` prüft
**vor** seinem Artefaktcachecheck jedes Mal die vollständige Compiler-Toolchain:
171 Dateien, zusammen 16.145.832 Bytes, also fünfmal 80.729.160 gelesene/gehashte
Bytes, plus die fünf Describe-Childprozesse. Dies erklärt, weshalb ein vorhandener
Artefaktcache die Validierung nicht überspringt. Die genaue Zeitverteilung auf
Dateiprüfung, Prozessstart und allgemeine Systemlast wurde nicht profiliert;
eine alleinige Ursachenbehauptung wäre durch diese Messung nicht gedeckt.

## Verbleibende Grenze

Belegt ist die echte Desktopserviceintegration mit dem normalen gelieferten
transitiven Payload und realen Prozessen. Der Test ist kein Start des endgültigen
self-contained Desktopapphosts, kein SDK-freier/read-only Installationsnachweis,
keine neue GUI-/native Windows-/macOS-Abnahme und kein Liveanbietercheck. Diese
R10-10/R10-12-Gates bleiben mit den jeweiligen Root-/Plattformnachweisen zu verbinden.
