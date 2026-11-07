# W31: honest v4 inventory within the existing native-transfer flow

Technical implementation decision under accepted M1; no scope reduction or full M1 acceptance.
Base W29 a605f5f plus separately frozen W30 retained-stage/Host apply. Root now validates W30;
this disjoint next code change must not modify its source, tests or frozen snapshot.

- Extend existing passive NativeTransferImportPreview, not a second importer. Admit validated
  public chan-v4 and keep all old counts. Private input is still rejected until receiving consent exists.
- Add non-positional init properties DerivedPreviews and VideoPosters (TransferImportStatusCounts,
  zero default) to aggregate and per-thread preview records. Counts include both current and retained
  attachments, separate from old Preview-role observations. A retained representation never creates a post.
- Keep contained/omitted/unavailable derived counts separate; never count derived bytes as original
  success/playable video. UniquePayloads/UniquePayloadBytes still count globally measured payload once.
- Immutable DTO-versus-captured-catalog comparison must compare nested representation values,
  nondefault arrays, complete source revision/byte evidence and order, not ImmutableArray reference
  identity. Full existing validator and cancellation/caps remain; no untrusted arbitrary enumeration.
- Existing preview-only dialog will display extra counters with localized labels. It stays inventory
  only. W30 actual-apply backend still explicitly rejects v4 until durable representation preservation,
  recovery, roundtrip and private/multithread contracts are integrated; these remain required M1 work.
- Core worker owns only libmeowchan/Transfer/NativeTransferImportPreview.cs and NEW
  tests/MeowChan.Tests/Transfer/NativeTransferV4ImportPreviewTests.cs; existing tests are retained.
  Root alone handles UI/localization, state, fresh isolated runners and selective commits.
