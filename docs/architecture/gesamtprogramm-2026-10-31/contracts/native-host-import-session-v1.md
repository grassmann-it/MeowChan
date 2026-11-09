# W30a exact technical cut — retained native ingress and real Desktop host apply

> **SUPERSEDED for public v4 Apply by [Native v4 Apply v2 — authoritative public single-thread contract](native-v4-apply-v2.md).**
>
> This W30a document is retained as historical implementation context for the host session, state names, and lifecycle safeguards. It is not the current v4 Apply authority; in particular, its blanket v4-unsupported wording is superseded only for the admitted public single-thread shape defined by v2.

Authority: current shortened M1/M2/GP39 goal. Base a605f5f + preserved unrelated WIP.
Root writes Desktop/NativeTransferImportSession.cs and Desktop/DesktopSessionHost.NativeImport.cs only (plus state/docs/runner/Git). This is a backend integration increment toward M1, NOT M1/full import completion, no storage relocation/foreign import/other features. Existing preview-only UI remains unchanged until complete supported flow is ready. No public API/SQL/schema changes in this cut.

New internal Desktop API (Root implementing):
- host.CreateNativeTransferImportSession(string workParentDirectory) -> NativeTransferImportSession. Caller owns existing trusted work parent and session; host must be Ready.
- session.Current -> NativeTransferImportSnapshot(Generation, State, Error, Inventory, Plan, Result, CleanupRetained).
- session.InspectAsync(string sourcePath, CancellationToken=default), ApplyAsync(long expectedGeneration,CancellationToken=default) -> Task<NativeTransferImportSnapshot>.
- session.Cancel(); IAsyncDisposable.DisposeAsync().
- NativeTransferImportState: Idle, Reading, Ready, Unsupported, Applying, Completed, NeedsReview, Canceled, Failed, RecoveryRequired, Closed.
- NativeTransferImportError: None, InvalidSource, InvalidWorkDirectory, InvalidPackage, UnsupportedPackage, PrivateMetadataUnsupported, LimitExceeded, TargetUnavailable, ProfileUnavailable, ReadFailure, ApplyFailure.
- NativeTransferImportPlanSummary(ResourceId Resource,bool IsMerge,int NewPosts,int UpdatedPosts,int NewAttachments,int Conflicts).
- NativeTransferImportResult(NativeMediaImportCommitStatus Status,int NewThreads,int NewPosts,int UpdatedPosts,int NewAttachments,int Conflicts,bool IsReplay).
- Plan/inventory/result are immutable path-free descriptive values, not executable capabilities. Actual prepared plans and retained Stage stay private to session/host.

Behavior:
1. Read/stage once off UI with RegularProfileFile, RejectLinks and real NativeTransferImportStage in private random own child. Source handle closed after validated copy; original later replacement/deletion must not affect apply. No raw source paths/exception text in snapshot.
2. Real read-only host planner first PrepareNativeMediaMergeAsync; MissingTarget then PrepareNativeMediaImportAsync, never skip-existing. Merge-first preserves the broader existing-target retained-binding grammar. Keep real stage + owner-issued plan alive after Ready. New/merge summaries derive real plan, no guessed identities. No writes in preview.
3. Unsupported current core shapes (v4, multi-thread, absent/preview-only originals, private metadata etc.) must stay explicit Unsupported with no target mutation. They remain required M1 followups, NOT new release exclusions. No weakening existing parser/materializer gates.
4. Explicit Apply only for matching Ready generation. Exactly one executing apply; new inspect cannot displace applying request. Borrow Host.RunWithProfileProducerExclusiveAsync's one real token. Call existing admitted import/merge and preserve returned disposition verbatim; never infer SafeToResume from status/cancellation. StalePlan => NeedsReview, no auto-replan/auto-apply. Committed means real receipt, even if cancellation observed late. RecoveryRequired means host remains recovery failed.
5. Cleanup only session-owned temp stage/random child, never recursive/unknown children. Cancel/Dispose joins actual owned work (does not pretend cancellation settled mutation); no source reopening; no user profiles/media/network. Inspect while active returns current no competing IO. Reinspect from terminal/Ready first drains own old stage. Dispose idempotent Closed, waits real active task before cleanup. Cancel during apply requests cancellation but actual reconciled outcome wins; cancel Ready invalidates apply and cleans owned stage.
6. Test hooks Root: session.BeforeSourceOpenForTests : Func<CancellationToken,Task>?; session.AfterPreparationForTests : Func<CancellationToken,Task>?; session.BeforeDirectoryCleanupForTests : Action<string>?; host.BeforeNativeImportCommitForTests : Func<Task>? (inside real Exclusive, before core); host.NativeImportCheckpointForTests : Action<NativeMediaImportPhase>? forwarded into real commit. These only observe/gate/fail actual work, never replace outcomes or admission.

Worker scopes individually assigned. No builds/tests/Git/state/runner/cleanup/shared wiring by workers. Edit directly in current workspace, list paths and hashes; Root fresh isolated serial build/tests/reconciliation. Keep synthetic tests bounded. No old test assertion weakening, no reflection/manual tokens for normal host journeys. Read-only field inspection only if needed to inspect real catalog/lease and existing fixture practice, never mutate hidden startup state.

Root refinement: duplicate Apply while Applying for the same generation joins its actual Task. Cancel on settled terminal states is a no-op, so a late UI cancel cannot relabel committed history.
