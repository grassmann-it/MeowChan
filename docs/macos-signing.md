# macOS packages and optional future signing

## Standard 0.9.0 macOS packages

The standard macOS distribution is a complete self-contained `MeowChan.app` in
one disk image per architecture:

- `MeowChan-<version>-osx-arm64.dmg` for Apple Silicon.
- `MeowChan-<version>-osx-x64.dmg` for Intel Macs.

The native packaging step applies an **ad-hoc signature**, without Apple
Developer ID, hardened runtime or notarization. No Apple certificate is required.
The app in the DMG includes the matching .NET runtime and selected VLC libraries/plugins;
keep the entire app bundle together. Homebrew source builds remain an optional
route, rather than a prerequisite for installing the packaged app.

At source `226af46`, the [native package/media run](https://github.com/tfgrass/MeowChan/actions/runs/39662560567)
passed on ARM (`macos-15`) and Intel (`macos-15-intel`). Each job mounted its
signed DMG, verified the app and Applications shortcut, copied the app out, and
ran startup, bundled-VLC playback and the packaged desktop workflow from that
copy. The run also paired both DMGs with their corresponding-source overlay.
See [the platform evidence](platform-support.md#090-candidate-final-native-revalidation-pending).
This evidence belongs to that exact source. The newer `b8e1291` normal-window
and delayed-fullscreen corrections still await final native validation and
rebuilt-package checks. An ad-hoc signature does not establish developer
identity or automatic Gatekeeper approval; physical audio/hardware and
downloaded/quarantined-copy approval remain unvalidated. This is not a public
Mac release or a final readiness claim.

Open the DMG in Finder, drag `MeowChan.app` to the **Applications** shortcut, then
open the installed app. Keep the whole app bundle intact. If macOS blocks
it because the developer cannot be verified, and you trust the downloaded source,
open **System Settings → Privacy & Security**, find the blocked-app notice and
choose **Open Anyway**, then confirm **Open** and authenticate if requested.
On versions using System Preferences, use **Security & Privacy → General**.
This is an explicit exception for the selected app; keep Gatekeeper enabled and
retain quarantine metadata. If macOS reports malware or a damaged app, investigate
the download and diagnostics rather than treating this procedure as a repair.
See [Apple's guidance for opening an app from an unidentified developer](https://support.apple.com/102445).

## Ad-hoc packaging contract

The packager interface is `scripts/package-macos.py --runtime osx-arm64` (or
`osx-x64`) with `--sign-adhoc` and an internal `--archive <output>.zip`, alongside
its publish, output and version arguments. `scripts/package-macos-dmg.py` builds
the public DMG from the signed bundle, mounts it read-only, checks the Applications
shortcut and signed contents, and copies the app out for actual execution.
`scripts/macos-signing.py` provides the signing and validation helper. Complete binary/install-name changes first, then sign every
nested Mach-O file from inside outward and the bundle last. Apply the explicit
CoreCLR entitlements to the actual .NET apphost; do not copy them onto every
library. The ad-hoc route does not enable hardened runtime.

The current signed bundle implementation places the real .NET executable physically
at `MeowChan.app/Contents/MacOS/MeowChan`, where it carries the four CoreCLR
entitlements and serves as the bundle's main executable. All other publish entries
(runtime dylibs, VLC tree, licenses and documentation) live under
`Contents/Resources/application`. Relative top-level symlinks in `Contents/MacOS`
point to these payload entries; `Contents/Resources/application/MeowChan` links
back to the physical main executable. There is no C launcher or launcher compiler
source. Every Mach-O file in the code/payload locations is explicitly signed and
validated. Fixture CFBundle identity/icon lookups passed at the earlier
[`672b45d` checkpoint](platform-support.md#native-macos-package-and-startup-checkpoint--672b45d);
the later `226af46` run exercised actual copied-app startup, playback and desktop
workflow. Those results do not validate subsequent product changes.

Legacy unsigned/Homebrew bundles keep the publish output in `Contents/MacOS`.
Self-contained publishing uses `PublishSingleFile=false` so native .NET libraries
remain visible for final signing; users still need no separate .NET installation.

The internal inventory ZIP is created with `ditto`; a fresh extraction revalidates
signatures and bundle contents. The DMG is created with `hdiutil`, mounted read-only,
and its app is checked against the signed bundle before and after copying outside
the image. The copied app undergoes architecture, signature and dependency closure
checks plus native startup; package-media validation also runs playback and the
desktop workflow from that copy. Signature validation alone does not prove .NET
JIT startup, VLC plugin loading/playback or shutdown. The `226af46` package/media
run exercised those runtime paths from the copied DMG app on both architectures.
A fresh native run for the current source, and a downloaded/quarantined-copy
check of the manual approval flow, still require separate evidence.

## Optional later Developer ID and notarized distribution

The procedure below is a future optional distribution route, not a gate for the
initial ad-hoc 0.9.0 DMG packages. It requires separately provisioned Apple
credentials and native Mac validation; none of the steps is claimed as completed.
It is independent of the Windows preview.

LibVLC loads its plugins dynamically. Every selected dylib below
`MeowChan.app/Contents/Resources/application/libvlc/<RID>/lib`, including
`lib/vlc/plugins/*.dylib` and any dependent dylibs, must be included in the signing
inventory. Preserve the selected architecture, relative directory structure and
contained symlinks. Complete copying and any install-name changes before signing;
changing a signed binary or bundle afterward invalidates its signature.

For future Developer ID/notarized distribution:

1. On a Mac with Xcode command-line tools, use an appropriate **Developer ID
   Application** certificate and its private key. An ad-hoc signature is useful
   for local tests but does not establish Developer ID/notarized distribution.
2. Sign the embedded Mach-O libraries/plugins and other nested executable code
   explicitly, from the innermost items outward, with the same Developer ID
   identity. Then sign the app/executable with the hardened runtime and a secure
   timestamp. Apple advises against using `codesign --deep` as the signing
   strategy; it is suitable for recursive verification.
3. Account for **CoreCLR JIT separately from VLC**. Microsoft's
   [macOS notarization guidance](https://learn.microsoft.com/en-us/dotnet/core/install/macos-notarization-issues#default-entitlements)
   lists four default .NET host entitlements and says the submitted apphost
   should use at least that set. Use the following documented baseline for the
   .NET 10 self-contained apphost when re-signing; do not replace its runtime
   entitlements with a video-only policy:

   ```xml
   <?xml version="1.0" encoding="UTF-8"?>
   <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
   <plist version="1.0"><dict>
     <key>com.apple.security.cs.allow-jit</key><true/>
     <key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
     <key>com.apple.security.cs.allow-dyld-environment-variables</key><true/>
     <key>com.apple.security.cs.disable-library-validation</key><true/>
   </dict></plist>
   ```

   `allow-jit` and executable-memory handling serve the .NET runtime, independently
   of video. `disable-library-validation` also appears in Microsoft's baseline;
   do not remove it simply because the VLC libraries were re-signed with the same
   Team ID. A future narrower .NET 10 policy needs its own native runtime evidence.
   Signing all embedded libraries with the application's identity remains useful
   and avoids depending on unsigned third-party plugins. Do not add further
   entitlements such as disabled executable-page protection without an established
   need. Apply the apphost entitlements explicitly to the physical .NET executable,
   for example `codesign --force --options runtime --timestamp
   --entitlements MeowChan.entitlements.plist --sign "$IDENTITY"
   MeowChan.app/Contents/MacOS/MeowChan`. Then sign the outer bundle, retaining
   the main executable's runtime entitlements.
   Do not blindly copy apphost JIT entitlements onto every dylib.

   A local read of both cross-published .NET 10 apphosts found code-signature
   payloads without XML entitlement blobs. That is evidence about those unsigned/
   ad-hoc candidates only, not permission to omit entitlements when enabling the
   hardened runtime during Developer ID signing. On the signing Mac inspect the
   actual apphost with `codesign -d --entitlements :-` before and after signing.
4. Verify the completed bundle, for example with
   `codesign --verify --deep --strict --verbose=2 MeowChan.app`, and check the
   identity/entitlements of the app and the selected VLC libraries. Test actual
   playback, plugin loading, media navigation and clean shutdown on the intended
   Mac architecture, including a path different from the build directory.
5. Create a notarization upload preserving the bundle, for example with
   `ditto -c -k --sequesterRsrc --keepParent MeowChan.app MeowChan-macos.zip`.
   Submit with `xcrun notarytool submit ... --wait` using separately provisioned
   Apple credentials (a keychain profile or App Store Connect API credentials).
   An accepted service result is required; retain the submission result/log.
6. Staple the ticket to the app with `xcrun stapler staple MeowChan.app`, then
   `xcrun stapler validate MeowChan.app`. Recreate the final archive from the
   stapled app. Assess that final app with
   `spctl --assess --type execute --verbose=4 MeowChan.app` and test a fresh
   downloaded/quarantined copy on a disposable Mac. A source-built launch or
   `codesign` success alone does not establish Gatekeeper acceptance.

Do not remove quarantine or disable Gatekeeper as a substitute for either the
per-app manual approval above or native validation. Replacing a VLC library later changes the signed bundle; users
rebuilding or replacing LGPL components may need to re-sign their modified app
for local use. The application must not prevent compatible component replacement.

Apple references:

- [Notarizing macOS software before distribution](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)
- [Hardened Runtime](https://developer.apple.com/documentation/security/hardened-runtime)
- [Disable Library Validation entitlement](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.cs.disable-library-validation)
- [Signing Mac Software with Developer ID](https://developer.apple.com/developer-id/)
- [Code Signing In Depth, nested code and the deep option](https://developer.apple.com/library/archive/technotes/tn2206/_index.html)
