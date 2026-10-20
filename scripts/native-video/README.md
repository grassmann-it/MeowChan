# Pinned upstream packaging evidence

`package-nuget-mac.sh` is the unchanged VideoLAN libvlc-nuget helper introduced at
`d08e89e0c6e8a4137e47f841e47ed24ac7e44467`, identical at inspected revision
`bff3a598ac3d67f2cc5bfdc39293792890ebe292`.

Source: https://code.videolan.org/videolan/libvlc-nuget/-/blob/d08e89e0c6e8a4137e47f841e47ed24ac7e44467/package-nuget-mac.sh

SHA-256: `d3cbfcb50bb7b3721d632d1bc1f0076d5121f56e780c314c076aff1135045b0c`.
It records upstream selection/removal/relocation from the official VLC DMGs.
The 800-file hash mapping independently establishes the shipped input bytes.
The helper is retained as source-packaging evidence; generating an overlay does
not execute it, download DMGs, remove plugins or rebuild the NuGet package.
Its comment/aggregate “LGPL” label does not override the four retained modules'
GPL-2.0-or-later source declarations. See the Mac source manifest and notices.
