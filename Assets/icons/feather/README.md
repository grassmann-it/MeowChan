# Feather icons for MeowChan

64 unchanged SVG originals from [Feather Icons v4.29.2](https://github.com/feathericons/feather/releases/tag/v4.29.2),
commit [`1b002399e8758fb2bccea4d07312dc9e0f43dcb8`](https://github.com/feathericons/feather/tree/1b002399e8758fb2bccea4d07312dc9e0f43dcb8/icons).
Copyright (c) 2013-2023 Cole Bemis. Distributed under the [MIT license](LICENSE);
retain the copyright and license notice when redistributing these assets.

The 64 files in `masks/` are transparent 48 × 48 PNG raster derivatives, rendered
with rsvg-convert 2.62.3 / Cairo 1.18.4. Use their alpha channel with the existing
Avalonia `ImageBrush`/`OpacityMask` approach and a theme foreground color. They
cover 16–24 logical pixels through 200% display scaling. Larger sizes need larger
derivatives or vector rendering. The original SVGs remain unchanged.

These are prepared assets, not a record of completed UI integration. The local
0.9.0 architecture handoff contains the action map, source and derivative SHA-256
inventory, preview and portable kit under
`docs/architecture/0.9.0/references/ui-icons/`. That planning directory is ignored
by Git. The source/version and license above remain with the asset tree.

The older PNGs in the parent directory have an unknown export revision. This
source pin applies only to this `feather/` subtree.
