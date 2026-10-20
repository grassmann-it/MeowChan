# Synthetic gallery playback inputs

`gallery-playback-vp8.webm` and `gallery-playback-vp9.webm` contain a 12-second,
160×90 moving test pattern at 15 frames/second. They contain **no audio** and no
external media, photographs, fonts, or artwork. They test actual local video
frames, timestamps and UI transitions; they do not test physical audio hardware.

To the extent copyright applies, these generated fixture inputs and their
regeneration recipe are dedicated to the public domain under **CC0 1.0**.
See [CC0-1.0.txt](CC0-1.0.txt). This dedication applies to these fixtures, not to
ffmpeg, libvpx, or other repository content.

CI uses the checked-in bytes and verifies their SHA-256 values against
`gallery-playback-manifest.json`. No ffmpeg installation is needed to run them.
Regenerate from the repository root using:

```sh
python3 scripts/generate-playback-fixtures.py
```

Regeneration requires ffmpeg with libvpx/libvpx-vp9. The manifest records the
ffmpeg version, complete encoding commands and hashes. Bit-exact encoding is
reproducible with the same ffmpeg/libvpx build; another encoder version can change
the bytes and must be reviewed with its new manifest.
