# C19 — gallery/reader smoke matrix receipt

The gallery-toolbar smoke expectation now exercises the GL5 width boundaries at
760, 1099, 1100 and 1440 DIP. It keeps the existing strict assertions for
primary-control placement, rare-command overflow, back-button geometry, toolbar
height, post-panel image space, menu bounds and focus return. English and German
are recorded at every width, in Light and Dark. The 760/1099 cells use 500 DIP
height; the 1100/1440 cells use 920 DIP height.

Each closed/post-panel cell emits a JSON `SMOKE gallery toolbar metrics` receipt
containing requested size, actual window/viewer/viewport bounds, toolbar bounds,
focused control and visible toolbar-button count. Captures remain enabled.

No GUI/native display was available in this checkout, so no new measured receipt
or screenshot is claimed here. The change is limited to the smoke matrix,
receipt fields and registry wording; production code and product contracts are
untouched. Run `MEOWCHAN_GALLERY_TOOLBAR_ONLY=1` with the normal desktop-smoke
runner on a GUI host to collect the real receipts and captures.
