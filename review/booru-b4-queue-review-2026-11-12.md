# B4 – Booru-Originale über die bestehende DownloadQueue (12.11.2026)

Bezug: `docs/architecture/gesamtprogramm-2026-10-31/pakete/1.1.0-booru-arbeitsplan-2026-11-12.md`, Paket B4.
Basis: `3c675786` (Branch `claude/booru-b4-downloadqueue-20261112`). Kein Live-Netzwerk, nur In-Memory-Fake-CDN.

## Ist-Befund vor dem Patch

- `DownloadQueue` ist an `IThread`/`IImageBoard` gebunden und verlangt im produktiven Journalmodus
  (`ConfigureWorkState`) für jedes Item einen per `BindWork` gebundenen `WorkResource`. Ein Booru-Item
  wäre dort still abgelehnt worden (`PrepareDurableAdmission` → `false`).
- Ein Booru-`WorkResource` im Chan-Journal hätte `Registry.RestoreAdmittedAsync` bei jedem Start als
  unauflösbare Chan-Watch gemeldet – das wäre faktisch eine Chan-Registrierung gewesen.
- `MediaStore` prüft bereits fail-closed: Transportlänge, Providergröße, Provider-MD5, optional
  SHA-256/SHA-1, misst SHA-256 und liefert erst nach atomarer Veröffentlichung `MediaCommitResult.AssetId`.
- B3 (`BooruCatalogStore`) bietet Claim, Findings und `LinkLocalAssetAdmitted`, aber keinen
  Admission-/Cancel-Intent.

## Umsetzung

1. `libmeowchan/Downloader/ExternalMediaWorkOwner.cs` (neu, intern): Owner-Vertrag
   `IExternalMediaWorkOwner.OnMediaCommitted(item, MediaCommitResult, admission)`.
2. `DownloadQueue`: `JournalFor(item)` liefert für externe Owner `null`; alle item-bezogenen
   Journalpfade (Admission, Run-Check, Publikationsfence, Ergebnis, 404-Deadline, Filter, Quarantäne)
   laufen darüber. Nach erfolgreichem Commit ruft die Queue genau einmal `OnMediaCommitted` mit dem
   Producer-Token des Jobs. Chan-Pfade bleiben unverändert (593 Queue/Store/Booru-Tests grün).
3. `libmeowchan/Booru/BooruOriginalQueue.cs` (neu): Admission-Adapter.
   - Jobidentität `(danbooru, ProviderPostId, original)` über einen Laufzeit-Owner pro Post; Dedupe im
     Adapter und zusätzlich über die `JobIdentity` der Queue, nie über die URL.
   - Nur explizites Original, nur Host `cdn.donmai.us`, Pflicht auf Providergröße + MD5
     (`MissingIntegrityClaims`, `MediaHostNotAllowed`); Sample/Preview werden nie angefragt.
   - Ziel: `<OutputRoot>/danbooru/<postId>/<postId><ext>`; `ContentProfileLease` = Catalog-Lease, eine
     fremde Profil-Queue scheitert fail-closed ohne Request.
   - Nach Commit: genau ein `BooruCatalogStore.LinkLocalAssetAdmitted(...)` mit dem Job-Token.
     Fehler → sichtbarer Zustand `LinkFailed`; Retry/Recovery findet die Datei per strikter
     Store-Restore-Prüfung und verlinkt ohne zweiten Transfer.
   - Pause/Interruption/Cancel kommen aus der Queue; Cancel ist `Cancelled`, nie Erfolg.
   - Recovery: Post mit Finding und ohne `LocalAssetId` = ausstehendes Original
     (`GetPendingOriginals`, `RecoverPendingAsync`).
   - Owner beansprucht keine URL, wirft bei Monitoring/Check/Snapshot, wird nie an Registry, Session,
     Journal oder Archiv gegeben; es entstehen keine privaten Tags.

## Präzise Grenzen / offene Verträge

- **Cancel über Neustart:** B3 hat keinen Admission-/Cancel-Intent. Ein abgebrochener Post bleibt
  „pending“ und würde von `RecoverPendingAsync` wieder aufgenommen. Deshalb ist die automatische
  Recovery noch **nicht** in den Startup verdrahtet. Erforderlich: B3-Feld (z. B. Original-Intent
  `Requested|Cancelled`) oder Root-Entscheidung, dass Cancel nur sitzungsweit gilt.
- **Findings = Admission:** Der Adapter definiert „Finding ohne Asset = ausstehendes Original“.
  B5/B6 dürfen Findings daher nur bei echter Admission schreiben (Watcher-Baseline nur als
  `KnownPostIds`, reine Suchtreffer nicht als `Search`-Finding).
- **Komposition:** Wer `BooruOriginalQueue` mit Registry-Queue, Catalog, OutputRoot und Medienclient
  baut, ist Host/B5/B6. Default-Client ist der bestehende `ProviderHttpClientFactory`-Pfad
  (keine Redirects); B1-HTTP wurde nicht verändert.
- **SHA-256:** Danbooru liefert MD5 + Größe; SHA-256 wird gemessen und an Manifest/AssetId gebunden.
  Ein erwarteter Provider-SHA-256 bräuchte ein B3-Claimfeld.
- **Shutdown:** `StopAllAsync` beendet aktive Jobs ohne Owner-Callback; eine offene `Completion` bleibt
  dann unvollendet (kein falscher Erfolg, aber kein `Cancelled`-Ergebnis).
- Fortschritt pro Post (Bytes) wird nicht separat projiziert; B6 kann `GetThreadCounts` bzw.
  `OutcomeChanged` nutzen.
