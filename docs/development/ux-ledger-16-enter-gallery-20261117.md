# UX-LEDGER-16 — Enter opens the focused medium

## Decision: unify, not two scoped commands

`media.open` and `gallery.open` both open the gallery on the identified medium. They do not describe different user outcomes. A Media-only name, a second shortcut-editor row and separate rebinding state buy nothing; runtime scopes would not justify them under protocol 1.10a / R8. The canonical command is **gallery.open**, label **Open in gallery**, icon **image**, default **Enter**, alias **Shift+Enter**. No G default is needed: it would spend another single-letter slot without shortening the hot path.

The old `media.open` identifier is a compatibility lookup, not a second catalog entry or editable command. Old saved overrides migrate in memory to the canonical command, including Unbound. An explicit `gallery.open` override wins if both old names exist; otherwise the former Media override wins. This avoids silently discarding a deliberate gallery binding or inventing an invisible union of keys. Reset clears the migrated choice. A custom override continues to replace the entire gesture set, so the default alias is not a hard-coded backdoor.

Enter on a **thread/post** is still open/activate that focused object, not an instruction to substitute the first gallery image. Those existing object-activation commands share the *meaning* of Enter, not an unrelated action hidden behind runtime scope. The conflict engine allows precisely the unmodified Enter activation family; other gestures still get normal conflict checks. Thread-list routes prefer Read; reader post activation retains its earlier policy. Focused media route before their containing thread/post. Text and action buttons are not media.

## Contract matrix

| Focus owner | Enter | Shift+Enter | Pointer contract |
| --- | --- | --- | --- |
| Media grid tile | Gallery at that tile; multiple selected tiles remain the gallery set, starting at the focused member | Same gallery command | Unchanged selection/preview and double-click opening |
| Archive medium tile/list row | Gallery at the focused row | Same command | Unchanged |
| Reader sidebar thumbnail | Resolve focused item before opening, not stale displayed index | Same command | Selection/preview unchanged |
| Reader inline preview (implemented as a button) | Exactly one medium-button Click and direct gallery opening at its attachment | Same command | Ordinary Click still uses the configured preview policy |
| Media panel picture | Gallery at the displayed medium, not unrelated grid selection | Same command | Preview-only click unchanged |
| Filmstrip tile | Activate its index and return to gallery picture | Same command | Unchanged |
| Ordinary button / tag field | Native button activation / tag commit | Never intercepted as a medium | Unchanged |
| Thread/post | Existing focused-object activation | Existing explicit gallery path | Unchanged |

This revises PKG-B's two-key default, not its identity, single-Click, text ownership or focus-decides-scope invariants. PR #56's Primary+Enter commit-and-advance and Primary+Alt+Enter reader expansion are retained. Effective bindings, not literal Enter branches, determine medium opening.

## Evidence and limits

Validation receipts are appended below after the focused DesktopTests run. The added focus test uses an actual scoped headless window and focuses medium → button → tag editor → medium, asserting exact gallery entry, one native button activation, one persisted tag and the legacy alias. This is managed/headless input evidence, **not native Windows/macOS/Linux keyboard certification**. No full solution build or GUI smoke suite is part of this package. Updated reader parity smoke scenes are left for CI/native acceptance.
