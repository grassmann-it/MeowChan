# Official source plugins: 4chan, 8kun and Danbooru

Each plugin is maintained as one complete editable `.cs` file **in this
repository** and consumes only .NET framework references plus
`MeowChan.PluginSdk`. The application package delivers prebuilt official bundles,
not their editable source files:

| Source | Plugin ID | Repository entry source | Delivered assembly | Editable source in package |
| --- | --- | --- | --- | --- |
| `4chan` | `meowchan.official.4chan` | `FourChan/FourChan.cs` | `MeowChan.Plugin.FourChan.dll` | No |
| `8kun` | `meowchan.official.8kun` | `EightKun/EightKun.cs` | `MeowChan.Plugin.EightKun.dll` | No |
| `danbooru` | `meowchan.official.danbooru` | `Danbooru/Danbooru.cs` | `MeowChan.Plugin.Danbooru.dll` | No |
| `e621`, `e926` | `meowchan.official.e621` | `E621/E621.cs` | `MeowChan.Plugin.E621.dll` | No |

[`PluginDelivery.targets`](../../packaging/PluginDelivery.targets) allowlists
these projects for prebuilt delivery: each has a `.plugin.json` manifest under
`plugins/` and its assembly under `plugins/assemblies/`. Its separate editable
source allowlist contains only example/filter files under `plugins/Examples/`,
copied to `plugins/sources/`. The [complete inventory](../../docs/plugins/README.md#packaged-inventory)
is checked against [`package-plugins.py`](../../scripts/package-plugins.py).

4chan and 8kun are board/thread sources. Danbooru is a booru post-search source
(SDK 1.1.0, `IBooruSourcePlugin`, capability `PostSearch`): it has no URL routes, is
not registered as a chan source by the desktop, and is verified by the same plugin
package check. Its fixtures and the scenarios they cover are listed in the
[booru test matrix](../../docs/development/booru-test-matrix.md).

The 4chan/8kun board routes declare ASCII word identifiers and their thread
routes declare decimal identifiers, matching the legacy URL grammar without worker IPC.

Both have a public parameterless entry point (`FourChanPlugin` / `EightKunPlugin`
in `MeowChan.OfficialSources.FourChan` / `.EightKun`). Descriptions are static,
have an empty version-1 settings schema, and declare catalog, thread, post and
original-media metadata capabilities. Describe performs no network access.

Plugins make metadata requests only through `IHostHttp`, never retry, and do
not fetch media, choose paths, write captures or change watch generations.

## Authoring and local builds

For a source-file candidate, obtain the desired entry file from a matching
repository checkout; it is not shipped beside the official assembly. Copy that
complete `.cs` file directly to the active profile's plugin directory (opened
with **Open plugin folder**), edit the copy and use **Build and test** under
**Developer details**. The delivered compiler needs no installed developer SDK,
`#load`, NuGet restore, project evaluation, generated sources or auxiliary files.
See [install, test and select a source candidate](../../docs/plugins/using-plugins.md#install-a-source-file).

For a project/assembly build, use the SDK pinned by the repository's
`global.json`. From the repository root, for example, with `NUGET_PACKAGES`
pointing to a populated local package cache:

```sh
dotnet restore plugins/Official/FourChan/MeowChan.Plugin.FourChan.csproj --locked-mode --source "$NUGET_PACKAGES" -p:NuGetAudit=false -m:1 -p:UseSharedCompilation=false
dotnet build plugins/Official/FourChan/MeowChan.Plugin.FourChan.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
```

Substitute the EightKun or Danbooru project path to build those entries. This
builds an assembly, not an installed bundle: a loose DLL alone is not installable.
Repository desktop delivery generates the matching manifests; separately
packaged candidates must follow the [verified bundle contract](../../docs/plugins/hosting-and-resources.md#prebuilt-bundles).
Do not overwrite a delivered assembly without regenerating its manifest hashes.

A modified official source retains its identity. Selecting it against the
bundled candidate follows the host's explicit conflict-selection and next-start
activation workflow. Merely copying, editing, building or testing does not
activate it; explicitly select **Use this build next start** and restart.

## Shared parser and library integration

The framework-only parser is inside the same file as its SDK adapter.
`LibraryParsing.targets` is imported by `libmeowchan.csproj`. It links the
4chan/8kun files with `MEOWCHAN_CORE`: the SDK adapters are excluded and direct
library providers call the identical parser. No SDK/runtime/compiler reference is added
to libmeowchan. There is no copied or separately generated library parser.
The 0.9 library classes retain metadata name lookup, request policy, downloads,
paths, legacy media paths, watch state and archival lifecycle ownership.

The Desktop project excludes `plugins/**/*.cs` from recursive compile items.
Delivery builds the allowlisted projects and ships their verified prebuilt
bundles, not the official editable sources. The worker supplies the SDK assembly.
Source filenames/assembly filenames do not become source IDs or archive/export
identities.

## Preserved 4chan/8kun semantics

- `4chan` retains `boards.4chan.org` and `boards.4channel.org`, catalog aliases,
  lowercased board IDs and optional thread title segments. Metadata is fetched
  from `a.4cdn.org`, original URLs point at `i.4cdn.org`.
- `8kun` retains `8kun.top`, `catalog`, `catalog.html`, `index.html`,
  `/res/{thread}.html`, and `nerv.8kun.top/file_store/` media URLs.
- Thread HTTP404 is terminal without fabricated posts. HTTP304 requires an
  accepted snapshot and known validator; it never returns an empty snapshot.
  HTTP206/403/410/429/5xx do not become thread-end signals. Host deferred and
  failure responses pass through unchanged; the plugin adds no retry policy.
- A 4chan archived flag includes the final posts. 8kun locked/archived flags
  remain active, matching the established library end-signal policy.
- Subject/name HTML entities are decoded; comment content stays inert HTML.
  Missing, null and empty fields remain distinguishable. Decimal post identity
  orders partial snapshots. 8kun extra files retain positions starting at 1,
  original media IDs and deleted attachment markers.
- Malformed media timestamps or replies without a post ID reject the whole
  response; no attachment is silently dropped from an otherwise successful
  final snapshot. Leading UTF-8 BOMs are accepted like the library reader.
- Catalog omission does not signal the end of a thread. Catalogs are complete
  multi-page API observations; explicit cursors/pages are unsupported.

## Verification

`tests/MeowChan.OfficialPlugins.Tests` supplies synthetic fixture HTTP responses
and compares plugin DTOs with actual libmeowchan archive captures and catalog
results. It checks shared parser parity, response states, 304 validators,
4chan archive versus 8kun lock, extra attachments, empty/missing fields,
cancellation, malformed/oversized bodies and SDK-only entry assemblies.
Fixture provenance is documented alongside the JSON files. No live board crawl
is needed or performed.
