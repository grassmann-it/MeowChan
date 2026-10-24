# Genuine synthetic 0.9 → 1.0 desktop profile

The generator runs the **unchanged 0.9 implementation at
`8ec0cf50b270e0551a06d3ad61eff0f448bd70ff`**, the accepted released 0.9 baseline.
Tag `v0.9.0` resolves to `e4413084d481d950307f4424be272b8b67e8479f`;
`git diff v0.9.0 8ec0cf5 -- libmeowchan Desktop MeowChan.csproj` is empty.
It creates settings with old Config APIs, watches through the old DesktopSessionHost,
and bytes/captures/manifests/work state through the real old 4chan/8kun providers,
using only an in-memory terminal HTTP transport. Old FavoriteStore, HTML exporter,
and ExportHistoryStore create persisted favorites and verified exports. The profile
is paused and shut down normally. `v09-receipt.json` records old observed identities
and hashes; it is evidence only, never profile input to either app.

0.9 already used `.meowchan-profile.protocol`, `.meowchan-profile.lock`, and
`.meowchan-manifest.json`/`.meowchan-store.lock`. Its adoption of the settings-first
fixture also creates `.ochan-profile.lock`. We assert that real old lock artifact;
we do **not** rename manifests or invent pre-0.9 `.ochan` state to claim a 0.9 test.
This case covers schema migration in the old work journal, current plugin registration,
raw source/board/thread/media identities, complete original files, archive posts and
attachments, favorite keys, export identities/generations and exact exported files,
settings, pause, and two additional offline shutdown/startup cycles.

All commands below are for root coordination; every dotnet command must hold the
shared build lock. The preparation script itself only exports source, and can run
without that lock. Choose new absolute paths for every run; the integration test
intentionally fails if the generated profile has already acquired a plugin cache.
It mutates only the explicitly supplied synthetic profile and retains it as evidence.

```sh
python3 tests/fixtures/upgrade-v09/prepare.py /tmp/meowchan-v09-upgrade-source-UNIQUE
flock /tmp/meowchan-ui-1.0-build.lock dotnet run --project /tmp/meowchan-v09-upgrade-source-UNIQUE/tests/UpgradeFixtureGenerator/UpgradeFixtureGenerator.csproj --configuration Debug -- /tmp/meowchan-v09-upgrade-profile-UNIQUE
MEOWCHAN_V09_UPGRADE_PROFILE=/tmp/meowchan-v09-upgrade-profile-UNIQUE flock /tmp/meowchan-ui-1.0-build.lock dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --configuration Release --filter FullyQualifiedName~DesktopUpgradeIntegrationTests --logger 'console;verbosity=detailed'
```

Use the usual root build/restore/runtime properties and payload staging required by
DesktopPluginSessionIntegrationTests: all seven shipped plugin manifests and assemblies
(six sources plus ImageDimensionsFilter), together with
plugin-worker and plugin-compiler must be beside the desktop test assembly. These
are actual out-of-process delivered workers; no injected imageboard providers are
used on the 1.0 side. Missing payloads fail startup assertions. The normal suite
reports an explicit skip unless `MEOWCHAN_V09_UPGRADE_PROFILE` is set.

Limitations: this is a synthetic desktop-session integration case on the machine
running it, not a GUI upgrade, installer upgrade, live network claim, OS matrix,
or downgrade proof. All 1.0 starts remain paused; resumed plugin downloading is
covered independently by DesktopPluginSessionIntegrationTests. No personal profile,
display, clipboard, or external service is used.
