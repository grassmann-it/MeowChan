# MeowChan.DesktopTests

These xUnit tests reference the actual desktop assembly to exercise configuration recovery/save behavior, batch Add routing, localization and passive row projections. Session-host tests cover profile ownership, restore failures, stale remove commands, settings transactions and verified media reuse after restart. They use isolated temporary profiles and controlled HTTP transports; they do not open a window, access the real clipboard or read the user's profile.

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -c Release
```

The separate [core suite](../MeowChan.Tests/README.md) references only `libmeowchan`. Run `dotnet test MeowChan.sln -c Release` for both suites. Native window/tray checks remain in [MeowChan.DesktopSmoke](../MeowChan.DesktopSmoke/README.md).

Archive presentation tests cover inactive plain-text rendering of source HTML. Passive-row tests distinguish known, excluded, selected and saved media so filtered files do not count as successful downloads or dilute selected progress.
