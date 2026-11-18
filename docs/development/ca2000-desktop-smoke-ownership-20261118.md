# DesktopSmoke CA2000 ownership package — 2026-11-18

## Scope

This is a separate integration package. It does **not** modify UX PR #125 or its
branch. The worker started at integration HEAD `605a9d3f3` on
`codex/auftrag3-ca2000-integration-20261118`.

The common target `origin/goal/gesamtprogramm-profile-a` was fetched and merged
with the required normal command:

```text
git merge --no-ff origin/goal/gesamtprogramm-profile-a
```

Git reported `Already up to date` because the target commit `659f247c5` was
already an ancestor of the integration HEAD. No rebase or force-push was used.

## Ownership changes

Exactly eight CA2000 diagnostics were fixed in the five existing DesktopSmoke
files named by the integration failure:

- `tests/MeowChan.DesktopSmoke/MediaGalleryReturnChecks.cs`
  - explicit `await using` ownership for the session host;
- `tests/MeowChan.DesktopSmoke/GalleryTransitionReviewChecks.cs`
  - explicit disposal of the test-installed `GalleryVideoPlayer`, followed by
    clearing the reflected viewer field so the viewer cannot dispose it twice;
- `tests/MeowChan.DesktopSmoke/ManualImportChecks.cs`
  - explicit `await using` ownership for the session host;
- `tests/MeowChan.DesktopSmoke/PostBookmarkGalleryReturnChecks.cs`
  - explicit `await using` ownership for the session host and shell;
- `tests/MeowChan.DesktopSmoke/PreferenceStartupChecks.cs`
  - explicit `await using` ownership for the first and reopened session hosts
    and reopened shell.

No analyzer suppression, baseline change, product-code change, rebase, or
force-push was introduced.

## Validation

The locked, no-restore analyzer build passed:

```text
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj \
  --configuration Release --no-restore -m:1 -nodeReuse:false \
  -p:UseSharedCompilation=false -p:RunAnalyzers=true -v:minimal

0 Error(s), 309 Warning(s)
```

There are no remaining CA2000 diagnostics in the five package files. The
warning count is existing repository analyzer debt; this package does not
expand that scope.

Focused native smoke results from the current integration HEAD:

- **Passed:** `manual-import`;
- **Passed:** `post-bookmarks` (covers the post-bookmark gallery return path);
- **Blocked by pre-existing integration failures:**
  - `media-page`: missing control `MediaSelectionState` in
    `MediaPageChecks.HeaderStability.cs:144`;
  - `reader-ux`: timeout in `ReaderInteractionChecks.cs:788`;
  - `preference-startup`: existing close-failure cell reports
    `FinalWriteUnavailableException: Final preferences unavailable: Stopping without authority`;
  - the isolated `MEOWCHAN_GALLERY_TRANSITION_REVIEW=1` run reaches the current
    test/product mismatch `InvalidCastException` (`VideoOperationGate` versus
    `SemaphoreSlim`) before the changed disposal path.

The three blocked scenarios are not caused by the five ownership edits and
remain documented rather than broadened into this package.
