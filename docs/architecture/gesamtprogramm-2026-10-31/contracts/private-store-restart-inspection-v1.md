# P08a — Constructorfreie private Storeprüfung nach Neustart

Interne Implementierungsnaht innerhalb GP12/13, 02.11.2026. Kein produktives
Import-/Applygate, kein Abschluss des umfassenderen P08-Admissionvorhabens.

## Wirkung

`NativePrivateApplyJournal.InspectStores(operationId)` prüft zuerst ein vorhandenes
historisches Finalreceipt. Ein gültiges Receipt bleibt historisch maßgeblich und
liefert **keine** aktuellen Storezeilen; Request-/Claim-/Storepayloads werden dafür
nicht nachgeladen. Defekte Receipts werden nicht ignoriert.

Für unvollständige Vorgänge werden P07-Claim/Request/Chain und aktuelle Head-/Byte-
Fences geprüft. Nur die im validierten Request gewählten Storearten werden unter
**derselben** ProfileLease synchron gelesen. Die neue statische
`InspectRecoveryFence`-Naht benutzt die bestehenden strikten P06-Parser und
`RegularProfileFile`, ohne normale Storekonstruktoren auszuführen. Insbesondere
kein verstecktes Bookmark-Pendingcleanup, Restore, Migration, Erstellen eines
fehlenden Dokuments oder Lesen/Schreiben von Notizbackups.

Skalares Ergebnis pro gewähltem Store:

- `ExactAfterWithMarker`: exakte erwartete Primärbytes/Schema/Instance/Revision,
  exakter Operations-/ContentPlan-/Storemarker; bei Favoriten zusätzlich exaktes
  erwartetes Backup. Kein Requesthash im Marker, keine zweite Digestimplementierung.
- `ExactBefore`: exakte Vorherbytes samt Backup und ohne Marker. **Nur Stichprobe**,
  niemals kontinuierlicher Nichtstartbeweis oder `NotStartedProven` nach Restart.
- `NoChange`: Vorher und erwartetes Nachher identisch, Primär/Backup exakt unverändert
  und unmarkiert. Keine daraus automatisch erzeugte Journalbeobachtung.
- `Drifted`: lesbar, aber Bytes oder Marker passen nicht; After ohne Marker reicht
  nicht. Ein Marker aus anderem Vorgang oder Plan ist kein Erfolg.
- `Unreadable`: defekte/unlesbare/spezielle Dokumente, fehlende sichere physische
  Directoryidentität oder unsichere Pendingbelege. Belege bleiben unverändert.

Vor/nach IO wird die in-process physische Profilidentität nachgeprüft. Favoriten
prüfen `.bak`, Bookmarks ausdrücklich nicht. Alle passenden `.pending-*`,
`.private-pending-*` und AtomicJsonFile-`.name.*.tmp`-Einträge verhindern ein positives
Ergebnis; auch unbekannte Suffixe/Verzeichnisse/Links werden erhalten. Fremde
Storearten werden weder gelesen noch nach Pendingeinträgen untersucht.

Die Journalprüfung liest den Request nur transient und gibt ihn nicht mit zurück.
Nach den Storelesevorgängen werden die gleichen Journalfences erneut geprüft.
Alle Rowergebnisse enthalten nur Storekind und Inspektionsenum; keine Notizen,
Dateinamen, Pfade, Vollimages oder Ownerhandles.

## Grenzen

Der Aufruf ändert **weder Stores noch Journal**, setzt keine Intents oder Receipts,
resumiert nichts und bestätigt keine Zustimmung. Ein passender Marker ohne echte
P07-Intentgeschichte wird weiterhin nicht zu `Recorded`/`Completed` aufgewertet.
Ein gültiges historisches Receipt ist kein Beweis über den heutigen Storeinhalt.

Physische Directory-/Dateihandleprüfung ist kein Schutz vor beliebig böswilligen
externen gleichzeitigen Schreibern und keine dauerhafte Profilinkarnation.
Keine ACID-/Rollback-/Parent-fsync-/Powerloss-/native WinMac-Garantie. Dauerhafte
Owneridentität, Catalog-/Host-/Registry-/Queue-/Media-/Lifecycle- und Allwriter-
Startup-/Cleanup-/Shutdown-Admission bleiben Pflicht vor produktivem Apply.
Receiving Consent, Konfliktauswahl/-discard, Markerretirement und Desktopworkflow
bleiben gesonderte offene Integrationsschritte.
