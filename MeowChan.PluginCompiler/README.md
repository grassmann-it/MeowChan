# Bundled plugin compiler

This is a separate process that builds one `.cs` byte snapshot against delivered
.NET 10 and MeowChan SDK references. It never runs MSBuild, NuGet, user analyzers,
source generators, projects, or plugin initializers. Public host APIs may change
in MeowChan 1.x.

Build/publish restore uses `Microsoft.CodeAnalysis.CSharp` 5.0.0 and the exact
`Microsoft.NETCore.App.Ref` 10.0.11 NuGet package. The compiler lock pins Roslyn;
the framework `PackageDownload` SHA512 is explicitly checked because NuGet does
not put PackageDownload entries in lock files. No compiler code discovers an SDK,
PATH command, reference pack, or home-directory package cache at runtime.

Publish this project **self-contained, untrimmed, non-single-file** for each RID
to `<app>/plugin-compiler/`. Both build and publish generate `toolchain.json` with
SHA256 hashes for compiler binaries and every delivered SDK/framework reference.
Keep the whole directory, including `references/`, runtime files and `licenses/`.
The main app and worker need their own publication. Crosspublish is not native
execution evidence. All five platform archives still require package verification.

Host wiring:

```csharp
var compilation = new PluginCompilationService(new PluginCompilerOptions(
    Path.Combine(appDirectory, "plugin-compiler", compilerExecutableName),
    Path.Combine(appDirectory, "plugin-compiler"),
    Path.Combine(profileDirectory, "plugin-cache")));
var inventory = new PluginInstallationInventory();
```

`BuildAsync` returns a candidate with `CompilationFingerprint` and a separate
`ExecutionRevision` incorporating resource hashes. Configuration/UI values do not
enter the compile key. Activation is exclusively the activation store's decision.
Default limits: one running build, eight waiting callers, 60 seconds per helper,
256 MiB managed helper heap, 4 MiB source, 128 resources, 32 MiB per resource and
64 MiB total resources. Limits protect availability and are not an OS sandbox.

A source is read once and sent as bytes to the helper. Diagnostics map back to
the original path, line and column. Roslyn sees a stable virtual source path, so
file moves cannot alter CallerFilePath or deterministic output under one byte key.
The helper is started by absolute apphost path with `ArgumentList`, private pipes
and a stage working directory. Cancellation/timeout kills the process tree.

Cache directories are `compilations/<hash>` and `artifacts/<execution-revision>`.
All bytes are checked before publication and on retrieval; final directories are
renamed atomically from same-volume staging. A corrupt published revision is
reported and left untouched because it may be pinned/active. A damaged redundant
compilation cache is rebuilt; `FindArtifactAsync` returns null for invalid data.
`CleanupAsync` requires the activation store's retained execution and compilation
sets, including active, pending, last-good, pinned and disabled selections.

Shipped prebuilt plugins use `IPluginBundleInstaller.ImportBundleAsync` and a
`*.plugin.json` serialized from `MeowChan.PluginHost.Installation.PluginBundleManifest`:

```json
{
  "SchemaVersion": 1,
  "AssemblyRelativePath": "official.dll",
  "AssemblySha256": "<lowercase SHA256 of official.dll>",
  "SdkFingerprint": "<lowercase SHA256 of delivered MeowChan.PluginSdk.dll>",
  "CompilerFingerprint": "<lowercase SHA256 identifying the prebuilt compiler inputs>",
  "Resources": [
    { "Name": "logical-name", "RelativePath": "data/table.bin", "Sha256": "<lowercase SHA256>", "Rid": null }
  ]
}
```

Resource paths are normalized relative paths; traversal, symlinks, duplicates,
reserved cache filenames and hash mismatches are rejected. Import copies the
verified bytes into the same immutable cache without starting the compiler or
initializing the plugin. Assembly contract/Describe validation is the worker's job.
The inventory enumerates at most 256 top-level entries and keeps unknown, empty,
unreadable or invalid candidates visible with diagnostics.
