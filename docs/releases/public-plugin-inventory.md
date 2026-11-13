# Public desktop plugin inventory

This is the public desktop package contract for the 1.1 line. It is a build-time
allowlist, not a scan of the repository or of a user's profile. The package
contains exactly the 24 bundle identities below, their generated manifests and
assemblies, and the 21 editable example/filter source files listed afterwards.
Danbooru is included as an official post-search bundle.

## Bundled plugin identities

Every row produces `plugins/<identity>.plugin.json` and
`plugins/assemblies/<identity>.dll` in the application package.

| Group | Assembly identity |
| --- | --- |
| Official | `MeowChan.Plugin.FourChan` |
| Official | `MeowChan.Plugin.EightKun` |
| Official | `MeowChan.Plugin.Danbooru` |
| Examples / filters | `MeowChan.Plugin.Lainchan` |
| Examples / filters | `MeowChan.Plugin.Kohlchan` |
| Examples / filters | `MeowChan.ImageDimensionsFilter` |
| Examples / filters | `MeowChan.Plugin.Wizchan` |
| Examples / filters | `MeowChan.Plugin.Endchan` |
| Examples / filters | `MeowChan.FileRulesFilter` |
| Examples / filters | `MeowChan.HashBlocklistFilter` |
| Examples / filters | `MeowChan.AnimationFormatFilter` |
| Examples / filters | `MeowChan.PngMetadataFilter` |
| Examples / filters | `MeowChan.Plugin.Leftypol` |
| Examples / filters | `MeowChan.Plugin.Dvach` |
| Examples / filters | `MeowChan.Plugin.Uboachan` |
| Examples / filters | `MeowChan.Plugin.Zzzchan` |
| Examples / filters | `MeowChan.Plugin.SushiChan` |
| Examples / filters | `MeowChan.Plugin.SevenChan` |
| Examples / filters | `MeowChan.Plugin.BbwChan` |
| Examples / filters | `MeowChan.Plugin.AnonImageboards` |
| Examples / filters | `MeowChan.Plugin.CapybaraChan` |
| Examples / filters | `MeowChan.Plugin.EightchanMoe` |
| Examples / filters | `MeowChan.Plugin.Fchan` |
| Examples / filters | `MeowChan.Plugin.GuroCx` |

For each bundle, the verifier requires the manifest schema, assembly path,
assembly SHA-256, SDK/compiler fingerprints and empty resource list to match.
It also requires the exact assembly filename and rejects missing, extra,
symlinked, special or build-output files.

## Editable source files

These files are copied to `plugins/sources/` for the delivered compiler:

`Lainchan.cs`, `Kohlchan.cs`, `ImageDimensionsFilter.cs`, `Wizchan.cs`,
`Endchan.cs`, `FileRulesFilter.cs`, `HashBlocklistFilter.cs`,
`AnimationFormatFilter.cs`, `PngMetadataFilter.cs`, `Leftypol.cs`, `Dvach.cs`,
`Uboachan.cs`, `Zzzchan.cs`, `SushiChan.cs`, `SevenChan.cs`, `BbwChan.cs`,
`AnonImageboards.cs`, `CapybaraChan.cs`, `EightchanMoe.cs`, `Fchan.cs`,
`GuroCx.cs`.

Official source files are not copied into the application package. A user can
still install a trusted local source file through the unchanged active-profile
`plugins` directory; that local candidate is separate from this bundled
inventory and is not a release input.

## Enforcement and final gates

`packaging/PluginDelivery.targets` contains the explicit project/source
allowlist and fails closed if build outputs do not have the expected identities
or if helper output contains links. `scripts/package-plugins.py --verify`
checks the exact inventory, manifest/checksum/fingerprint bindings, helper
runtime layout and compiler toolchain. `scripts/check-desktop-package.py`
rejects repository, build and profile artifacts in the desktop payload while
preserving the legal macOS alias view.

This change has synthetic offline inventory and mutation coverage only. A final
release still needs the normal per-RID locked restore, self-contained publish,
package verifier, desktop-package verifier, and platform/native acceptance
gates. This document does not grant overall release approval.
