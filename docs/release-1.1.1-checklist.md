# Release checklist — MeowChan 1.1.1

Validation for [MeowChan 1.1.1](releases/v1.1.1.md).

## Candidate validation

- Record the candidate SHA, SDK, CI run, test totals and package checksums.
- Use the [desktop and library validation workflow](../.github/workflows/dotnet-desktop.yml)
  in `validate` mode for the full desktop matrix.
- On Windows, extract the x64 ZIP. On macOS, install the DMG for the matching
  architecture and see the [first-launch guidance](macos-signing.md) if needed.
  Confirm the version in About and record architecture and display scale.
- Check Downloads keyboard focus and context menus, Reader and Media sidebar
  controls, Booru tab and result restoration, Tagging selection and resume,
  and the Gallery footer with pointer and keyboard.
- For an upgrade check from 1.1.0, use a copy of a representative profile.
  Exercise download, Reader, Gallery, tagging, resume and Booru across restarts.

```sh
dotnet test tests/MeowChan.Tests --filter "FullyQualifiedName~Release|FullyQualifiedName~Changelog|FullyQualifiedName~Version"
```

## Known limitations and open items

- `validate` provides the full validation sweep but does not produce the inventory-bound stable candidate or its AP04 result. The AP04 stage and runner under `tests/fixtures/upgrade-v1.0.2/` still expect 1.1.0. An upgrade from 1.0.2 to 1.1.1 via that package gate remains open.
- Booru result restoration is bounded by the profile settings. Bulk download is bounded by the provider’s page limit. Live-provider behavior and native input are part of the pending acceptance.
- Current macOS packages are ad-hoc signed and require manual approval when Gatekeeper blocks first launch. The separate Homebrew formula pins a historical preview source and bundle version.
- The Windows application manifest still declares `MeowChan.Desktop` identity 1.0.0.0. About and package versions come from managed build metadata. Whether the manifest identity should follow future product releases is open. The plugin SDK compatibility contract remains 1.1.0.
- The public downloads, README and platform support documentation continue to refer to the published 1.1.0 assets during preparation. Any 1.1.1 publication metadata belongs to the later release step.
