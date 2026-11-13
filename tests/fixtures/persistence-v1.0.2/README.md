# Reader groups and saved searches as the released 1.0.2 stores wrote them

`Program.cs` writes `reader-groups.json` (+ `.bak`), an emptied `reader-groups.json` and `archive-searches.json` (+ `.bak`)
with the **released** stores of tag `v1.0.2` (`eec6c0169f8d52f7481ea0bf7febfd20981e97c3`). The bytes are checked in under
`tests/Fixtures/persistence102/` and embedded into `MeowChan.Tests`, which pins their SHA-256 values
(`PersistenceCorruption/Release102FixtureTests.cs`). Synthetic content only, no personal data.

Regenerate (only when the fixtures must change; the digests in the test then change with them):

```sh
git archive v1.0.2 libmeowchan Shared plugins/Official Directory.Build.props Directory.Build.targets global.json | tar -x -C /scratch/rel102
mkdir /scratch/rel102/gen && cp tests/fixtures/persistence-v1.0.2/{Program.cs,Gen.csproj} /scratch/rel102/gen/
cd /scratch/rel102/gen && dotnet build && dotnet bin/Debug/net10.0/MeowChan.Tests.dll /scratch/out102   # empty output directory
cp /scratch/out102/files/* <repo>/tests/Fixtures/persistence102/
```

The generator project uses the assembly name `MeowChan.Tests` because the released library exposes its internal stores to exactly
that assembly. Never patch the released source. Do not build this project inside the repository: it references the exported
1.0.2 tree, not the current one.
