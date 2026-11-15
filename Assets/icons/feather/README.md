# Feather icons for MeowChan

68 unchanged SVG originals from [Feather Icons v4.29.2](https://github.com/feathericons/feather/releases/tag/v4.29.2),
commit [`1b002399e8758fb2bccea4d07312dc9e0f43dcb8`](https://github.com/feathericons/feather/tree/1b002399e8758fb2bccea4d07312dc9e0f43dcb8/icons).
Copyright (c) 2013-2023 Cole Bemis. Distributed under the [MIT license](LICENSE);
retain the copyright and license notice when redistributing these assets.

The 69 files in `masks/` are transparent 48 × 48 PNG raster derivatives, rendered
with rsvg-convert 2.62.3 / Cairo 1.18.4. Use their alpha channel with the existing
Avalonia `ImageBrush`/`OpacityMask` approach and a theme foreground color. They
cover 16–24 logical pixels through 200% display scaling. Larger sizes need larger
derivatives or vector rendering. The original SVGs remain unchanged.
`masks/star-filled.png` is the one filled derivative: `star.svg` rendered the same way
with `fill="currentColor"` instead of `fill="none"`, for the marked favorite state.
`skip-forward` (next frame) and `repeat` (A–B loop) of the video transport were added from the
same release with the same renderer and size; `rotate-cw` (rotate 90°) likewise.
`tag` (the tagging page in the footer) likewise.
`chevrons-up` and `chevrons-down` (move to the start or the end of the tagging queue) are two stacked chevrons in the same stroke
style, written as SVG and rendered with the same renderer and size; Feather's own chevrons-up/down use the same two polylines.

These are prepared assets, not a record of completed UI integration. The local
0.9.0 architecture handoff contains the action map, source and derivative SHA-256
inventory, preview and portable kit under
`docs/architecture/0.9.0/references/ui-icons/`. That planning directory is ignored
by Git. The source/version and license above remain with the asset tree.
