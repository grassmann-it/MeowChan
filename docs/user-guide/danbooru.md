# Danbooru-Suche und Suchbeobachtungen (MeowChan 1.1.0)

MeowChan behandelt Danbooru in der 1.1-Linie als **öffentliche,
schreibgeschützte Quelle**. Die Oberfläche zeigt Treffer und Provider-Metadaten;
ein Download wird erst durch eine bewusste Auswahl in die normale MeowChan-
Downloadwarteschlange gegeben. Es gibt keine Provider-Anmeldung und keine
Provider-Credentials in MeowChan. Die beschriebene 1.1.0-Entwicklungslinie ist noch
nicht als fertiges Release abgenommen.

Das Danbooru-Plugin muss unter **Plugins → Alle Plugins** aktiviert und
für das Profil verfügbar sein. Öffne die Hauptseite **Booru** über Fußleiste oder
**Mehr**; **Danbooru-Suche und Suchbeobachtungen…** im Appmenü führt auf dieselbe
Seite, nicht in ein separates Fenster. Das Erkennen einer Danbooru-Adresse und
das Öffnen der Booru-Seite fragt noch nichts ab. Auf der Seite senden erst
**Suchen** oder **Post abrufen** eine begrenzte Anfrage; bereits aktive Suchbeobachtungen laufen unabhängig davon nach
ihrem Zeitplan. Eine Post-Adresse über **Downloads → Hinzufügen** lädt Metadaten
und reiht das Original in einem Schritt als One-Shot ein.

## Booru-Seite, Anzeige und Tastenkürzel

Die Fußleiste und das Appmenü ordnen die Hauptseiten als **Downloads, Archiv,
Medien, Taggen, Booru | Regeln, Plugins | Status**. Die kontextbezogene F1-Hilfe
folgt derselben Reihenfolge. Booru liegt unter Minimal in **Mehr**, ab Einfach
bei ausreichender Breite direkt in der Fußleiste; bei Platzmangel wandern Ziele
nach Mehr. Nach den Gruppen folgen **Mehr** und **Einstellungen**. Mehr enthält
nur aus der Fußleiste ausgelagerte Ziele, keine doppelten Einträge für Downloads
oder Einstellungen; das Appmenü bietet weiterhin die verfügbaren Hauptseiten an.
Booru hat als Hauptseite keinen eigenen Zurück-/Schließen-Knopf.

Strg+6 (⌘+6 auf macOS) öffnet standardmäßig Booru. Plugins bleibt auf
Strg+5, Status auf Strg+7; nur Strg/⌘+8 ist standardmäßig frei. Eine gespeicherte
Plugins-Belegung auf Primary+6 hat Vorrang vor dem neuen Booru-Standard und öffnet
**Alle Plugins** auf der Seite Plugins. Vorhandene Bereichskürzel für Statistik
und Diagnose bleiben erhalten; Strg+7 öffnet den zuletzt benutzten Status-Bereich.
F1 und die Fußleisten-Tooltips zeigen die wirksamen Belegungen, nicht einen zweiten
Standard über deiner gespeicherten Wahl. Fußleiste und Menü folgen der aktuellen
Verfügbarkeit. F1 kann auch ein ausgeblendetes oder nicht verfügbares Seitenkürzel
weiter auflisten; das schaltet die Seite nicht frei.

Mit F1 öffnest du das gemeinsame Fenster **Tastenkürzel** beim aktuellen Bereich.
Es bleibt offen, während du die Hauptseite weiter bedienst. Du kannst die
Belegungen suchen, nach Bereich filtern, sortieren und bearbeiten; Änderungen
gelten erst nach **Speichern**, Kollisionen verhindern das Speichern. F1 oder Esc
schließt das Fenster wieder; Esc beendet zuerst eine laufende Aufnahme, und bei
ungespeicherten Änderungen fragt das Fenster nach.

**Booru anzeigen** in Fußleiste und Appmenü ist ein gespeicherter, profilweiter
Anzeigeschalter, standardmäßig an. Ohne eingebundene Booru-Dienste fehlen Schalter
und Seitenziel. Ausschalten blendet die Booru-Ziele in Fußleiste, Mehr und Appmenü
sowie Medien → Mehr → **Booru öffnen** aus; das Seitenkürzel öffnet keine ausgeblendete
Seite. Von der gewählten Booru-Seite geht es zu Downloads zurück. Wiederanzeigen
macht sie erneut erreichbar und bewahrt den ungesendeten Entwurf, solange dieselbe
Quelleneinbindung besteht. Das ist nur Anzeige, keine Quellenautorisierung,
Plugin-Aktivierung oder Suchbeobachtungspause. Quellen, Suchbeobachtungen und
Downloads laufen nach ihren bisherigen Pausen-, Cooldown- und Verfügbarkeitsregeln
weiter. Anzeigen setzt keine pausierte Suchbeobachtung automatisch fort, sendet
keine Suche und umgeht diese Regeln nicht. Ausblenden ist nicht dasselbe wie das
Deaktivieren des Plugins unter Plugins.

## Suchen und einzelne Posts

- Eine Tag-Suche nimmt höchstens **zwei Tag-Terme** und optional einen
  `rating:`-Term an. Negative Tags werden mit `-tag` geschrieben. Die eingegebene
  Query wird nach der normalen Trennzeichennormalisierung gesendet; MeowChan fügt
  weder `g,s` noch andere versteckte Suchbegriffe hinzu.
- Die Suche zeigt höchstens **fünf Seiten mit je 20 Treffern**. Die Seitennavigation
  bleibt innerhalb dieses technischen Suchbudgets. Eine kleinere Trefferanzeige
  bedeutet nicht, dass weitere Treffer vollständig verarbeitet wurden.
- Eine Danbooru-Post-URL oder eine numerische Post-ID kann auf der Booru-Seite über
  **Post abrufen** geladen werden. Das legt keine Suchbeobachtung an. Die Vorschau
  und die Provider-Metadaten werden zuerst angezeigt; das Original kommt erst nach
  **Original laden** in die Downloadwarteschlange.
- Eine Danbooru-Suchadresse oder die Danbooru-Website-Adresse kann in **Downloads**
  eingefügt werden. Sie öffnet die Booru-Seite mit vorbefüllter Suche und
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
separater ausgelieferter Provider. Bei einer Safebooru-Suchadresse zeigt die
Booru-Seite den zusätzlichen `rating:g`-Term sichtbar in der Eingabe an, sofern die
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
Sind die übrigen neuen Posts nicht mehr erreichbar, meldet die Suchbeobachtung das
und bietet **Neu ab jetzt** an. Das nimmt die Lücke in Kauf: Die Beobachtung wird
ohne Löschen lokaler Daten beendet und für dieselbe Suche mit einer frischen
Baseline neu angelegt. Posts in der Lücke werden nicht geladen; bereits geladene
Dateien bleiben.

**Jetzt prüfen** führt eine einmalige begrenzte Prüfung unabhängig vom Zeitplan
aus. Eine pausierte Suchbeobachtung bleibt danach pausiert und wird nicht erneut
automatisch eingeplant; **Jetzt prüfen** ist kein **Fortsetzen**. Eine Prüfung kann
durch Seitennavigation und begrenzte Wiederholungen mehrere HTTP-Anfragen
benötigen, auch bei einer pausierten Beobachtung. Eine entfernte Suchbeobachtung oder eine Beobachtung im
Provider-Cooldown sendet nichts.
**Pause** beendet automatische Prüfungen; schon eingereihte Originale folgen den
normalen Warteschlangenregeln. **Fortsetzen** umgeht keinen Cooldown. Die globale
Sitzungspause verhindert neue automatische Prüfungen; eine bereits laufende
automatische Prüfung beendet nur noch ihre gerade geladene Seite. **Jetzt prüfen**
führt auch während der globalen Pause eine manuelle Prüfung aus, ohne die Sitzung
fortzusetzen oder einen Provider-Cooldown zu umgehen. Nach dem Fortsetzen gehen
automatische Prüfungen unter dem gespeicherten Cursor weiter; individuelle Pausen
und Cooldowns bleiben erhalten.

Bei HTTP 429 bleibt die Suchbeobachtung erhalten und wartet mindestens 60 Sekunden.
Der Retry-After-Deckel liegt standardmäßig bei **24 Stunden** und ist von **1 bis
168 Stunden** einstellbar. Die Einstellung heißt **Maximale Danbooru-Retry-After-Pause
(Stunden)**. Ein gültiger längerer Providerwunsch wird an diesem Deckel begrenzt;
fehlende, ungültige, abgelaufene oder kürzere Angaben behalten die Mindestwartezeit.
Zusätzlich sättigt die Kalenderrechnung am technisch maximal darstellbaren Datum;
das hebt den eingestellten Deckel nicht auf.

Ein Absenken der Einstellung kann eine bestehende Providerpause im Arbeitsspeicher
beim nächsten Prüfen durch den Host verkürzen; es verspricht keine sofortige
Beobachtungsprüfung. Es setzt weder eine pausierte Suchbeobachtung fort noch
wiederholt es automatisch eine fehlgeschlagene Suche oder einen Postabruf.
Ein 429 beendet den aktuellen Metadatenjob ohne Wiederholung. Später zulässige
Arbeit verwendet einen neuen Job und beachtet weiterhin Pacing und Pausenzustand;
die gesendete Query wird dabei nicht geändert und kein Ratingfilter ergänzt.

**Vor** dem einzigen 502/503-Wiederholungsversuch wartet der Host mindestens zwei
Sekunden. Ein längeres gültiges Retry-After wird durch den aktuellen Deckel begrenzt;
der Versuch muss strikt vor der Jobfrist liegen. Andernfalls endet der Job mit
einem Budgethinweis, statt über seine Laufzeit hinaus zu warten. Auch bei einer
zweiten 502/503-Antwort an das Plugin wird ein gültiges Retry-After oberhalb des
Deckels begrenzt; das erlaubt keine weitere Host-Wiederholung.
Bis zur erlaubten Zeit wird keine weitere Metadatenanfrage an diesen Provider gesendet.
Ein temporärer Providerfehler löscht keine Suchbeobachtung und pausiert nicht
still die übrigen lokalen Medien.

Ist das Danbooru-Plugin deaktiviert oder läuft es nicht, werden neue Suchen,
Postabrufe, **Original laden** und Befehle für Suchbeobachtungen mit dem Hinweis
abgelehnt, dass das Plugin nicht verfügbar ist; es läuft auch keine Prüfung. Eine
Prüfung, die beim Deaktivieren noch läuft, wird verworfen und legt keine Baseline
fest. Bereits in die Downloadwarteschlange aufgenommene Originale bleiben dort und
folgen den normalen Warteschlangenregeln; gespeicherte Suchbeobachtungen und der
lokale Katalog bleiben erhalten. Beim erneuten Aktivieren startet die Planung der
Beobachtungen aus dem gespeicherten Katalog neu. Sobald das Beenden beginnt,
stoppen die Beobachtungen zuerst und neue Danbooru-Anfragen werden abgelehnt.

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
standardmäßig eine Schreibgrenze von **32 MiB**. Unter **Einstellungen → Speicher und Grenzen**
lässt sie sich auf **8 bis 128 MiB** setzen. Ab 75 % warnt der Beobachtungsbereich
auf der Booru-Seite und nennt die Einstellung. Eine kleinere Grenze löscht keinen
vorhandenen Katalog; sie verhindert nur weiteres Wachstum, bis wieder Platz unter
der Grenze vorhanden ist. Der feste technische Lesedeckel von **128 MiB** bleibt
unabhängig von dieser Schreibgrenze bestehen.

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
