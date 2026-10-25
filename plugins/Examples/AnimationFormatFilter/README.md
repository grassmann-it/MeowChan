# File format and animation

`meowchan.filter.animation-format` supplies the `animation-format` filter.
The editable `AnimationFormatFilter.cs` depends only on `MeowChan.PluginSdk`.
It inspects the staged original through the host's asset reader. Filename,
extension and reported MIME do not determine its result. It does not render
pixels, fetch files or change an archive.

Rule settings (schema `1`):

| Key | Values | Default |
| --- | --- | --- |
| `format` | `any`, `jpeg`, `png`, `gif`, `webp` | `any` |
| `animation` | `any`, `static`, `animated` | `any` |

PNG includes APNG. One-frame GIF/APNG/animated-WebP containers count as static;
multiple structurally evidenced image frames count as animated. An animation
flag alone is insufficient. JPEG is static. A rule with `animation=any` reads
only the format-identifying structure, and a known format mismatch can be
rejected without an animation scan.

Animation checks follow complete GIF image/subblock boundaries through the
trailer, PNG chunks through IEND (including APNG sequence/control/data and
announced-frame consistency), or RIFF boundaries and ANMF frame subchunks.
Header/control CRCs are checked for PNG; compressed pixel payloads and their
CRCs are not decoded or fully validated. JPEG classification validates its
frame header. A Match is **not proof of complete pixel decodability**.

All asset reads are at most 64 KiB, use the request's exact asset token and
cancellation token, and stay within the existing SDK image-byte budget.
Known canvas dimensions must fit the SDK pixel limit. Scans stop after 16,384
container blocks (including GIF subblocks); reaching this limit is a technical
error, never a static/animated guess. Optional unknown PNG ancillary chunks
and WebP metadata are skipped by checked offsets, without loading their bodies.
Unsupported PNG critical chunks are rejected.

Stable reason codes:

- Match: `format_match`, `format_animation_match`.
- NoMatch: `format_mismatch`, `animation_mismatch`.
- Unavailable: `unsupported_format`, `unknown_filter`.
- Error: `invalid_rule_parameters`, `invalid_asset_length`, `image_byte_limit`,
  `image_pixel_limit`, `container_scan_limit`, `invalid_container`, `asset_read_failed`.

Cancellation propagates so the host can reject invalidated work. Technical
omissions do not become ordinary rule exclusions. The host remains responsible
for revisions, grants, staging and committing exactly the verified bytes.
This filter does not add filter chaining; the existing effective rule still
selects one plugin filter.
