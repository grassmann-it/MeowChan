# Danbooru-Suche und Suchbeobachtungen (MeowChan 1.1.1)

MeowChan behandelt Danbooru in der 1.1-Linie als **öffentliche,
schreibgeschützte Quelle**. Die Oberfläche zeigt Treffer und Provider-Metadaten;
ein Original kommt erst durch **Original laden** oder einen anderen ausdrücklichen
Downloadauftrag in die normale MeowChan-Downloadwarteschlange. Das Markieren eines
Treffers allein startet keinen Download. Es gibt keine Provider-Anmeldung und keine
Provider-Credentials in MeowChan. Für Änderungen an Tabs, gespeicherten Suchen und
Galerie siehe die [deutschen Release Notes für 1.1.1](../releases/v1.1.1.md#deutsch).

Das mitgelieferte Danbooru-Plugin ist standardmäßig aktiv. Öffne **Booru** in der
Fußleiste, unter **Mehr**, über **Danbooru-Suche und Suchbeobachtungen…** im
⋯-Appmenü, über **Medien → Mehr → Booru öffnen** oder per Tastenkürzel (Tabelle
unten). Das
Erkennen einer Danbooru-Adresse und
das Öffnen der Booru-Seite fragt noch nichts ab. Auf der Seite senden erst
**Suchen** oder **Post abrufen** eine begrenzte Anfrage; bereits aktive Suchbeobachtungen laufen unabhängig davon nach
ihrem Zeitplan. Eine Post-Adresse über **Downloads → Hinzufügen** lädt Metadaten
und reiht das Original in einem Schritt als One-Shot ein.

## Booru-Seite, Anzeige und Tastenkürzel

Die Fußleiste und das Mehr-Menü ordnen die Hauptseiten als **Downloads, Archiv,
Medien, Taggen, Booru | Regeln, Plugins | Status**; die kontextbezogene F1-Hilfe
folgt derselben Reihenfolge. Seit 1.1.1 zeigt die Standard-Fußleiste Booru auch
für Profile, die bisher die alte Standard-Fußleiste verwendet haben. Gespeicherte
Ausblendungen bleiben wirksam. Den Knopf kannst du unter **Einstellungen →
Oberfläche → Fußleiste** in der Liste ausblenden. Unter Minimal bleibt Booru auch dann
unter **Mehr**; ab Einfach steht der Knopf bei ausreichender Breite direkt in der
Fußleiste, bei Platzmangel wandert er nach Mehr. Mehr enthält nur aus der Fußleiste
ausgelagerte Ziele, keine doppelten Einträge für Downloads oder Einstellungen. Das
⋯-Appmenü enthält keine Seitenliste, nur den Eintrag **Danbooru-Suche und
Suchbeobachtungen…**. Booru hat als Hauptseite keinen eigenen Zurück-/Schließen-Knopf.

Die Seitentasten folgen der Fußleiste: Strg/⌘+1 bis 9 zählen die direkt gezeigten
Knöpfe von links, danach die von der Arbeitsansicht nach Mehr verlagerten und zuletzt
die ausgeblendeten Seiten. Daneben hat jede Seite einen festen Buchstaben mit
Strg+Umschalt (⌘⇧ auf macOS), der sich nie ändert; Booru hat Strg+Umschalt+B. Mit der
Standard-Fußleiste gilt:

| Seite | Buchstabe | Ziffer in Einfach (Startansicht eines neuen Profils) | Ziffer in Minimal und Erweitert |
| --- | --- | --- | --- |
| Downloads | Strg+Umschalt+D | Strg+1 | Strg+1 |
| Archiv | Strg+Umschalt+A | Strg+2 | Strg+2 |
| Medien | Strg+Umschalt+E | Strg+3 | Strg+3 |
| Taggen | Strg+Umschalt+K | Strg+4 | Strg+4 |
| Booru | Strg+Umschalt+B | Strg+5 | Strg+5 |
| Status | Strg+Umschalt+S | Strg+6 | Strg+8 |
| Regeln | Strg+Umschalt+U | Strg+7 | Strg+6 |
| Plugins | Strg+Umschalt+X | Strg+8 | Strg+7 |

Mit der Standard-Fußleiste hat Booru **Strg+5** (⌘+5 auf macOS). Wenn du den
Fußleistenknopf ausblendest, bleibt die Seite unter **Mehr** und erhält
**Strg+8**. Die Tabelle steht auch in der
[Hilfe](../Help.md#page-shortcuts-and-footer-numbering). Eine gespeicherte Belegung
eines anderen Befehls, etwa Plugins auf Strg+6 oder Strg+8, hat Vorrang vor dem
Booru-Standard und behält ihre Bedeutung; eine gespeicherte Plugins-Belegung öffnet
**Alle Plugins** auf der Seite Plugins. Vorhandene Bereichskürzel für Statistik
und Diagnose bleiben erhalten; die Status-Ziffer öffnet den zuletzt benutzten
Status-Bereich. F1 und die Fußleisten-Tooltips zeigen die wirksamen Belegungen, nicht
einen zweiten Standard über deiner gespeicherten Wahl. Fußleiste und Menü folgen der
aktuellen Verfügbarkeit. F1 kann auch ein ausgeblendetes oder nicht verfügbares
Seitenkürzel weiter auflisten; das schaltet die Seite nicht frei.

Mit F1 öffnest du das gemeinsame Fenster **Tastenkürzel** beim aktuellen Bereich.
Es bleibt offen, während du die Hauptseite weiter bedienst. Du kannst die
Belegungen suchen, nach Bereich filtern, sortieren und bearbeiten; Änderungen
gelten erst nach **Speichern**, Kollisionen verhindern das Speichern. F1 oder Esc
schließt das Fenster wieder; Esc beendet zuerst eine laufende Aufnahme, und bei
ungespeicherten Änderungen fragt das Fenster nach.

**Booru-Aktionen anzeigen** (Einstellungen → **Oberfläche → Fußleiste**) ist ein
gespeicherter, profilweiter Anzeigeschalter, standardmäßig an. Ohne eingebundene
Booru-Dienste fehlen Schalter und Seitenziel; einen Fußleisten-Haken und eine
Appmenü-Entsprechung gibt es nicht mehr. Ausschalten blendet die Booru-Einträge in
Fußleiste, Mehr und Appmenü sowie Medien → Mehr → **Booru öffnen** aus; das
Seitenkürzel öffnet keine ausgeblendete Seite. Von der gewählten Booru-Seite geht es zu Downloads zurück. Wiederanzeigen
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

## Treffer in der Vorschau durchsehen

**In Galerie öffnen** auf einer Trefferkarte öffnet eine größere Vorschau. Enter
oder ein Doppelklick auf den Kartenkörper eines noch nicht gespeicherten Treffers
öffnet dieselbe Ansicht. Ist das Original schon lokal gespeichert, führen diese
beiden Hauptgesten weiterhin zu **In Medien anzeigen**. Schaltflächen, Tags und
Text innerhalb der Karte behalten ihre eigenen Aktionen.

Die Ansicht vergrößert nur die bereits von der Suche geladene, begrenzte
**Vorschau**, nicht das Original. Die Auflösung hängt von der Provider-Vorschau
ab. Fehlt sie oder ist sie gesperrt, erscheint der entsprechende Hinweis statt
des vorherigen Bilds. Öffnen, Blättern und Markieren laden weder Originale noch
eine weitere Ergebnisseite nach.

| Taste oder Aktion | Wirkung in der Booru-Vorschau |
| --- | --- |
| Links / Rechts, Zurück / Weiter | Vorheriger/nächster Treffer auf der aktuellen Ergebnisseite; kein Umlauf am Rand |
| Leertaste / Enter auf dem Bild | Angezeigten Treffer markieren oder die Markierung entfernen |
| Auswahlkästchen | Gleiche Markierung wie auf der Trefferkarte; bleibt beim Schließen und Wiederöffnen auf derselben Ergebnisseite erhalten |
| **Original laden**, Strg+E (⌘+E auf macOS) | Nur das angezeigte Original einreihen beziehungsweise erneut versuchen, nicht alle markierten Treffer |
| Esc / Schließen | Vorschau schließen und den Fokus auf die zuletzt betrachtete Trefferkarte zurückgeben |

Das Downloadkürzel folgt deiner eigenen Belegung für **Exportieren**. Die üblichen
Verfügbarkeits-, Sperr- und Deduplizierungsregeln gelten weiter; eine Markierung
allein startet nie einen Download. Tab sowie die normale Aktivierung mit Enter
oder Leertaste bleiben an den Bedienelementen erhalten. Bewertung und eigene Tags
für lokale Medien stehen in dieser Vorschau nicht zur Verfügung.

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
