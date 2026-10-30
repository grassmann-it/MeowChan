# Original synthetic GIF playback fixtures (CC0)

These five GIF files contain only programmatically generated solid colors and rectangles.
No photographs, external images, fonts, personal data, downloads or network access are used.
To the extent copyright applies, the original patterns, encoded fixture inputs, generator,
and generated golden metadata are dedicated to the public domain under **CC0 1.0**;
see the repository's existing [CC0 dedication](../CC0-1.0.txt).
This does not relicense Pillow, SkiaSharp or application code.

Regenerate **offline** with Pillow (recorded generator version: 12.3.0):

```sh
python3 tests/Fixtures/gif-playback/generate.py
```

The generator writes only the five named GIFs and `manifest.json` in this directory.
It is fixture generation, not a Skia decoder test, UI test or runtime playback pass.
Another Pillow version may change encoded bytes: review the complete new manifest then.
Tests consume the checked-in bytes/goldens, not Pillow or regenerated inputs at test time.

| File | Contract |
| --- | --- |
| `animated-loop.gif` | 12x8, three distinct opaque red/green/blue frames, infinite encoded loop. Raw durations 10/60/140 ms; first frame's browser-style effective duration is 100 ms. |
| `finite-loop.gif` | 12x8, three distinct opaque frames, Netscape loop=2: two repeats after the initial pass (three passes total); 40/80/120 ms. |
| `disposal-transparency.gif` | 12x8, four transparent/color-island frames, infinite loop; includes Keep (1), RestorePrevious (3), RestoreBackground (2), Keep (1). Previous content must be restored/cleared before later frames, including non-sequential requests. |
| `single-frame.gif` | 12x8, one white frame, no loop extension (one pass); 100 ms, so Skia's zero-frame-count single-image metadata fallback has the same effective duration. |
| `truncated.gif` | Exactly the first eight bytes of `animated-loop.gif`; invalid/incomplete GIF header, never a usable frame. |

`manifest.json` records each file's byte count/SHA-256 and, for valid GIFs, dimensions,
frame count, raw and effective durations, encoded loop and Skia-compatible repetition
count (`-1` infinite, `0` once, positive N repeats after the first pass). It also records
Pillow's per-frame disposal values and **fully composited** canonical frame hashes.

Goldens are computed with Pillow, independently of the production Skia decoder:
`Image.seek(index)` -> `convert("RGBA")` -> BGRA byte order, channels premultiplied by
`(channel * alpha + 127) // 255`, rows top-to-bottom with no padding. Transparent pixels
use the transparent palette color (zero RGB). The unit checks remove any Skia row padding
before hashing. GIF palette transparency is binary alpha, not partial-alpha blending.

Coverage is codec-level, not a rendered animation claim. Root-owned controller/GUI
acceptance must independently show changing presented pixels over time, pause/resume,
finite/infinite looping, navigation/reopen, stale-frame exclusion and Reader activation.
No audio track, VLC path, frame-predecode cache or new source is involved.
