# macOS packages and optional future signing

## Initial 0.9.0 distribution without an Apple certificate

The initial macOS 0.9.0 distribution is intentionally **without Apple Developer ID
signing or notarization**. Obtaining an Apple certificate or notarization account
is not a release prerequisite. The packager preserves SDK-produced unsigned or
ad-hoc-signed executable code; an ad-hoc signature does not establish Developer
ID or notarized distribution.

Native execution of the complete bundle still needs validation on the intended
Intel and Apple Silicon Macs, including .NET startup, dynamically loaded VLC
plugins, playback and shutdown. Cross-publishing and Mach-O/bundle structural
checks do not establish that any of these native workflows passed.

Extract the complete archive and open `MeowChan.app` in Finder. If macOS blocks
it because the developer cannot be verified, and you trust the downloaded source,
open **System Settings → Privacy & Security**, find the blocked-app notice and
choose **Open Anyway**, then confirm **Open** and authenticate if requested.
On versions using System Preferences, use **Security & Privacy → General**.
This is an explicit exception for the selected app; keep Gatekeeper enabled and
retain quarantine metadata. If macOS reports malware or a damaged app, investigate
the download and diagnostics rather than treating this procedure as a repair.
See [Apple's guidance for opening an app from an unidentified developer](https://support.apple.com/102445).

## Optional later Developer ID and notarized distribution

The procedure below is a future optional distribution route, not a gate for the
initial unsigned/ad-hoc 0.9.0 packages. It requires separately provisioned Apple
credentials and native Mac validation; none of the steps is claimed as completed.
It is independent of the Windows preview.

LibVLC loads its plugins dynamically. Every selected dylib below
`MeowChan.app/Contents/MacOS/libvlc/<RID>/lib`, including
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
   need. Apply the apphost entitlements explicitly when signing the outer app/main
   executable, for example `codesign --force --options runtime --timestamp
   --entitlements MeowChan.entitlements.plist --sign "$IDENTITY" MeowChan.app`.
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
