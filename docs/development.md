# Developing MeowChan

Use the .NET SDK version pinned in [global.json](../global.json). Build and run
from the repository root.

```sh
dotnet restore MeowChan.sln -p:Configuration=Release --locked-mode
dotnet build MeowChan.sln -c Release --no-restore
dotnet run --project MeowChan.csproj -c Release --no-build --no-restore
```

- [Repository layout](development/repository-layout.md)
- [Analyzer policy and baseline maintenance](analyzer-baseline.md)
- [Desktop smoke harness](../tests/MeowChan.DesktopSmoke/README.md) and [package acceptance boundary](smoke-release-boundary.md)
- [Booru test coverage](development/booru-test-matrix.md)
- [Plugin development](plugins/README.md)
- [Platform requirements and dependency updates](platform-support.md)
