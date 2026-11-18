# UX Ledger: Ungrouped Tagging Count

- Symptom: An all-grouped queue is not classified as empty when the `Without group` scope is selected.
- Error class: Tagging scope classification.
- Root cause: `TagQueueSnapshot.CountFor(null)` treated `null` as the whole queue instead of matching entries whose `GroupId` is `null`.
- Fix: `CountFor`, `OpenFor`, `DoneFor`, and `SkippedFor` consistently filter by `item.GroupId == groupId`.
- Test/reproduction: `TaggingPageMenuTests.AGroupedQueueIsEmptyWhenTheUngroupedScopeHasNoItems` asserts zero ungrouped entries and `TaggingEmptyKind.GroupEmpty` for a fully grouped queue.
- Status: Fixed on `codex/pkg3-countfor-null-20261117`; focused test passes.
- Platform limits: No platform-specific behavior identified; the test is pure queue/classifier logic.
