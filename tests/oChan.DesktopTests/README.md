# oChan.DesktopTests

These xUnit tests reference the actual desktop assembly to exercise configuration recovery/save behavior, batch Add routing, localization and passive row projections. Session-host tests cover profile ownership, restore failures, stale remove commands, settings transactions and verified media reuse after restart. They use isolated temporary profiles and controlled HTTP transports; they do not open a window, access the real clipboard or read the user's profile.

```sh
dotnet test tests/oChan.DesktopTests/oChan.DesktopTests.csproj -c Release
```

The separate [core suite](../oChan.Tests/README.md) references only `libochan`. Run `dotnet test oChan.sln -c Release` for both suites. Native window/tray checks remain in [oChan.DesktopSmoke](../oChan.DesktopSmoke/README.md).
