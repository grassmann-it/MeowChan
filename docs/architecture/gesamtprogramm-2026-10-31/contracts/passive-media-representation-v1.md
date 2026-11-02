# GP12/13 producer: passive Medienprovenienz und verlustfreier Reexport

Technischer Umsetzungsschnitt unter angenommenem Profil A/G4/G5, **keine neue
Fachwahl, kein vollständiger Medienimport**. Root ist alleiniger Storageowner.
Der Schnitt liefert Repräsentation/Leser/Reexport; kein Commit-/Journal-/SQL- oder
Desktopimportangebot. Keine bereits bestehende aktive Prüfung wird gelockert.

## Manifest

Gemeinsame exakte `MediaAttachmentBinding` statt zweitem, widersprechendem
Bindinginventar. Additives `MediaAcceptanceKind.PassiveImport` mit verpflichtendem
`MediaPassiveImportRecord` am Binding. Bestehender nichtnull Acceptancevertrag
bleibt kompatibel; bei PassiveImport beschreibt AcceptedAtUtc ausschließlich die
lokale Aufnahmezeit (gleich ImportedAtUtc), niemals Quellenfreigabe. ProfileId,
PluginOwnerId, SourceId, PolicyRevision müssen sämtlich null sein. Size/MD5flags
sind aus heutiger Actual/Providerbewertung, nicht aus behaupteter Remotehistorie.

PassiveProvenienz: nichtleere kanonische Operation-GUID (D), untere SHA256-
PackageFingerprint, lokale ImportedAtUtc, optional IncomingRevisionKey (gleich
exaktem Bindingkey, null bedeutet aus ursprünglichem unkeyed Surrogat lokal
abgeleitet), optionale ursprüngliche `MediaImportedHistory` (nur Strict oder
ExplicitProviderMismatch, Zeitpunkt+Size/MD5flags; keine Rechte-/Ownerfelder).
IncomingRevisionKey=null erfordert History=null (alle bisherigen Wireversionen
verbieten History an unkeyed Eingangssurrogaten). Kein rekursiver Provenienzbaum. DTOs sind Daten, nie Publicationautorität.

Dieser Storageproducer ist original-only; Previewrollen und unvollständige
Legacy-Metadaten brauchen einen eigenen Folgevertrag, keine erfundene URL/MediaID.

Identität, AssetID, heutige Messung und exakte Providerkeys bleiben vollständig
validiert. Passive Daten ohne ursprüngliche Historie dürfen Providerabweichungen
haben, auch SHA1/256: Payload/Actual benennt die gemessenen Bytes, Provider bleibt
unveränderte unbeglaubigte Beobachtung. Eine vorhandene historische aktive
Akzeptanz muss dagegen wie bisher zu Actual passen (keine SHAabweichungen,
Strict/Mismatchflags korrekt). Keine erfundene Historie und kein Downgrade einer
inkonsistenten Historie zu null. Aktive Bindungen dürfen keine PassiveProvenienz
tragen. Für neue Downloads/neue aktive Referenzen gelten unverändert die aktiven
AuthorizeAssessment-/Policy-/Transport-/Cap-/Collisionprüfungen.

Manifest v5 ist nötig für passive Bindings. v1–4 mit passiven Daten sind korrupt;
v1/2 dürfen passive Daten nicht durch ihre alte Leerinventarprojektion verstecken.
Aktive-only Schreibstände bleiben v4, v5 mit passiven Daten bleibt v5. Altschemata
und ihre bestehenden Schlüssel/IDs werden nicht umgeschrieben oder neu interpretiert.
Nullzusatzprovenienz wird beim Schreiben ausgelassen. Doppelte JSONfelder werden
vor DTOdeserialisierung und Legacyprojektion in sämtlichen Versionen abgewiesen,
sodass kein vorheriges passives Kennzeichen versteckt werden kann. Resolver liest
dieselbe exakte Bindung; keine zweite aktive Wurzel, keine neue Legacyinferenz.
Exakter TryRestoreBound darf bereits passiv gebundene lokal erneut gemessene Bytes
zurückgeben, auch bei Anbieterabweichung: keine neue Bindung/Annahme oder Netz-
beschaffung. AuthorizeAssessment/AddBindings für neue Referenzen bleiben streng.

## Transfer

Additives `chan-v3`, ohne neue Metadatenfelder/Privatdaten: dieselbe vollständige
Form wie v2 einschließlich retainedBindings an jedem Thread. Nur v3 erlaubt
contained **keyed** Original-/Preview-/retainedbeobachtungen mit history=null.
Null heißt ausdrücklich: keine historische aktive Akzeptanz behauptet. Es ist
kein Strict und keine Quellenfreigabe. Aktuelle unkeyed Beobachtungen behalten
history=null. Retained bleiben keyed/original und erfinden keinen Post.

Serializer wählt v3 genau bei contained+keyed+history=null, sonst unverändert
v2 bei retained und v1 ohne retained. Alle Identitäts-/Presence-/Budget-/URL-/HTML-
und Messprüfungen bleiben erhalten. Nichtnullhistorie hat in v3 unverändert strenge
Semantik; nullhistorie verlangt trotzdem tatsächliche Payloadlänge/SHA im Paket.
v1/v2 werden **nicht** gelockert. Envelope/schema/requiredFeatures müssen wie
bisher exakt übereinstimmen, unbekannte Versionen bleiben abgewiesen.

Archiveexport projiziert aktive Acceptance wie bisher. PassiveImport projiziert
nur seine optionale ursprüngliche History, niemals die lokale Aufnahmezeit als
Strict/ProviderMismatch; lokale Operation/Package/Profilinfos verlassen das Profil
nicht. Desktop/HTML benennen passive Aufnahme ohne behauptete Quellenfreigabe.

## Nachweise und Restgate

Synthetische Tests: v3 aktuelle+retained/nullhistory, gleiche Bytes getrennte Keys,
Downlevelversuche v1/2 reject, nichtnullhistory-invalid reject, Envelopefeature-
Mismatch, alte v1/v2 Regression; v5 read/resolve/verified bytes, v1–4 reject passive,
Hybrid/Rechteinjektion reject, aktive neue Bindungen weiterhin verweigert,
Reexport ohne erfundene Historie/lokale IDs. Unabhängiger Astrareview und feste
isolierte Build-/Testreceipts. Keine persönlichen Profile/Medien verwenden.

Folgeschnitt bleibt Stage→B-eigene Publication mit Journal/Crashrecovery und
Catalogfences, Konfliktbytes separat/inaktiv, Offline404/Favoritenroundtrip und
sichtbare Auflösung/Verwerfen. GP12/13 bleiben in_progress; dieser Producer
berechtigt nicht zur unjournalisierten Produktaufnahme.
