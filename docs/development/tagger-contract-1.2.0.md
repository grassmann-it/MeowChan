# Tagger contract 1.0.0 (MeowChan 1.2.0, package P10)

Status: **contract, store, host runner, AP11 review domain and Desktop view of
saved suggestions implemented**. Interactive review and Desktop job launch
are pending. Decisions marked *open* below are proposals until Tom confirms them.

Sources: `.architecture-sparring/1.2.0-marketplace-editionen-ki-planung-2026-11-20.md`
(§4, §5, §10, §11, §13), `release-zuordnung-1.1.1-1.2.0-2.0.0.md` (P10 before P8
audio), `recherche-booru-tagger/VERGLEICH-UND-EMPFEHLUNG.md` (E10, E11, AI1).

## 1. Purpose and boundary

A tagger proposes tags for media. It never writes tags. Every result becomes a
*suggestion* in a review state; only the user turns it into a confirmed tag
(accept) or discards it (reject). The contract is the same for every way a
tagger may arrive later: a C# plugin (out of process like source/filter
plugins), an ONNX model run inside such a plugin (WD14/SwinV2, JoyTag,
MobileNetV3), or a plugin that wraps a separately installed CLI. Planned CLI/MCP
access (§13) should write through the same suggestion store, not around it.

Out of scope here: the tagging page, model-pack download/installation
(marketplace), worker protocol messages, audio media (P8), video frame
extraction.

## 2. Contract (`MeowChan.PluginSdk`, `TaggerContracts.cs`)

Versioning: `TaggerContract.Version = "1.0.0"`, accepted exactly (ordinal), and
independent of `SdkContract.Version`. A plugin with taggers declares SDK `1.1.0`
(the SDK constant is a shipped value and stays unchanged; see open decision 1).
All additions are new types or init-only properties; no shipped signature
changed (RS0016/RS0017 clean, entries in `PublicAPI.Unshipped.txt`).

| Part | Type | Notes |
| --- | --- | --- |
| Capability | `ITaggerPlugin.TagAsync(request, context, progress?, ct)` | independent of `ISourcePlugin`/`IImageFilterPlugin` |
| Declaration | `PluginDescription.Taggers` (init, omitted from JSON when null) | a tagger-only plugin is a valid capability; `Blocked` must not declare taggers |
| Tagger | `TaggerDescription(Key, Name, Description, ContractVersion, SupportedMedia, SupportedMimeTypes, Models, Settings)` + init `NetworkHosts`, `MaximumInputBytes`, `MaximumBatchItems` | settings use the new `SettingScope.Tagger` |
| Model | `TaggerModelRequirement(ModelId, Revision, License, SizeBytes, Sha256?)` | models are installed data packs, never bundled or fetched by a tagger |
| Input | `TaggerMediaInput(ItemId, Kind, MimeType, Length, ContentSha256, AssetToken, PixelWidth?, PixelHeight?, DurationMilliseconds?)` | `ItemId` is batch-local and opaque; bytes only via `context.Images` with the token |
| Batch | `TaggerBatchRequest(TaggerKey, BatchId, Items, MinimumConfidence, MaximumSuggestionsPerItem, TaggerSettings, ConfigurationRevision)` | floor = lowest selectable threshold (5 %) |
| Output | `TaggerBatchResult(TaggerKey, BatchId, Provenance, Items)`, `TaggerItemResult(ItemId, Outcome, Suggestions, ReasonCode?)`, `TaggerSuggestion(Name, Category, Confidence, Range?)` | `Confidence` is the raw score in [0, 1]; `Range` (ms) only for video/animation |
| Provenance | `TaggerModelProvenance(ModelId, ModelRevision, ModelSha256?, Runtime?)` | must name a declared model (hash must agree when both are given) |
| Outcomes | `Tagged, Unsupported, TooLarge, ModelUnavailable, Cancelled, Error` | only `Tagged` carries suggestions and counts as done |
| Categories | `Unknown, General, Artist, Copyright, Character, Meta, Rating` | order follows `BooruTagCategory`; `Rating` is a content rating, never the user's stars |
| Progress | `TaggerBatchProgress(CompletedItems, TotalItems)` | in-batch; the host reports job progress across batches |

Validation (`SdkValidation.ValidateTaggerRequest/ValidateTaggerResult`, plus the
description rules): exact echo of tagger and batch, every requested item
answered once and nothing else, suggestion cap, trimmed names of at most 256
UTF-8 bytes without control characters, unique (name, category, range) per item,
finite confidence between floor and 1, ranges inside the item's duration.
Issues never echo tag names, items or tokens.

Fixed ceilings (`TaggerLimits`, not user limits): 16 taggers per plugin, 64
items per batch, 256 MiB input (images additionally 32 MiB, the SDK image
budget), 512 suggestions per item, 8 models, 8 network hosts, 24 h ranges.

## 3. Host: registration and runner (`MeowChan.PluginHost/Taggers`, internal)

- `TaggerRegistry.Register(description, binding, invoker)` validates the whole
  description and registers each tagger; duplicates and tagger-less or blocked
  plugins are refused. The invoker is a delegate: in-process today (tests,
  reference double), the worker client later.
- `TaggerJobRunner.RunAsync(job, progress, ct)`: consent and model checks first
  (`ConsentRequired`, `ModelUnavailable` without calling the tagger), resume
  skips media already tagged by this tagger and model revision (`Rerun` is an
  explicit user action), type/size limits exclude media before anything is
  sent, batches of `min(BatchSize setting, tagger ceiling)` and SDK validation
  on both sides of the invocation. A host deadline stops waiting for the
  invoker and closes the batch services. The commit checks the same lifetime
  before atomic replacement. Finished batches survive cancellation. Invalid
  batches count as failed. A timeout, loss of consent or store failure ends the job.
- The asset reader serves the current batch's random tokens in SDK chunks up to
  64 KiB. It reserves cumulative bytes, reads and concurrent reads before
  calling the host media reader, including reads at EOF. A read that finishes
  after the batch closes cannot return bytes to the invoker. Progress advances
  through at most one report per completed item.

## 4. Security and consent rules

1. Offline first: a tagger without `NetworkHosts` gets an `IHostHttp` that
   refuses every request (`tagger_network_denied`).
2. A network grant covers one active registration, the host-verified execution
   revision and the snapshotted HTTPS origins. The service checks that grant
   revision and batch lifetime for every call. Revoking, re-granting or
   unregistering cancels the old grant and its active calls. Only declared
   origins on the standard HTTPS port are eligible. The service validates
   request headers and enforces the batch HTTP request count. Its host-supplied
   transport must disable redirects and own the network time and byte budgets.
3. No bundled models and no downloads by taggers: required models must be
   installed (`ITaggerModelCatalog`; the default reports none). Download, hash
   pinning, licence and size display belong to the marketplace `model-pack`
   type (§2/§10 of the planning note).
4. The runner writes proposals only. The review domain applies a confirmed
   AP06 preview to the private tag store using the journal protocol in §7.
5. Results are untrusted input: validated against the request before storage;
   diagnostics stay constant and never echo content.

## 5. Persistence (`libmeowchan/Tags/TagSuggestionStore.cs`, internal, `tag-suggestions.json`)

- Format `meowchan-tag-suggestions`, version 1 for suggestion-only profiles
  and version 2 after the first journal write, with its own revision. A writer
  uses one fixed sibling staging file under the profile lease and store-writer
  admission. The primary is the commit record. Each file is bounded by the
  configured size during normal writes. Run commits leave a configurable share
  of the limit available for review writes.
- Each persisted tagger proposal is a `StoredTagSuggestion`. Suggestions are
  bound to content (SHA-256 + length) and provenance (plugin,
  tagger, plugin version, model id, model revision, model hash), never to an
  archive occurrence: accepting applies the tag to the occurrence the user is
  looking at.
- States are `Pending → Accepted | Rejected`, with undo back to `Pending` and
  `Superseded` from a successful rerun. Each transition appends a review event
  with its sequence, suggestion ID, previous and new state, and timestamp.
  AP11 events also reference the journal operation ID.
- Reruns supersede the previous pending suggestions for the medium and tagger,
  including same-revision reruns. Superseded rows and events keep their
  identities. A decision to accept or reject the same tag for these bytes
  suppresses further proposals across taggers.
- Run records (medium, provenance, outcome, count, time) make jobs idempotent;
  only `Tagged` counts as done, other outcomes are retried. The local worker
  records the host-verified manifest SHA-256 with the model weights hash.
  Resume requires both digests to match. Older runs without a manifest hash
  remain readable and are processed on the next run with a verified manifest.
- Raw scores are stored; the threshold only filters the view
  (`PendingFor(media, threshold)`), so changing it needs no re-run.
- An unreadable or newer file is preserved and makes the store read-only.

## 6. Settings (`TagSuggestionSettings`, each default/min/max, binary units)

| Setting | Default | Min | Max |
| --- | --- | --- | --- |
| Confidence threshold (view, %) | 35 | 5 | 95 |
| Batch size (media per call) | 8 | 1 | 64 |
| Largest medium sent (MiB) | 32 | 1 | 256 |
| Suggestions kept per medium | 64 | 1 | 512 |
| Batch time limit (s) | 120 | 10 | 3600 |
| Suggestion file size (MiB) | 32 | 4 | 256 |
| Share reserved for review writes (%) | 10 | 5 | 50 |
| Cumulative read budget per batch (MiB) | 256 | 1 | 1024 |
| Cumulative reads per batch | 8192 | 1 | 65536 |
| Concurrent reads per batch | 4 | 1 | 16 |
| HTTP requests per batch | 16 | 1 | 128 |

They are domain settings only and **not wired to the desktop settings page**
yet (no visible effect until a tagger can run). The wiring belongs to the
tagging-page package and must then follow the settings checklist: EN/DE texts,
10 packs, KnownMissingKeys, audit counter, Reset/Transfer/Validate/Clone/CopyFrom.

### Remaining hardening

- AP10 owns the production Worker route, enforced worker termination and restart,
  IPC budgets and binding reads to verified asset handles. The current runner's
  in-process invoker is a trusted seam for tests and the reference tagger. A
  non-cooperative invocation may continue after the host returns its timeout.
- Use a host-owned, redirect-disabled metadata transport when networking is
  wired into AP10. Target address restrictions and host-verified model
  selection, provenance and revision ordering remain open.
- Keep legacy `.corrupt-*` copies until a migration can identify and handle them.
  Bounds during serialization, a per-medium index, pruning policy and
  platform crash-durability tests remain open. The review reserve is finite.
- Keep the settings in the domain until an executable tagger is wired to the
  desktop. That integration supplies EN/DE and the ten bundled packs together
  with the localization audit updates.

## 7. AP11 review journal

- `AcceptReview` confirms one model proposal from an AP06 preview at one exact
  bound attachment or local occurrence. It checks the suggestion revision and
  the private revision and fingerprint under the same profile lease. The
  journal records the operation ID, proposal ID, occurrence, mapped tag names
  and which assignments existed before the write. The caller revalidates the
  observed media before confirming the preview.
- Acceptance and undo of an acceptance first persist a `Prepared` operation
  in `tag-suggestions.json`. A second atomic write publishes the private
  assignments and a receipt with the operation ID and new private revision in
  `private-tags.json` (version 4, with versions 1–3 still readable). Finalization
  records `Completed` and the review event in one suggestion-store write.
  Each terminal operation advances the suggestion revision once. Review
  preflight checks both possible terminal documents against the existing
  suggestion file limit. Private changes use the existing private tag limits.
- The host calls `RecoverReview` before showing reviews after restart. It
  finalizes a prepared operation when the private receipt matches its ID and
  expected new revision, even if manual edits have advanced the private store
  since. An absent receipt leaves the proposal state as it was and records
  `Interrupted`. A fresh ID and a fresh confirmation are then required to
  apply the requested change. New journal requests recover first. Run writes
  and legacy decision writes wait for recovery by returning
  `ReviewRecoveryRequired`. An identical retry returns the original outcome
  and the current snapshot. A different request with the same profile-local
  ID is refused.
- Rejection and its undo complete in a single suggestion-store write.
  Suppression spans taggers for the same content (SHA-256 and length) and raw
  name, category and time range. Undo reopens the original proposal and ends
  suppression from that decision. A later run may propose other rows.
- `UndoReview` names the original decision and works after restart while that
  decision is current. It removes only exact assignment rows still owned by
  that acceptance. Pre-existing assignments, later additions and manually
  reasserted or re-added assignments stay. A later review of an already
  assigned tag releases earlier ownership, as do renames and merges for
  affected tag definitions. Vocabulary, occurrence registrations and tags at
  other occurrences stay. Undo records which owned names were actually removed
  and carries no whole-file rollback.
- The three atomic writes cover process interruption under the profile
  lease. Independently restoring an older copy of either file can break their
  correspondence. Receipt and assignment ownership data are private to the
  profile.

### Desktop integration

- The tagging queue's **View model suggestions…** action opens saved results
  for one selected medium. It shows the raw tag, category, score, model and
  tagger provenance, and review state. The confidence threshold applies to
  pending proposals. Decisions and superseded proposals remain visible.

Remaining work:

- A "Suggested" group next to provider and own tags for the current medium:
  `PendingFor(sha, threshold)`, sorted by score, with category, score and model
  shown; accept/reject per chip and for all shown; existing `Alt+1–9` slots can
  take the top suggestions (check macOS collisions).
- Use the journal for accept, reject and undo. Supply the profile's configured
  private tag store. After a revision conflict, reload the preview and ask the
  user to confirm the current occurrence.
- Job UI: run for selection/queue, progress, cancel, resume, "re-run with new
  model revision". Model and consent prompts come from the marketplace/model
  pack UI.
- Reference: `tests/MeowChan.PluginRuntime.Tests/ReferenceTagger.cs` is a
  deterministic test double (no model) usable for UI tests.

## 8. Open decisions for Tom

1. SDK version: keep `SdkContract.Version = 1.1.0` for 1.2.0 (tagger contract
   versioned on its own), or bump to `1.2.0` at release (changes a shipped
   constant: release review entry).
2. Persist network consent (per profile file) or keep it per session as now?
3. The AP11 domain keeps the existing cross-tagger rejection scope. A future
   per-tagger policy would need a migration.
4. Target of accepted `Rating` suggestions: map to a content-rating field, a
   private tag, or ignore the category for now?
5. Mapping of tagger names to own tags: keep model vocabulary (`long_hair`) or
   normalise (underscores to spaces), and how it meets the planned alias layer
   (P7/E10, "Tag-Verwaltung vor dem ersten KI-Tagger").
6. Video/animation input: frames sampled by the host (PTS, scene cuts, lab §11)
   or bytes to the tagger? Today video items are declared but the only reader is
   the chunked byte reader.
7. Worker route: extend the private worker protocol for `ITaggerPlugin` (with
   progress messages) in 1.2.0, or ship the first reference plugin in-process?
8. Pruning: should superseded suggestions and old run records be removed after a
   time or count (setting), or kept as history?
9. Desktop settings exposure: all six settings visible, or only threshold and
   batch size with the rest as advanced?

## 9. Tests

- `tests/MeowChan.PluginSdk.Tests/TaggerContractTests.cs`: description rules,
  wire shape (no `taggers` key without taggers, round trip), ABI (independent
  interface, init-only property, contract version), request and result
  validation, time ranges, no echo in issues.
- `tests/MeowChan.Tests/TagSuggestionStoreTests.cs`: pending/threshold view,
  persistence, run outcomes, decisions with history and undo, stale revision,
  supersede vs. decisions, rerun replace, per-item cap, invalid input, damaged
  and newer files, size limit, settings ranges.
- `tests/MeowChan.Tests/Tags/TagReviewJournalTests.cs`: deterministic model output
  and failures at the private write boundary, recovery after reopening the
  profile, duplicate requests, rejection scope, and ownership-limited undo for
  bound and loose media.
- `tests/MeowChan.PluginRuntime.Tests/TaggerContractRunnerTests.cs` with
  `ReferenceTagger`: full job with progress and provenance, resume and rerun,
  cancel and resume, failed/invalid batches, exclusions, missing models,
  offline network denial, consent scope, registration, batch-bound asset reader.
