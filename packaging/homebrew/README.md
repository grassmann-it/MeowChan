# Experimental macOS source-build formula

`Formula/meowchan.rb` is a proposed formula for a **local or personal tap**. No
Homebrew tap, bottle, cask or macOS release has been published. This is a separate
follow-up to the Windows preview; it does not change the Windows source snapshot.

The formula compiles MeowChan locally from real commit
`a98759e819f233bee0de806cb4f0b9fbd535d2cd`, the Windows preview.3 source. Its version
is `0.9.0-preview.3`; the app's existing numeric bundle/assembly version remains
`0.9.0`. The download uses that commit, **not** a nonexistent final `v0.9.0` tag.

## Requirements and build contract

- macOS 15 or later, with Apple command-line tools and an up-to-date Homebrew.
  Use the Homebrew architecture matching the intended native build: Apple Silicon
  selects `osx-arm64`; Intel selects `osx-x64`. Rosetta operation is not validated.
- Homebrew's current [`Formula#fetch` phase](https://rubydoc.brew.sh/Formula.html#fetch-instance_method)
  must be available. It runs NuGet restore with network access; `install` then
  publishes in the same build directory without network access. Update Homebrew
  before testing. No `--no-sandbox`, global cache modification or sandbox bypass
  is used.
- The application pins SDK **10.0.111** with `rollForward: disable`. At preparation,
  the official formula API lists [`dotnet`](https://formulae.brew.sh/formula/dotnet)
  as **10.0.402**; `dotnet@10` returns 404. Therefore the formula stages the exact
  architecture-specific Microsoft SDK as a **build-only resource**. It does not
  silently rewrite `global.json` or use a newer host SDK.
- Both SDK tarballs are pinned by SHA-256 after checking their full SHA-512 against
  Microsoft's [10.0 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
  Python 3.14 is the other build dependency; it runs the repository's native-tree
  validator and macOS app-bundle packager.
- `fetch` performs the exact Release, selected-RID, self-contained/single-file
  **locked restore**, including vulnerability auditing. A drift, package audit
  failure or unavailable audit data fails visibly; the formula does not regenerate
  locks or suppress the audit. Source, NuGet cache, SDK and build outputs stay
  inside Homebrew's temporary build directory. The SDK lives under
  `artifacts/homebrew-tools`; CLI state and NuGet packages live under
  `artifacts/homebrew-state`. The pinned project excludes `artifacts/**` from
  implicit SDK globs, so unpacked dependency `.cs` files cannot become application
  compile items. Build outputs remain separate in `artifacts/homebrew`.
- `install` publishes with `--no-restore`. The complete selected
  `libvlc/<RID>/lib` tree, its plugins, the .NET runtime, resources, screenshots
  and dependency notices go into `MeowChan.app/Contents/MacOS`. It checks every
  native VLC library's architecture before and after bundling. The SDK is not
  installed in the final application and is not needed to run it.

"Source build" means **MeowChan's application code is compiled locally**. This
is not a source rebuild of .NET, Avalonia's native dependencies or VideoLAN's
macOS VLC libraries. These remain the pinned upstream binary dependencies from
the reviewed NuGet/runtime graph. The formula does not depend on Homebrew's VLC
cask or a separately installed VLC application, and it does not substitute a
flattened or system-wide plugin tree.

## Install into a local tap without publishing a repository

Run these commands from a checkout containing this formula on a disposable test
Mac. `tap-new` creates local files only; it does not create a GitHub repository.
If that local tap already exists, skip its creation:

```sh
brew update
brew tap-new --no-git tfgrass/meowchan
install -m 644 packaging/homebrew/Formula/meowchan.rb \
  "$(brew --repository tfgrass/meowchan)/Formula/meowchan.rb"
brew install --build-from-source tfgrass/meowchan/meowchan
brew test tfgrass/meowchan/meowchan
```

The app is installed inside the formula's `libexec`, and `meowchan` launches its
actual apphost through Homebrew's executable wrapper (`MeowChan` remains an alias).
The lowercase command links to that wrapper; the real apphost and its adjacent
resources stay inside the installed bundle. The formula does not write
into `/Applications` or change the user's profile. For Finder access, optionally
create a link in the current user's Applications folder:

```sh
mkdir -p ~/Applications
ln -sfn "$(brew --prefix tfgrass/meowchan/meowchan)/libexec/MeowChan.app" \
  ~/Applications/MeowChan.app
```

Remove that user-created link after uninstalling. A future published tap can use
the same formula after its own review; these instructions do not assume that such
a remote tap already exists.

## Signing and security boundaries

The pinned .NET SDK enables macOS apphost code signing for `osx-*` publishes and
its HostWriter creates an **ad-hoc** Mach-O signature. The formula verifies that
apphost signature with Apple's `codesign`. This matters especially on Apple
Silicon, where executable code needs a valid code signature; it does not require
a Developer ID certificate.

It does **not** produce a Developer ID signed app bundle or a notarization ticket,
re-sign all third-party plugins, or establish Gatekeeper acceptance. It does not
enable hardened-runtime signing or apply a new entitlement policy. The local
build route avoids requiring the maintainer's Apple credentials for this
experiment; it does not eliminate all macOS security checks. A downloaded SDK,
native dependency, generated app or user environment can still encounter a
platform restriction. Record the exact failure rather than disabling Gatekeeper
or removing quarantine attributes.

A cask would primarily install a prebuilt app; it would not provide this local
application build or solve unsigned download distribution. Developer ID signing,
CoreCLR/JIT entitlements, nested-plugin signing and notarization remain a separate
future distribution route, described in the project's
[macOS signing document](../../docs/macos-signing.md).

## Validation status and next native checks

Prepared on Linux. No Homebrew installation, native macOS build, Finder launch,
audio output, signing acceptance or notarization was performed here. Static Ruby
syntax/contract checks and downloaded-input hashes are the available preparation
checks; they are **not** a successful `brew install` or `brew audit` result.

On each native Mac architecture, retain the Homebrew version, OS/CPU, source/SDK
hashes, restore/build logs and the resulting app tree. Run:

```sh
brew style --formula tfgrass/meowchan/meowchan
brew audit --strict --online --formula tfgrass/meowchan/meowchan
brew fetch --build-from-source tfgrass/meowchan/meowchan
brew install --build-from-source tfgrass/meowchan/meowchan
brew test tfgrass/meowchan/meowchan
```

`brew test` checks the installed tree, selected native Mach-O architecture,
essential VLC plugins and the apphost's code signature. It deliberately does not
start a GUI or claim playback. Then, on the disposable desktop, check real startup
from both the wrapper and Finder, local VP8/VP9 WebM playback with actual audio,
pause/seek/stop/replay, media navigation, return from gallery, and normal shutdown
with file/profile handles released. Record any macOS launch/security prompt.
Do not call the formula accepted until those native checks pass.

## Official references consulted

- [Homebrew Formula Cookbook: dependency fetching before building](https://docs.brew.sh/Formula-Cookbook#fetching-dependencies-before-building)
- [Homebrew Formula API: fetch phase](https://rubydoc.brew.sh/Formula.html#fetch-instance_method)
- [Homebrew tap-new manual](https://docs.brew.sh/Manpage#tap-new-options-userrepo)
- [Homebrew Cask Cookbook](https://docs.brew.sh/Cask-Cookbook)
- [Microsoft .NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
- [.NET 10.0.11 HostWriter: ad-hoc signing](https://github.com/dotnet/runtime/blob/v10.0.11/src/installer/managed/Microsoft.NET.HostModel/AppHost/HostWriter.cs)
- [Apple: running unsigned code](https://developer.apple.com/documentation/security/running-unsigned-code)
- [Apple: notarizing macOS software](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)

The companion `input-manifest.json` records the selected source and SDK bytes.
Updating any pin requires rechecking the source graph, SDK compatibility,
architecture-specific hashes and native acceptance.
