# Verified upstream API gate

Read 2026-10-23 directly from the pinned Ursa source revision `00fef728ce87cc8cbc9085f3162867daa1d57490` used by the package plan (Irihi.Ursa 2.2.0). These are source observations; runtime acceptance requires W20's compiled package execution.

## URSA-09

[PathPicker.cs](https://github.com/irihitech/Ursa.Avalonia/blob/00fef728ce87cc8cbc9085f3162867daa1d57490/src/Ursa/Controls/PathPicker/PathPicker.cs): template setup attaches the private `LaunchPicker` directly to the browse button. `Command` executes after the storage picker and after `UpdateSelectedPaths`; it is not a pre-picker app-delegation hook. `UpdateSelectedPaths` uses `TryGetLocalPath() ?? x.Name`; catch only logs. There is no supported pre-picker hook that retains app control of local-path rejection, visible errors and lifetime.

Decision: complete composite fallback. Keep the real editable TextBox/Browse semantics, isolate selection in `Desktop/LocalFolderPicker.cs`, expose every failure through the existing editor error area, and never adopt Ursa's display-name fallback. No private template interception, fork or package upgrade.

## URSA-17

[Anchor.cs](https://github.com/irihitech/Ursa.Avalonia/blob/00fef728ce87cc8cbc9085f3162867daa1d57490/src/Ursa/Controls/Anchor/Anchor.cs): public `TargetContainer`, attached `Id`, `InvalidatePositions`; its private pointer scroll animates 300ms. Comparison C overrides the public/protected routed pointer/key hooks and does not invoke that native animation, handing one navigation request to the same controller. Actual improvement over B remains unproven. Product default is the simpler explicit list with compact selection; final integrated comparison decides whether C has enough additional value.

## URSA-22

[IntUpDown.cs](https://github.com/irihitech/Ursa.Avalonia/blob/00fef728ce87cc8cbc9085f3162867daa1d57490/src/Ursa/Controls/NumericUpDown/IntUpDown.cs) defines `NumericIntUpDown : NumericUpDownBase<int>`, `Step`, min/max and `int.TryParse`. [NumericUpDownBase.cs](https://github.com/irihitech/Ursa.Avalonia/blob/00fef728ce87cc8cbc9085f3162867daa1d57490/src/Ursa/Controls/NumericUpDown/NumericUpDownBase.cs) exposes protected `_textBox` and virtual `CommitInput(bool)`; there is no public root `Text` property. `OnLostFocus` invokes `CommitInput(true)`. `SyncTextAndValue` force-formats current Value even after invalid parsing; `OnFormatChange` also force-formats. The sample B adapter is deliberately limited to CommitInput/raw protection and spinner-focus preservation. Matrix culture-change rows will determine whether this is enough; do not infer a global replacement from source or from B's happy path.

C tests whether removing existing Avalonia raw/focus overrides is viable. It is a real unmodified NumericUpDown, not an alias of A. Existing production editors are unchanged.

## Downloaded source SHA-256

- `PathPicker.cs`: `fa2073305072203e27479ee93beab426a466f25a34b37f3c06f5f4f5a986b235`
- `Anchor.cs`: `d6bced587e38623d1dbdb5b82ac0895291d1143eee0ebac67e30a251e75fed13`
- `IntUpDown.cs`: `611d8d24fb67ad89f32928fa8703126a0f3c0e9e8b0eccd6582cbe4aae071da9`
- `NumericUpDownBase.cs`: `d142b9bed40df5a4bfb417e8a2bb086746223679669c7aa8af2a4383feff266e`
