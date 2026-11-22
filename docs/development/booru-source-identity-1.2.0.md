# Booru-Quellenidentität in 1.2.0

Stand B2, Schritt 1. Die Abnahme verwendet Danbooru und eine zweite Bindung mit einem HTTP-Testdouble. Die ausgelieferte Quellenwahl bietet weiterhin Danbooru an.

## Inventar vor B2

| Stelle | Bisherige Annahme und betroffene Daten |
| --- | --- |
| [BooruOriginalQueue.cs](../../libmeowchan/Booru/BooruOriginalQueue.cs) und [BooruOriginalDownload.cs](../../libmeowchan/Booru/BooruOriginalDownload.cs) | Aktive Owner, Annahmebestätigung, Abbruch und Ergebnis waren nach der bloßen Post-ID adressiert. Die Queue setzte `danbooru/posts` als Owner, `booru-danbooru-{post}` als Medien-ID und `danbooru/{post}/{post}.{ext}` als Pfad. Der Selektor prüfte Danbooru-Dezimal-IDs und wählte ausschließlich das Original. |
| [BooruClaimMapper.cs](../../MeowChan.PluginHost/Sources/Booru/BooruClaimMapper.cs) | Der Mapper akzeptierte ausschließlich `danbooru`, Dezimal-IDs und die passende kanonische Danbooru-Post-URL. Varianten waren durch ihre Rolle pro Post bestimmt. |
| [BooruSourceProfile.cs](../../MeowChan.PluginSdk/BooruSourceProfile.cs) und [BooruContracts.cs](../../MeowChan.PluginSdk/BooruContracts.cs) | Profile deklarieren Endpunkte, Abfragesyntax, Zugriff und Taktung. Der SDK-Post trägt `SourceId` und `PostId`. Die Medienliste kennt je eine Rolle für Original, Sample und Preview. Instanz und logische Medien-ID fehlen auf dieser Grenze. |
| [BooruCatalog.cs](../../libmeowchan/Archive/BooruCatalog.cs) und [DownloadQueue.WorkTransitions.cs](../../libmeowchan/Downloader/DownloadQueue.WorkTransitions.cs) | Der Katalog indexierte nach `(SourceId, PostId)` und akzeptierte nur Danbooru. `originalIntent`, Asset-ID, Pfad und Befunde sind dort dauerhaft gespeichert. Der gemeinsame Downloader registriert einen Booru-Originalauftrag als externen Owner und überlässt dessen Persistenz dem Katalog. |
| [DanbooruWatcherDomain.cs](../../libmeowchan/Booru/DanbooruWatcherDomain.cs), [DanbooruWatcherRecordMapper.cs](../../libmeowchan/Booru/DanbooruWatcherRecordMapper.cs) und [DanbooruWatcherQueueAdmission.cs](../../libmeowchan/Booru/DanbooruWatcherQueueAdmission.cs) | Beobachtungen, `KnownPostIds`, High-Water-Mark und Cursor sind innerhalb der öffentlichen Danbooru-Instanz numerisch geordnet. Der Watcher übergibt Befunde an die Originalqueue. |
| [BooruSearchTabs.cs](../../Desktop/Booru/BooruSearchTabs.cs) und [BooruSearchModel.cs](../../Desktop/Booru/BooruSearchModel.cs) | Tabs hielten ihre Ansichten im Arbeitsspeicher. Die Ansicht kannte eine Anbieter-ID und eine ausgeführte Post-ID. Zeilenaktionen, Vorschau, Bulk und lokaler Status verwendeten kurze Post-IDs im Kontext eines Tabs und seiner Ports. |
| [TagQueueStore.cs](../../libmeowchan/Tags/TagQueueStore.cs) und [TagQueueItem.cs](../../libmeowchan/Tags/TagQueueItem.cs) | Die separate Tagging-Queue speichert einen optionalen `BooruPost`-Wert. Das ausgelieferte Format v2 und das Dogfood-Format v3 kennen dafür nur die alte Danbooru-ID. v3 kann zusätzlich einen Attachment-Revisionsschlüssel tragen. |
| [BooruMediaProjection.cs](../../libmeowchan/Gallery/BooruMediaProjection.cs) und [MainWindow.BooruMedia.cs](../../Desktop/Booru/MainWindow.BooruMedia.cs) | Medienansicht, Favoriten, Tagprojektion und Navigation verwenden die Danbooru-Projektion `danbooru/posts/{post}` und die bisherige Medien-ID. |

## Vertrag

- `BooruPostKey` steht für `(Quelle, Instanz, Post)`. `BooruMediaKey` ergänzt die logische Medien-ID und die genaue Variante. Alle ID-Felder vergleichen ordinal. URLs, Aliase, Dateinamen, Hashes und Anzeigenamen können sich ändern, ohne einen neuen Post zu erzeugen.
- Der Host bindet die Quelle und Instanz. Die öffentliche Danbooru-Instanz nutzt `danbooru/default`. Der Safebooru-Alias von Danbooru bezeichnet denselben Postbestand. Post-IDs dieser Instanz bleiben Dezimal-IDs. Andere Bindungen verwenden die vom Anbieter gelieferten IDs als opake Werte. Konten sind in diesem Schritt außerhalb der Identität und der Worker-Vertrag bleibt bei öffentlichen Zugriffen.
- Das heutige SDK-Modell liefert ein logisches Medium pro Post. Dafür reserviert der Host `primary`. Original, Sample und Preview unterscheiden sich auch dann, wenn URLs oder Dateiinhalte übereinstimmen. Die Originalqueue fordert weiter eine erlaubte Medien-URL, exakte Größe und MD5 an und verknüpft nur den verifizierten lokalen Commit.
- Katalog, Downloadplan, aktive Queue-Owner, Ergebnisse und Wiederanlauf verwenden die gebundene Identität. Die bisherige Danbooru-Medien-ID und ihr Pfad bleiben für vorhandene Dateien, Manifeste und Commit-Recovery gültig. Sonstige Pfade und Medien-IDs verwenden einen stabil kodierten SHA-256-Schlüssel aus den fünf Feldern. Jede Queue-Bindung benötigt ihre eigene Hostfreigabe und ihren Transport. Die Wiederaufnahme bearbeitet ausschließlich die eigene Bindung. Ausstehende Aufträge einer deaktivierten Bindung bleiben gespeichert, bis sie wieder verfügbar ist.
- Ein Such-Tab speichert Quelle, Instanz, ausgeführte Post-ID oder kanonische Abfrage sowie die Seitennummer. `booru-search-tabs.json` speichert außerdem Reihenfolge und aktiven Tab. Das bestehende Tab-Limit in den Einstellungen begrenzt die Wiederherstellung. Entwürfe, Trefferlisten, URLs, Vorschauen und laufende Requests bleiben Sitzungsdaten. Wiederherstellen ist lokal. Eine explizite Wiederholung nutzt nur den zum Tab passenden aktiven Port. Ist die Bindung nicht verfügbar, bleibt der Tab mit seinem Ziel erhalten und zeigt den vorhandenen Status für eine nicht verfügbare Quelle.

## Altprofile

- Der Katalog liest Schema 1 als `danbooru/default`. Unter Schema 1 bleiben die bisherigen Post- und Befund-IDs und der Dateiaufbau lesbar. Ein Schreibvorgang mit einer weiteren Bindung hebt den Katalog atomar auf Schema 2, das Instanzen berücksichtigt. Danach ist ein Leser für Schema 2 nötig. Vor dem ersten Schreibvorgang ist keine Umrechnung oder Neuanforderung von Medien erforderlich.
- In v2/v3 der Tagging-Queue wird eine gültige alte Danbooru-ID als `(danbooru, default, Post, primary, original)` gelesen. Undurchsichtige Altmarker bleiben unterscheidbar. Die bestehende Lesekompatibilität und das Schreibformat v2 gelten weiter. Der Tagging-Workflow für weitere Quellen bleibt ein Folgeschritt.
- Das Ausgangsrelease hatte keine Such-Tab-Datei. Ohne Datei startet der bisherige leere Haupttab. Ein gespeichertes Ziel ohne Quellen- und Instanzangabe fällt auf `danbooru/default` zurück. Die neue Tab-Datei startet mit ihrem eigenen Schema 1.

## Abnahme

`BooruOriginalQueueTests.SourceIdentity` stellt zwei Originale mit Post-ID `42` in dieselbe Download-Queue. Der Test startet mit einem alten Danbooru-Auftrag, öffnet das Profil erneut und prüft getrennte Dateien, Katalog-Links und Wiederaufnahme. Die zweite Bindung bleibt zunächst deaktiviert und bekommt keinen Request. Die Fälle decken eine andere Quelle und eine andere Instanz derselben Quelle ab. `BooruSearchTabIdentityTests` speichert zwei Post-Ziele mit `42`, öffnet sie offline wieder und prüft, dass der deaktivierte Tab keine Anfrage an die verfügbare Quelle umleitet. Die v2/v3-Fixture in `TagQueueStoreTests` prüft die Alt-ID und den anschließenden Schreibvorgang.

## Abhängigkeiten und offene Arbeit

- B5/B6 verwenden ein lokales Review-Journal und einen Tagger-Worker. Beide können den Booru-Originalauftrag über den bestehenden bestätigten Asset-Link konsumieren. Der Schritt benötigt davon keinen neuen Schreibpfad.
- B7 behält die Formate der Tagging-Queue für den Altprofilpfad. B8 betrifft die Reader- und Seitenleistenkomposition. B9 liefert selektive CI. B10 kann den vollständigen Medienbezug später für die Kollisionsvorschau verwenden.
- Die Quellenwahl, ein realer zweiter Adapter und seine Endpoint-/Hostprüfung, geteilte Anbieterbudgets samt `Retry-After` sowie die Galerie- und Watcher-Pfade für weitere Bindungen folgen in B2. B3 kann die Offline-Wiederherstellung um Treffer und lokale Filter erweitern. B4 klärt Kontozugriff sowie gegebenenfalls getrennte Identitätsräume privater Daten.
