# Start und Upgrade — Pflichtmatrix für1.0.2

Geplante Prüfungen, **nicht** in dieser Runde ausgeführt. S6 führt durchgehende
Host-/Shell- und entpackte Apphostfälle aus; A2 prüft unabhängig. Keine persönlichen
Profile verändern. Alte Produktstände/Packagedateien mit Hash/Provenienz binden,
nicht nur heutige DTOs mit „old“-Label verwenden.

## Profile und Installationsorte

| Achse | Pflichtfälle | Erwartung |
|---|---|---|
| Neu | bestätigtes leer/neues Profil, Abbruch nachLease/Marker/Setupstart | Setup nur bei echtemNew/PendingNew, Später/X keinSuccess,1MiB/s/5; kein PhantomRecoveryreset |
| Alt1.0.0/1.0.1 | echte erzeugte alte Profile UND aktuelle Produktpaket-Upgrades | Archiv/Watches/Settings/Reader/Favoriten/Notes/Choices bleiben; SourceStart nicht pauschal broken |
| Ort | gleicherPfad überschrieben nachClosedBackup, neuerOrdner, verschobenerOrdner, Neuinstallation | richtige bestehende Kandidaten/erlaubte explizite Zuordnung, keine Abhängigkeit allein vom absoluten Pfad |
| Alte Auswahl | pinned/unpinned,disabled,mehrere Kandidaten,profil-lokale Datei,Hash-/SDK-/Dependencywechsel | Pins/Owner/Quellrechte erhalten oder gezielt verweigert, keine heuristische Autorenschaft/Rechteübernahme |
| Recht/Repair | Preview,Confirm,Deny,X,veralteteRevision/Bytes,partiellblocked | exakt erlaubte Handlung im echten UI, erneuterStart idempotent, kein verkappterProfilreset |
| Recovery | corrupt primary/gutesBackup,beidecorrupt,future,readDenied,lockzweiterProzess,SettingsfehlenmitBestandsdaten | nichtFirstRun, keineDefaultwrites oder grant resurrection, ursprünglicheBeweise erhalten |
| Format | PluginEnvelopev1→v2 undzweiterStart; VersuchaltesProgramm | Originalzustand/Backuppfad belegt; ältereLeser nicht aufv2loslassen, keineDowngradezusage ohneRestorebeleg |
| Laufwerke | Ordnernichtschreibbar/DateistattOrdner/offline/Platzknapp/USB oderNetzshare als eigene benannte Fälle | früh verständlich, UI bounded, laufendeJobs behaltenZiel, nur eigeneProbedatei löschen, kein stillerMove |

Upgradebehauptung muss die tatsächliche Legacy15-Situation schließen. „Failclosed
ist richtig“ allein ist kein Beleg für einen benutzbaren normalen Updatepfad.
Umgekehrt ist „alle verfügbar“ durch Profilreset/Rechte-/Pinlöschung unzulässig.
Wenn die aktuelle Freigabe alte unbelegteKandidaten noch nicht reparieren darf,
minimalen sicheren erweiterten Entscheidungsvorschlag in C102-02 bringen.

## Restore und automatische Fortsetzung

- Watching/Repairing1/5/25gleicheSource und gemischteSources. ErsteCapture-/Reader-
  Verfügbarkeit proThread und Mediendownloads ohne Checknow/Retryall nach Start.
- Globalpause,Sourcesdisabled,Suppressed,Completed undTerminalmedia getrennt:
  nicht pauschal wiederaufnehmen. Bestehende bekannte zulässigeMedien bei normalem
  Metadatawait nicht unnötig auf frischesHTML blockieren; currentpermission/fence
  bleibt Voraussetzung.
- GesundeAdmission/Deferred/Retry-After/429 von echtenHTTP500/Timeout/Worker-/
  Capturefailure unterscheiden. TatsächlicheFälligkeit respektieren, kein60s-
  Zusatzsnooze, kein Busyloop/mehrSourcezugriff alsPolicy erlaubt.
- Mehrfachauswahl3/25→RMB Nichtlead→Check: eingefroreneIDs, jede gültigeWatch
  genau einmal angefordert, Staffelung durchRateLimiter sichtbar, alleErgebnisse
  gezählt; Filter-/Statusupdates/Remove/Readd/Close ändern Scope nicht unbemerkt.

## Bootablauf und wiederholter Start

Je relevante Zelle mindestens: alter Zustand sichern → neuer echterAppstart →
gegebenenfalls erlaubte Bestätigung → Quellen-/Download-/Readerfunktion → sauber
schließen → zweiter neuerStart → Daten-/Choices-/Rechtediff prüfen. Dazu Start
bei gesperrtem Ziel, kurzer Netzstörung, fehlendem Pluginpaket/Compiler/Worker,
langsamer Queue und laufendenMediaretries. Keine Plugin-Netzabrufe inDescribe.

Startupmeldungen gelten nur dann als zulässig, wenn sie einen **real verbleibenden
handlungsrelevanten Zustand** erklären, nicht normaleInitialisierung/Admission.
„Candidate missing“, „desktop session not ready to save“ oder „checksfailed“
bei gesundem unterstützt gestartetenProfil sind keine akzeptierteStandardwarnung.
Fehlerzähler-/Quittierungshistorie behalten, keine Captionflut oder globalerReset.

## Plattform und Evidenz

Windows x64 und Linuxx64 mindestens echte persönlicheTester-relevante Paket-
Szenarien mit synthetischen Profilen; weitere unterstützteRIDs gemäß finaler
nativerReleasepipeline. EN/DE,Light/Dark und tatsächlicheDPI/WindowGeometry für
sichtbareBestätigung/Fehleraktionen. PrivaterNullsink != physischerSound/GPU;
USB/AV/UNC-Fälle ohne echteMessung UNRUN, keine universelleSpeedgarantie.

ProZelle Scope-ID,Quelle/Version/Paket-/Apphost-/Fixturehashes,vorher/nachher-
Profilzustände (privateDetails nichtöffentlich),Befehl/OS/Exitcode/Timeout,
Asserts/Logs/RawCounter,Cleanup undReviewer. Negativprüfer müssen falsche Quelle,
fehlendeChecks/Files,Token-/Ownerwechsel,staleReparatur undunknownProfil abweisen.
