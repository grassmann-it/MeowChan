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
`chevron-down` and `chevron-up` (the open/closed state arrow of the tagging selection state) are the single-polyline mirrors of `chevron-left`,
written as SVG in the same stroke style and rendered with the same renderer and size (rsvg-convert 2.62.3; the same call reproduces `chevron-left.png` byte for byte).
`star-half` (the media rating: edit, set, remove and manage ratings, so a rating no longer shares the favourite's star) is `star.svg`'s outline with
its left half filled (the left points of the same polygon with `fill="currentColor"`), written as SVG in the same stroke style and rendered with the
same renderer and size (`rsvg-convert -w 48 -h 48`).
`help` (the plain question mark of the keyboard-shortcut help command, no ring) is the question-mark stroke of `help-circle.svg` without the circle, enlarged about 1.4 times
about the centre so it fills the 24 grid like the other marks, written as SVG in the same stroke style and rendered with the same renderer and size (`rsvg-convert -w 48 -h 48`).

`panel-left`, `panel-right`, `workspace-expand` and `workspace-shrink` are MeowChan's own drawings, not Feather originals (MIT-licensed
Feather is only their style reference: 24 grid, 2 px round stroke, the frame of `sidebar.svg`). `panel-left`/`panel-right` are that frame with
the divider and the filled left or right part (the side panel the switch controls); `workspace-expand`/`workspace-shrink` are the frame with
two arrows pointing apart or together (viewer focus mode). Rendered like the others (`rsvg-convert -w 48 -h 48`). `sidebar.svg` is no longer
used by a command.
