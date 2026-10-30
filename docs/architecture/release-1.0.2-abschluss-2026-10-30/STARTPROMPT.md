# Fortsetzungsauftrag für die1.0.2-Abschlussrunde

Arbeite in /home/tomka/Desktop/oChan mit architecture-sparring und Projekt-AGENTS.
Lies zuerst config/Notizindex, dann README/STATUS/EXECUTION/PACKAGES.json und
UPGRADE-MATRIX unter docs/architecture/release-1.0.2-abschluss-2026-10-30.
Der aktive Implementierungsrun und dessen LEASES/REPORT sind die Codebasis,
nicht alteHEADs/Patches oder der Windows-Testcommit d7d1e06e. Prüfe aktuellen
WIP/Owner/Source14+Belege, bewahre Benutzeränderungen und geschützteDateien.

Dieser neue Ausführungsauftrag soll den bestätigten begrenzten1.0.2-Fixumfang
fertigstellen und nach bestandenenGates regulär veröffentlichen. Keine neuen
Chans/Archive/Transportfamilien oder optionalenCSV-/DATAprodukte hinzufügen.
VorhandeneGIF-/Media-/Reader-/Setupimplementierungen fertigstellen/regressionsprüfen,
nicht neu erfinden. Defaults1MiB/s/5 bleiben; keine pauschaleQueue1024,1000Worker
oder höhereTimeouts als angeblich bewiesenePerformancekorrektur.

C102-00zuerst lokal. C102-01Auto-Continue/Deferred+Mehrfachcheck undC102-02Legacy-
Pluginupgrade sind hartePflichtfälle. Bestehende sicherePreview-/Confirm-Policy
respektieren: keine fehlendeAltherkunft erfinden, keinePins/Rechte übernehmen,
keinProfilreset. Für darüberhinaus notwendigeLegacyhandlung minimalen sicheren
Entscheidungsvorschlagbringen undnurbetroffenenPfad wartenlassen. SupportedUpgrade
muss am Ende benutzbarsein, sonstkeineReleasefreigabe. Pluginformatv2/Backup/
Downgrade separat klarstellen. GlobalePause/gestoppteArbeit nichtautoresumen.

DanachC102-03Messung/Deadlines/defensiveParallelität, C102-04begrenztefairness/
MehrfachHighprio. Bereits vorhandenesRoundRobin nicht verdoppeln, Manualherkunft
nichtalsHigh missbrauchen. KeineTransferabbrüche/RangeResume/Limitumgehung oder
Starvation. Teile mit SharedQueue-/HTTPfiles seriell unter einemCoreowner;
UPgrade-/Media-/Readerteams können disjunktparallelarbeiten. C102-05echten
ReaderRückweg undstummenH264 schließen, alteNegativbelege nichtUnsupportedwegreden.

Halte max2gpt-6-astra/6gpt-6.1-sol, R0Integration/A1Core/A2unabhängigerReview,
S1DownloadsUI,S2Media,S3Reader,S4Setup/UpgradeUI,S6QA; S5nurgewählteTeilaufgabe.
KeineIdleworker, keineeigenenUnteragenten. Routinebuild/GUIseriellunterbekanntem
flockmit --disable-build-servers -p:UseSharedCompilation=false -m:2; nurprivate
Profile/Xvfb,keinepersönlichenMedien/Displays. AllgemeineTests ohneGIT_*-Overrides.

C102-06aktuelleGesamtintegration unddurchgehendeUpgrade-/Start-/25Threadmatrix;
dannC102-07Version1.0.2+echteLock-/Consumer-/nativeFünfRID-/Apphost-/Sourcegates.
WIPgezieltCommit/CIbereitstellen, nichtsFremdesaufnehmen. NiePässeverschiedener
Snapshotsmischen; tatsächlicheFeaturetestsindieCIaufrufe aufnehmen. Aktueller
1.xAssemblerist assemble-prerelease.py --channel stable, nichtderalte0.9Helper.

C102-08wirklichenKandidatenzumTestenbereitstellen, Downloadsprüfen, beiweiteremBug
neueQuelle/Abnahme. C102-09erstnachPflichtgates: freigegebenenCommit/Tag/Notes/
Inventaralsreguläresv1.0.2veröffentlichen,keinealtenReleasesersetzen. Historischer
1.0.1PublisheristversionsgebundenundvorAnpassungzuprüfen. NachunbekanntemUpload
Remotezustandreconciliieren. AlleReleaseassetsdanachohneLoginmitBytes/Hashesprüfen.
KeinewiederholtepauschaleFreigabefürbereitsbeauftragtenSchritt,abertechnischeund
expliziteLegacy-/Rechtegatesnichtüberspringen. Wennnichtfreigabefähig, genauwelche
Zelle/FAIL/BLOCKEDoffenistberichtenstattpublizieren. KurzeFortschrittsmeldungenjeWelle.
