# R102 synthetic codec fixtures

CC0-1.0: derived from the existing CC0 `../gallery-playback-vp8.webm` and a
synthetic440Hz sine (not private/user media). `manifest.json` binds bytes, codecs,
source and generator. Native track/control evidence is not physical-audio proof.

Generation (ffmpeg recorded in manifest):

```sh
ffmpeg -i ../gallery-playback-vp8.webm -an -c:v libx264 -preset medium -crf 25 -pix_fmt yuv420p -movflags +faststart playback-h264-silent.mp4
ffmpeg -i ../gallery-playback-vp8.webm -f lavfi -i sine=frequency=440:sample_rate=48000:duration=12 -c:v libvpx -b:v 120k -c:a libvorbis -shortest playback-vp8-audio.webm
ffmpeg -i playback-h264-silent.mp4 -f lavfi -i sine=frequency=440:sample_rate=48000:duration=12 -c:v copy -c:a aac -b:a 64k -shortest -movflags +faststart playback-h264-audio.mp4
```
