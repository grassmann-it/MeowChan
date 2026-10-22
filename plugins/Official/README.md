# Official 4chan and 8kun source plugins

Each plugin is one complete editable `.cs` file and consumes only .NET framework
references plus `MeowChan.PluginSdk`. Its project builds a precompiled assembly:

| Source | Plugin ID | Entry source | Assembly |
| --- | --- | --- | --- |
| `4chan` | `meowchan.official.4chan` | `FourChan/FourChan.cs` | `MeowChan.Plugin.FourChan.dll` |
| `8kun` | `meowchan.official.8kun` | `EightKun/EightKun.cs` | `MeowChan.Plugin.EightKun.dll` |

Both have a public parameterless entry point (`FourChanPlugin` / `EightKunPlugin`
in `MeowChan.OfficialSources.FourChan` / `.EightKun`). Descriptions are static,
have an empty version-1 settings schema, and declare catalog, thread, post and
original-media metadata capabilities. Describe performs no network access.

Copying either source to the user's plugin source directory produces a complete
drop-in candidate without `#load`, NuGet, project evaluation, generated sources
or auxiliary files. A modified official source retains its identity; selecting
it against the bundled candidate follows the host's explicit conflict-selection
and next-start activation workflow. Merely editing or building does not activate
it. Plugins make metadata requests only through `IHostHttp`, never retry, and do
not fetch media, choose paths, write captures or change watch generations.

## Shared parser and library integration

The framework-only parser is inside the same file as its SDK adapter.
`LibraryParsing.targets` must be imported by `libmeowchan.csproj` during root
integration. It links these exact two files with `MEOWCHAN_CORE`: the SDK adapters are excluded and direct library
providers call the identical parser. No SDK/runtime/compiler reference is added
to libmeowchan. There is no copied or separately generated library parser.
The 0.9 library classes retain metadata name lookup, request policy, downloads,
paths, legacy media paths, watch state and archival lifecycle ownership.

The Desktop project must exclude `plugins/**/*.cs` from recursive compile items.
Build each project for delivery; ship its assembly plus its editable source.
The worker supplies the SDK assembly. Source filenames/assembly filenames do not
become source IDs or archive/export identities.

## Preserved semantics

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
