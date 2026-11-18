# Auftrag 3 Audit A: Late Scrub Decoder Ownership

## Scope

This audit covers `Desktop/VideoScrubPreviewSession.cs` around the native decode call and its gate-release cleanup, plus the non-cooperative decoder coverage in `tests/MeowChan.DesktopTests/VideoScrubPreviewTests.cs`.

## Evidence

- `DecodeAsync` receives the verified stream as an ownership transfer. The session only disposes the stream when the decoder call throws synchronously before returning a task; otherwise the decoder owns it, including cancellation and failure.
- If cancellation reaches the load while the decoder task is still running, the load waits for `VideoScrubPreviewSession.NativeGrace`. After that grace expires, it leaves the shared decode gate held and attaches a completion continuation that releases the gate only when the decoder task settles.
- The decoder task remains responsible for its stream cleanup. The production decoder uses an async-disposable decode session, whose completion path disposes the native session and verified stream. The session does not dispose that stream after the grace timeout.
- The added test `AnUncooperativeDecoderCanAccessItsStreamAndTokenAfterGraceUntilCompletion` deliberately returns from `DisposeAsync` after grace while the decoder is still held, then releases the decoder and verifies that it can still read the stream and register the token before task completion. It also verifies that the gate remains occupied until completion and that the stream is disposed afterward.

## Conclusion

The suspected late-access issue is not reproducible under the decoder contract. Ownership remains with the incomplete decoder task after the grace timeout, and completion releases the gate and completes stream disposal. Completion-owned disposal of the session's decode resources is therefore not required; no product code change is made.

The focused test/build attempt is currently blocked by an unrelated base-branch compile error: `Desktop/Views/Media/MediaPage.cs:220` references the missing `_contentHeader` field. That unrelated error is outside this audit scope and was not changed.
