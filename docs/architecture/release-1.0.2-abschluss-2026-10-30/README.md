# MeowChan 1.0.2 — begrenzter Abschluss- und Releaseplan

**Stand: 30.10.2026. Zehn Abschluss-Arbeitspakete; Planung, keine heutige Umsetzung
oder Veröffentlichung.** Das Ziel ist jetzt, den bestehenden Stand inklusive der
neu gemeldeten Start-/Batchprobleme abzuschließen und regulär als **1.0.2** zu
veröffentlichen. Nicht erneut alle alten Pakete implementieren oder weitere Ideen
aufnehmen. **Neue Chans, externe Archivimporte und ungewählte CSV-/DATAfeatures
bleiben draußen.**

## Heute belegter Stand

- Basis: `main` / `58915e9aa98f807067ac82441c595ca1fb4af281` plus erheblicher,
  laufend integrierter WIP. Produktversion noch1.0.1. Der alte Handoff vom29.10.
  ist keine aktuelle Quellkopie; dessen Patch niemals über den heutigen Stand legen.
- Bestehende Implementierung liefert bereits große Teile von Readiness, Setup,
  Kompaktstatus/Retry, Reader-/Galerietasten und nicht blockierenden Galeriefehlern.
  GIF-Source13 hat eine vollständige gebundene native Pixel-/Reader-/Ownershipsuite
  bei1×/2× bestanden. Kein Grund, diese Funktionen nochmals neu zu bauen.
- Source14:661Desktop-,98Configuration-,2Startupfälle PASS, aber nur ausgewählte
  Gruppen. Allgemeiner Reader→Galerie→Reader-Rückweg weiterhin FAIL; stummer
  H264-Apphostfall im Bericht weiterhin offen. Neuere Bindungen bei Beginn prüfen.
- BundledCandidateMigration/ApplyBundledStartup/Preview/Confirm sind implementiert.
  **Die historischen15ungepinnten Alt-Kandidaten je1.0.0/1.0.1 bleiben dennoch
  ohne sichere Handlung unavailable.** Das ist ein Releaseblocker, kein erledigter
  Upgradefix. Aktuelles Pluginprofil kann Envelopev2 schreiben; alte Leser lehnen
  dies ab. Backup-/Restore-/Downgradewirkung muss vor Lieferung klar sein.
- Neu gemeldeter Linuxstart: „checks failed – watching“, manuelles Check hilft,
  Gruppencheck scheinbar nur einem. Gesunde Deferred-Admission landet im generischen
  Errorcatch und kann statt RetryAt das60sIntervall abwarten: starker Codekandidat,
  noch keine bewiesene alleinige Runtimeursache.
- Windows25-Thread-Batch: nach1→10MiB/s und Parallel10…1000 trotzdem nur ungefähr
 1–4angezeigter Durchsatz, bei1000vieleTimeouts/60s. Äußerer Media-Headerdeadline
  zählt Admission mit; Puffer256 allein erklärt das nicht. Nicht durch pauschal
  höhere Timeouts/Parallelität oder Queuegrößen als „behoben“ darstellen.

Lektürebasis und Grenzen: [bestehender REPORT](../implementation-run-2026-10-29/REPORT.md),
[Startupbefund](../startup-continuation-2026-10-30.md),
[Scheduler-/Timeoutbefund](../download-scheduling-2026-10-30.md).
Keine neuen Laufzeittests in dieser Planungsrunde.

## Die Pakete

| Paket | Prüffähiges Ergebnis | Reihenfolge |
|---|---|---|
| [C102-00](packages/C102-00.md) | Exakte Quelle, Restumfang und Blockerledger | zuerst |
| [C102-01](packages/C102-01.md) | Automatisches Fortsetzen und funktionierender Gruppencheck | kritischer Fixpfad |
| [C102-02](packages/C102-02.md) | Sicherer, benutzbarer Plugin-/Profilübergang einschließlich alter Releases | unabhängig parallel; Legacy-Policygate |
| [C102-03](packages/C102-03.md) | Timeout-/Überlastursache messen und begrenzt korrigieren | nach01, Queue-/HTTPowner seriell |
| [C102-04](packages/C102-04.md) | Frühe Erfassung, faire Medien und mehrere High-Priority-Threads | nach03; begrenzte V1 im Scopefreeze |
| [C102-05](packages/C102-05.md) | Stummes H264 und echter Reader-Rückweg behoben, bestehende Media-/GIFregression stabil | parallel zu Corefixes |
| [C102-06](packages/C102-06.md) | Durchgehende Start-/Upgrade-/Batchmatrix auf gemeinsamer Quelle grün | nach allen enthaltenen Fixes |
| [C102-07](packages/C102-07.md) | Version1.0.2, echte Lock-/Consumer-/native Paketkandidaten | nach integrierter Abnahme |
| [C102-08](packages/C102-08.md) | Tatsächlich testbarer Kandidat, Windows/Linux-Testfahrt vor Publish | nach07 |
| [C102-09](packages/C102-09.md) | Reguläres v1.0.2 samt öffentlichen verifizierten Downloads | letzter Schritt nach allen Gates |

Diese zehn Pakete **fassen den Rest zusammen**, sie sind keine zehn zusätzlichen
Großfeatures. Bestehende freigegebene Setup-/Recovery-/Migrationsgrenzen gelten.
AktuelleDefaults1MiB/s/5 erhalten; Unlimited, neue Wizards/Backup-Scheduler,
Threadtags/erweiterte Provideridentitäten/PDF/Themes nicht hinzufügen.

## Was „fertig gezurrt“ hier bedeutet

1. Normaler Start und unterstütztes Upgrade brauchen keine Reihe manueller Checks
   oder Profilreset. Ein legitimer Source-/Rechte-/Recoveryblock bleibt dagegen
   sicher, erklärt und mit erlaubter Handlung versehen.
2. Keine bekannten ungelösten Pflicht-FAILs als bloße Warnung weiterreichen:
   Legacy15, stummerH264, Reader-Rückweg, Auto-Continue/Gruppe und Überlastpfad.
3. Priority-V1 darf nur neue ausführbare Plätze bevorzugen, nicht laufende Dateien
   abbrechen, Rechte umgehen oder normale Threads dauerhaft aushungern.
   GrößererPuffer nur bei belegtem Nutzen. Falls V1 nicht begrenzt stabil lieferbar
   ist, **vor Freeze eine ausdrückliche Scopeentscheidung**, nicht still weglassen.
4. Ein eingefrorener 1.0.2-Commit, ein vollständiger aktueller Kandidatenlauf,
   passende Binaries/Sources/Checksummen und unabhängiger Review. Keine Mischung
   älterer Plattformpässe oder des d7d1e06e-Windows-WIP mit aktuellen Paketen.
5. Veröffentlichung ist das erklärte Ziel. Diese Runde plant es; weder ein Tag
   noch ein Push/Release wird jetzt ausgeführt. Bei späterem ausdrücklichen
   Ausführungsauftrag bis Release keine zusätzliche pauschale Freigabeschleife
   für denselben bereits genehmigten Schritt, technische Gates aber zwingend.

## Übergabe

[Start-/Upgrade-Matrix](UPGRADE-MATRIX.md) · [Ausführung/Owner](EXECUTION.md) ·
[Status/Blocker](STATUS.md) · [Fortsetzungsprompt](STARTPROMPT.md) ·
[maschinenlesbare Pakete](PACKAGES.json).
Die dynamische WIP-/Leasebasis muss vor Ausführung neu aufgenommen werden; kein
frischer HEAD-only-Thread und keine konkurrierenden Shared-root-Patches.
