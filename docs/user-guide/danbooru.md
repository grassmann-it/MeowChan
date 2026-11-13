# Danbooru-Suche und Suchbeobachtungen (MeowChan 1.1)

MeowChan behandelt Danbooru in der 1.1-Linie als **öffentliche,
schreibgeschützte Quelle**. Die Oberfläche zeigt Treffer und Provider-Metadaten;
ein Download wird erst durch eine bewusste Auswahl in die normale MeowChan-
Downloadwarteschlange gegeben. Es gibt keine Provider-Anmeldung und keine
Provider-Credentials in MeowChan. Die beschriebene 1.1.0-Entwicklungslinie ist noch
nicht als fertiges Release abgenommen.

Das Danbooru-Plugin muss unter **Plugins** aktiviert und für das Profil verfügbar
sein. Das Erkennen einer Danbooru-Adresse und das Öffnen des Danbooru-Fensters
fragt noch nichts ab. Im Fenster senden erst **Suchen** oder **Post abrufen** eine
begrenzte Anfrage; bereits aktive Suchbeobachtungen laufen unabhängig davon nach
ihrem Zeitplan. Eine Post-Adresse über **Downloads → Hinzufügen** lädt Metadaten
und reiht das Original in einem Schritt als One-Shot ein.

## Suchen und einzelne Posts

- Eine Tag-Suche nimmt höchstens **zwei Tag-Terme** und optional einen
  `rating:`-Term an. Negative Tags werden mit `-tag` geschrieben. Die eingegebene
  Query wird nach der normalen Trennzeichennormalisierung gesendet; MeowChan fügt
  weder `g,s` noch andere versteckte Suchbegriffe hinzu.
- Die Suche zeigt höchstens **fünf Seiten mit je 20 Treffern**. Die Seitennavigation
  bleibt innerhalb dieses technischen Suchbudgets. Eine kleinere Trefferanzeige
  bedeutet nicht, dass weitere Treffer vollständig verarbeitet wurden.
- Eine Danbooru-Post-URL oder eine numerische Post-ID kann im Fenster über
  **Post abrufen** geladen werden. Das legt keine Suchbeobachtung an. Die Vorschau
  und die Provider-Metadaten werden zuerst angezeigt; das Original kommt erst nach
  **Original laden** in die Downloadwarteschlange.
- Eine Danbooru-Suchadresse oder die Danbooru-Website-Adresse kann in **Downloads**
  eingefügt werden. Sie öffnet das Danbooru-Fenster mit vorbefüllter Suche und
  fragt noch nichts beim Provider an. Eine Post-Adresse über **Downloads →
  Hinzufügen** führt dagegen Metadatenabruf und Originalaufnahme als One-Shot in
  einem Schritt aus.
- Ein aus einer Suche geladener Post speichert diese Suche als Fundstelle. Ein
  Post, der per Link oder ID geladen wird, hat keine Such-Provenienz.
- Treffer, die bereits durch eine andere Suchbeobachtung, Suche oder einen
  One-Shot bekannt sind, werden nicht als zweiter aktiver Download eingereiht.
  Zusätzliche Fundstellen bleiben als Provenienz erhalten.

## Safebooru-Alias und Links

`safebooru.donmai.us` ist in dieser Funktion ein Alias für Danbooru und kein
separater ausgelieferter Provider. Bei einer Safebooru-Suchadresse zeigt das
Fenster den zusätzlichen `rating:g`-Term sichtbar in der Eingabe an, sofern die
Adresse keinen Rating-Term enthält. Er kann vor **Suchen** entfernt werden; bei
gewöhnlichen Danbooru-Suchen wird nichts versteckt ergänzt.

**Post im Browser öffnen** und **Link kopieren** verwenden die kanonische
Danbooru-Postseite, auch wenn der Treffer über den Alias gefunden wurde. Eine
Trefferkarte bietet beide Aktionen; eine Karte der Suchbeobachtung bietet sie für
den neuesten von ihr gesehenen Post. Die Aktionen öffnen beziehungsweise kopieren
nur den Provider-Link; sie laden nicht automatisch das Original. Das bewusste
Öffnen im Browser verlässt die lokale App und kann den Provider kontaktieren.

## Rating, Quelltags und Provenienz

Das Danbooru-Rating (`g`, `s`, `q` oder `e`) ist ein **Provider-Metadatum** und
keine MeowChan-Bewertung. Es wird angezeigt und gespeichert, aber nicht in eine
MeowChan-Bewertung umgewandelt. Eine ausdrücklich eingegebene Ratingbedingung
bleibt Teil der Query.

Quelltags von Danbooru bleiben von privaten MeowChan-Tags getrennt. Für
gespeicherte Medien bleiben Provider, Post-ID und Post-URL, Rating, Score,
Quelltags, Kategorien und die Fundstellen der Suchbeobachtungen erhalten.

Beim Laden wird die **Originalvariante** bevorzugt. Sample- und Preview-URLs sind
eigene Providerangaben und werden nicht stillschweigend als Original verwendet,
wenn kein Original verfügbar ist. Fehlt ein freigegebenes Original oder ist es
beschränkt, bleibt der Grund sichtbar und es wird keine kleinere Variante als
Ersatz eingereiht.

## Suchbeobachtungen

Eine gespeicherte Danbooru-Suche kann aktiviert, pausiert, manuell geprüft,
fortgesetzt oder entfernt werden. Neue Posts werden dedupliziert in die
**bestehende** Downloadwarteschlange übernommen; es gibt keine separate
Booru-Warteschlange.

Die erste erfolgreiche Prüfung arbeitet standardmäßig mit einer Baseline **ab jetzt**.
Sie lädt nicht ungefragt die gesamte historische Trefferliste. Eine automatische
Prüfung läuft standardmäßig einmal pro Stunde. Jede Prüfung ist auf höchstens
fünf Seiten mit je 20 Posts begrenzt. Wenn sie ihr Budget erreicht, merkt sich
MeowChan den Cursor unter dem ältesten gelesenen Post und setzt die nächste
Prüfung darunter fort. Dadurch werden neue Posts, die währenddessen erscheinen,
nicht einfach durch eine verschobene Seitennummer übersprungen.

**Jetzt prüfen** führt eine einmalige begrenzte Prüfung unabhängig vom Zeitplan
aus. Eine pausierte Suchbeobachtung bleibt danach pausiert und wird nicht erneut
automatisch eingeplant. Eine Prüfung kann durch Seitennavigation und begrenzte
Wiederholungen mehrere HTTP-Anfragen benötigen, auch bei einer pausierten
Beobachtung. Eine entfernte Suchbeobachtung oder eine Beobachtung im
Provider-Cooldown sendet nichts.
**Pause** beendet automatische Prüfungen; schon eingereihte Originale folgen den
normalen Warteschlangenregeln. **Fortsetzen** umgeht keinen Cooldown; die globale
Sitzungspause verhindert ebenfalls neue Prüfungen.

Bei HTTP 429 bleibt die Suchbeobachtung erhalten und wartet mindestens 60 Sekunden.
Ein längeres `Retry-After` wird berücksichtigt, aber höchstens 24 Stunden. **Vor**
dem einzigen 502/503-Wiederholungsversuch wartet der Host mindestens zwei Sekunden;
ein längeres `Retry-After` innerhalb des Jobbudgets wird ebenfalls berücksichtigt.
Bis zur erlaubten Zeit wird keine weitere Provideranfrage gesendet.
Ein temporärer Providerfehler löscht keine Suchbeobachtung und pausiert nicht
still die übrigen lokalen Medien.

Nach einem automatischen Check, der neue Originale in die Downloadwarteschlange
legt, zeigt MeowChan eine Info-Meldung mit dem Titel **Danbooru-Suchbeobachtung**.
Sie nennt die Beobachtung und die Zahl der hinzugefügten Originale; bei manuellen
Treffern erscheint diese Meldung nicht automatisch.

**Entfernen** beendet nur die Beobachtung. Bereits geladene Medien,
Provider-Metadaten, Quelltags, Fundstellen und private MeowChan-Tags werden nicht
lokal gelöscht. Eine später neu angelegte Beobachtung ist eine neue Beobachtung.

## Offlineverhalten und lokale Grenzen

Nach erfolgreichem Download sind Medien in Medienseite, Galerie und Favoriten ohne
Providerverbindung nutzbar. Die lokale Provenienz und die gespeicherten Metadaten
bleiben nach einem Neustart lesbar. Eine neue Suche, ein neuer Postabruf oder eine
Prüfung der Suchbeobachtung benötigt dagegen eine Verbindung; offline bleibt der
lokale Bestand verfügbar und der entfernte Abruf wird als Fehler beziehungsweise
später wiederholbare Prüfung angezeigt.

Für ein Backup müssen Profil und Medien zusammen erhalten bleiben. Eigene Tags
liegen in `private-tags.json`. Erst das tatsächliche v1→v2-Schreiben bewahrt dessen
vorherige Bytes einmalig als `private-tags.v1.bak`; eine vorhandene Sicherung wird
nicht überschrieben. Das ersetzt kein vollständiges Profilbackup. Siehe die
[Backup-Hinweise](../Help.md#own-tag-v1-backup-and-110-local-data).

Der lokale Danbooru-Katalog mit Posts, Fundstellen und Suchbeobachtungen hat
eine Schreibgrenze von **32 MiB**. Unter **Einstellungen → Speicher und Grenzen**
lässt sie sich auf **8 bis 128 MiB** setzen. Ab 75 % warnt der Beobachtungsbereich
im Danbooru-Fenster und nennt die Einstellung. Eine kleinere Grenze löscht keinen
vorhandenen Katalog; sie verhindert nur weiteres Wachstum, bis wieder Platz unter
der Grenze vorhanden ist. Der feste technische Lesedeckel bleibt unabhängig von
dieser Einstellung bestehen.

Danbooru ist in dieser Ausbaustufe read-only:

- kein Upload, Voting, Tagändern, Favorisieren beim Provider oder Providerkonto;
- kein unbegrenzter Backfill und keine Endlossuche „alle Treffer herunterladen“;
- kein zusätzlicher MeowChan-18+-Dialog und kein automatisch gesetzter `g,s`-Filter;
- keine weiteren Booru-Provider, HTML-Scraping-Fallbacks, Ähnlichkeitssuche oder
  Grabber-Parität in dieser 1.1-Funktion;
- Original, Sample und Preview bleiben unterschiedliche Varianten.

Providerantworten sind nicht vertrauenswürdig. MeowChan verwendet nur die
freigegebenen Danbooru-Hostpfade, begrenzte Antwortgrößen, eine Antwortfrist und
den vorhandenen sicheren Medienaufnahmeweg. Metadatenanfragen starten mindestens
eine Sekunde auseinander. Ein Job erlaubt höchstens 20 Sends einschließlich
Wiederholungen, dauert höchstens 120 Sekunden und gibt jeder Antwort höchstens
15 Sekunden Zeit. Antworten sind auf 2 MiB übertragen und 4 MiB dekodiert begrenzt.
Tests und Offline-Abnahmen verwenden
synthetische JSON-/HTTP-Fixtures, Fake-HTTP und Loopback; sie senden keine Live-
Provideranfragen.
