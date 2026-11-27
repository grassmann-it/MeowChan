# Current video sources

MeowChan 1.2.0 and later provide one `MeowChan-<version>-current-video-sources.7z`
alongside the nine application downloads. Check its SHA-256 against the release's
`SHA256SUMS`. Open the archive in 7-Zip or run `7z x` in a fresh directory.
The extracted `README.md` explains how to restore the original layout using
the paths in `manifest.json`. Source extraction and path restoration work offline.

| Package | What the manifest resolves |
| --- | --- |
| Windows x64 ZIP | The checked VLC 3.0.24 native tree, upstream contrib inputs, build scripts, patches, license notices and LibVLCSharp 3.10.2. [Windows sources](native-video-sources.md) |
| Linux x64 and ARM64 | Each final bundled tar and AppImage has its own distro binary-to-source mapping, including exact `.dsc` files, original inputs, distro patches and copyright records. The system VLC tars include the managed binding and use the host's VLC. [Linux sources](native-video-linux-sources.md) |
| macOS Apple Silicon and Intel DMGs | Each final DMG SHA-256 binds to the native inventory and signed ZIP evidence. Both map to the exact VLC source revision, contrib inputs, release packaging recipes, signatures evidence and notices. [macOS sources](native-video-macos-sources.md) |

The root manifest records the version and candidate commit. Under `runtimes`,
choose a RID and the package filename. Its record lists the package length,
SHA-256 and component IDs. The `components` map resolves every ID to its source
version, input paths, recipes, patches and licenses. `native_files` maps the
bundled runtime members and their hashes to those IDs. The `files` map gives a
physical archive path, length and SHA-256 for each logical source path.
Shared upstream files retain their original bytes and checksums.

Original VLC and distro archives contain the upstream build trees and patches.
The platform documents describe replacement builds and the available
toolchain provenance. In particular, the macOS source records retain the
same-commit contrib comparison and its unresolved toolchain details. The
AppImage runtime recipes pin the shipped type2 runtime, libfuse and squashfuse
inputs and record the upstream Alpine build environment.

## Format comparison using the published 1.1.2 assets

All four files were downloaded from the 1.1.2 release and checked against its
`SHA256SUMS`. [MeowChan 1.1.2](https://github.com/grassmann-it/MeowChan/releases/tag/v1.1.2)

| Public filename | Bytes | SHA-256 |
| --- | ---: | --- |
| `MeowChan-0.9.0-video-sources.tar.gz` | 725131331 | `bf556da3dd1fb511dafb81f684a0bb6fc38e7cb7b91081e9ac0c307bd0771bfa` |
| `MeowChan-1.1.2-linux-arm64-video-sources.tar.gz` | 606697393 | `460bcde1e44221aae9a3590268cfc3cdc631ed23a55b15307dbe0f811af0741d` |
| `MeowChan-1.1.2-linux-x64-video-sources.tar.gz` | 608376830 | `fb7b3eee28b75e4202702f04d33ae06f3291d2557360d9d1e19df5233619b5bf` |
| `MeowChan-1.1.2-macos-video-source-overlay.tar.gz` | 29573073 | `61ce475b2de14fa4425fe6455b25d0fa797137a102cd47ad1a613d5445ca0b7d` |
| **Published total** | **1969778627** | |
| **Solid 7z trial** | **1336510703** | `6a31348815ddb3a861fdece845b5e0d22346786e933cdaaea12ffeba77f997fc` |

The trial saved **633267924 bytes** (603.93 MiB, 32.15%). It took **272.187 s**
to compress and **3.074 s** to extract on Linux x64 with 7-Zip 26.03. Preparing
the four input trees, including their SHA-256 checks, took 6.471 s. Compression
used `7z a -t7z -m0=LZMA2 -mx=3 -md=32m -mmt=2 -ms=on -mtc=off -mta=off -mtm=off`.
Extraction used `7z x -y` into an empty directory. The archive reported solid
LZMA2. All **1554** logical paths resolved to **947** distinct files and matched
the length and SHA-256 of the original paths after extraction. Inner source
archives retained their original hashes.
