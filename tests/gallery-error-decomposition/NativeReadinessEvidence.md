# ARM closing-root preflight follow-up

Evidence: run `43938129569`, job `133548071463`, source `ba1842af4`,
`/tmp/ochan-arm-gallery-targeted.zip`, member
`artifacts/closure-gallery-errors/normal/output.log` (full stage output, not just
job-tail summary). Reviewed against parent `b3339ec86` in the existing worktree;
no new/copy worktree, application build or CI run.

Retirement and native clip checks pass. The **first** layout combination then
fails before the seed motion is submitted:

```text
external=False, postOverlay=False, position=460, 166, client=1000, 700,
scale=1, active=True, pixel=960, 735, working-area=0, 25, 1920, 982,
hit=GalleryViewport
Closing-root pointer target is obscured in its actual top-level hit test.
```

This is not the original fourth-case pointer-entry timeout. The target pixel is
on-screen and the selected root active. The artifact establishes a disagreeing
top-level hit snapshot, not whether it was stale or a genuine occlusion. There
was no motion attempt to distinguish those cases. It contains no local hit log.

## Bounded fix

Use the same live target-local hit fallback already used by `MediaNativeInput`
when top-level hit testing disagrees. Log both hits. A live local hit is **only a
preflight condition**, never proof of successful pointer entry. Strengthen BOTH
native motion acknowledgements: the actual event source must belong to the
requested target's subtree in the selected root, the target must be pointer-over,
and the event coordinate must match. A viewport event at the right screen pixel
can no longer satisfy the proof. A real occluder will therefore fail delivery,
not pass on the strength of the local geometry.

No synthetic event, disabled hit-test requirement, retry-click or timeout increase.
The native seed/entry/center sequence, two-second per-motion deadline, working-area
containment, root activation, final footer/root pointer-over predicate and terminal
retirement assertions remain intact. Only `EnterClosingRootAsync` changes among
existing smoke check bodies. Decomposition, decoder checks and matrices are intact.
The pinned-reference update is documented in README.md with the amended source's
content-blob identifier and explicitly reviewed assertion changes.

## Local bounded checks

- `python3 tests/gallery-error-decomposition/check_contract.py`: **3/3 PASS**.
  Includes actual orchestration extraction and source guards requiring subtree,
  pointer-over, same-root and coordinate proof plus unchanged motion timeout.
- Reused `/tmp/ochan-gallery-pointer-check/native` (small prior standalone probe),
  copied the exact amended `EnterClosingRootAsync` into it and linked the real
  `MediaNativeInput` and geometry helper. Build used `--no-restore`,
  `--disable-build-servers`, `-p:UseSharedCompilation=false`, `-nodeReuse:false`,
  an external 60-second bound: **0 warnings/errors**.
- Real XTest on private Xvfb, external 25-second bound: **4/4 successive window
  recreation probes PASS**, with target-subtree native motion proof.
- Negative control in the same minimal runner: a real painted sibling covers the
  target while the target-local hit still succeeds. Real OS motion reaches the
  sibling. **Correctly rejected by the unchanged two-second delivery timeout**;
  it cannot satisfy the new target-subtree proof. No routed synthetic input.

These small probe windows are not the actual gallery matrix and do not reproduce
macOS compositor caching. Logs: `/tmp/ochan-gallery-pointer-check/native.log` and
`native-build.log`. macOS verification remains required: run the isolated
`gallery-errors-layout-roots` selector on the authorized ARM desktop, requiring all
four context/root PASS lines and normal completion. The fix must not be called a
native macOS pass until that evidence exists.
