# NW-07 — Lieferung, Aktivierung und produktive Abnahme

Ziel: jede tatsächlich fertiggestellte Quelle ist aus dem gelieferten Paket
benutzbar; Katalog → Thread → Original → Archiv/Reader/Galerie → echter
Prozessneustart ohne Netzwerk. **Teilweise vorbereitet/implementiert; produktive Gesamtabnahme offen.**

## Zwischenstand 2026-10-25

Isolierter Branch `feature/source-filter-wave/integration`, separat von Alpha 2.
Alle fünf Sourceadapter und Parserfixtures importiert. Gemeinsame Parserprüfung
am Stand `7df8dc3`: 317/317 Fälle bestanden (inklusive Bestandsquellen und FE-03/04),
vier Nullablewarnungen im Testprojekt. Das ist keine produktive Quellenabnahme.

Akzeptierte Entscheidung: alle fünf Quellen Experimental und beim ersten
Auftreten standardmäßig aus. 2ch behält gerundete Rohgröße als Hinweis;
Uboachan erhält SHA1 additiv. Leftypol darf Größen-/MD5-Abweichungen ausschließlich
nach ausdrücklicher Freigabe je Quelle/Profil akzeptieren. Bestehende andere
Profile/Quellen behalten ihre Aktivierungen und Grants.

`ae307f3` implementiert getrennte Aktivierung: 2ch/Uboachan/ZZZchan/SushiChan
aktivieren ohne Freigabeschritt; Leftypol und bestehende drei Ausnahmequellen
verwenden den bewusst kombinierten Dialog. Ein gefrorenes Target hält auch die
Freigabeabsicht fest. Strikte neue Quellen bieten keine Ausnahmecheckbox, der
Host verweigert direkte neue Grants und übernimmt dort keine gespeicherte
Ausnahme als Laufzeitberechtigung. 56/56 gezielte Desktop-Vertragstests bestanden;
kein GUI-Nachweis. Datenpfad-/Source-Mappingintegration aus NW-01 noch offen.

Noch offen: Lieferinventaraufnahme der fünf Quellen nach vollständigem Mapping,
reale terminale HTTP-/Queue-/Archiv-/Prozessneustartketten, enger Liveversuch,
SDKfreier Compile-/Workerpfad, Paket-/Plattform- und GUIprüfung. Parserbelege
ersetzen diese Nachweise nicht. Die momentane Filterteillieferung umfasst
11 Plugins / 6 Quellen / 9 editierbare Dateien; keine neue Quelle ist dadurch
bereits produktiv freigegeben.

## Abhängigkeiten und gemeinsame Dateien

Eingang je NW-02–06: exakte Identitäten, Hosts nach Metadata/OriginalMedia,
kanonische Routen/Aliase, Fähigkeiten, synthetische Thread-/Katalogfixtures und
DTO-Erwartungen sowie Integritätsentscheidungen aus NW-01, soweit betroffen.
Keine globalen Inventare mit noch fehlenden Quellen versehen.

Gemeinsame Lieferstellen: `MeowChan.sln`, `packaging/PluginDelivery.targets`,
`scripts/package-plugins.py`, `tests/test_plugin_packaging.py`,
`tests/PluginDeliverySmoke/sdk_free_worker_probe.py`, bestehende ExamplePlugins-
Testprojekte und Locks, Desktop-/Sessioninventare und Workflowrunner. Nur an
betroffenen Stellen ergänzen; alte Zweier-/Dreierworkflowzahlen nicht global ersetzen.
Fünf neue Quellen ergäben elf Sources/zwölf Plugins/zehn editierbare Beispieldateien
im bisherigen Liefermodell. Teilwellen bekommen ein entsprechend genaues Inventar.

## Aktivierung und Integrität

Vor Shipping Default/Experimentalstatus festlegen. Empfehlung: standardmäßig aus,
URL erkennt Quelle und zeigt bewusste Aktivierung; diese allein erlaubt keine
Integritätsabweichung. Nur nach ausdrücklich gewählter Leftypol-Ausnahmepolicy
darf dort der bestehende kombinierte Aktivierungs-/Freigabedialog eingesetzt werden.
2ch-Größenpräzision und Uboachan-SHA-1 sind Datenvertragsfragen, keine generelle
Erlaubnis für defekte Downloads. Alte Profile und die bisherigen sechs Quellen
behalten ihre bestehenden Grants und Aktivierungszustände.

## Gezielte Abnahme pro geliefertem Anbieter

1. SDK-only-Build; editierbare Datei ohne Hilfsassembly kompilieren; gelieferte
   Assembly im echten Worker beschreiben und aktivieren; kein Startup-Netzwerk.
2. Routing/Hostzwecke mit erlaubtem und fremdem Redirect; 2ch `.hk`→`.su` gezielt.
   gzip/deflate/br korrekt, kein ununterstütztes zstd anfordern. Bedingte Requests
   mit Baseline einschließlich schwach/stark unterschiedlicher ETagdarstellung.
3. Produktive Fixturekette mit mindestens zwei Anhängen, Match/NoMatch und
   exakten Archivbytes. Fehlende/kaputte Quellen dürfen andere Quellen nicht stören.
4. Enger Liveversuch durch den tatsächlichen Host/Downloadpfad je Anbieter mit
   kleinem Original. Parse-/HTTPbelege aus NW-00 ersetzen das nicht. Integritäts-
   Mismatch darf kein PASS werden; bewusste Ausnahme, sofern beschlossen, separat prüfen.
5. Echten Prozess beenden und neu starten; Archiv/Reader/Galerie ohne Netz lesen,
   Hash-/Größen-/Freigabenachweise erhalten; Widerruf dort prüfen, wo unterstützt.
6. Paket-/Sourceinventar und aktuelle Nutzerdoku ergänzen. Ergebnisse mit Commit,
   Bundle-/Assemblyhashes und konkreten Befunden binden. Kein pauschaler PASS über
   ungetestete Plattformen/Dateitypen; bestehende Releasegates bleiben separat.

Bei NW-01-APIänderung zusätzlich APIanalyzer, SDK-/Coreconsumer, IPC und alte
Manifestdaten prüfen. Ohne APIänderung keine unnötige neue öffentliche APIrunde.
Build-/GUIprüfungen im Projekt seriell mit etwaig laufender Arbeit koordinieren;
dieser Plan startet keine Worker oder neue umfassende GUI-/Plattformmatrix.
