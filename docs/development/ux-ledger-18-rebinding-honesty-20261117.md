# UX-LEDGER-18 — honest rebinding

## Review of the interrupted draft

The two-file draft correctly moved quick-tag routing onto EffectiveShortcutBindings, ahead of structural keys, removed local digit aliases and added a real-surface save/serialize/publish/rebind/unbind regression. It was incomplete: the shortcut editor still accepted unrelated meanings on disjoint pages and Reader/Media video bindings shadowed by page commands. Suggestion help also still printed the obsolete Mac gesture.

All rebindable quick-tag actions now use the effective snapshot; Escape, focus movement, focused-chip activation/removal and badge-letter mnemonics remain structural. Text entry retains bare characters and nonempty-field native undo. Capturing shortcuts disables surface routing.

## Platform decision

Keep **Alt** (Option on macOS) for numbered suggestions, matching the authoritative catalog/default binding table on all platforms. The old locally hard-coded Command+digit path was not a platform default: Command+1…9 already belongs to shell page navigation. Turning it into a Mac platform mapping would introduce two unrelated meanings and shadow navigation. It therefore must not survive as an alias *or* as a conflicting platform mapping. Actual platform defaults (Primary-based undo/stamp and Mac quick-tag page navigation) continue to resolve through the effective table. Suggestion keycaps now read the same effective binding as routing rather than a hard-coded Meta string.

## Editor decision (protocol 1.10a)

Runtime context separation alone does not justify two unrelated meanings. Explicit edits are checked across execution surfaces, with named semantic families for genuinely equivalent actions (for example skip in the two tagging surfaces and commit-and-advance). Taking over a gesture removes unrelated owners across pages, not merely owners with overlapping contexts. Reader/Media video fallback execution contexts are included in this presentation/save policy: a binding that a page command would silently shadow is a blocking, symmetric conflict on both table rows.

Runtime fallback and default-upgrade yielding remain intact for legacy profiles; the stronger save policy prevents the editor from promising a new shadowed or semantically contradictory binding. No baseline reset, analyzer suppression, or assertion weakening is part of this change.

**Gallery decision:** unify media.open with gallery.open (implemented separately in UX-LEDGER-16). Both mean opening the identified medium in the gallery. Two names/editable states for that same action add ambiguity, not useful scope. This branch does not create a competing gallery implementation.

## Integration

Current origin/goal/gesamtprogramm-profile-a was fetched and merged (already ancestor at 209caadf6; no rebase/force-push). PR #56's eight shortcut moves remain unchanged. PR #65 is still open, not part of goal at validation time; its effective-tooltip intent is preserved. The overlapping SuggestionGesture helper now reads effective bindings as #65 intends; retain that implementation when integrating the PRs, together with this branch's effective runtime routing. No unrelated open PR was copied into this one.

## Validation

Receipts follow after targeted builds/tests. Coverage is affected DesktopTests and its referenced production graph plus an analyzer capture; no full solution build or GUI smoke suite. Routed managed key evidence is not native OS keyboard certification.
