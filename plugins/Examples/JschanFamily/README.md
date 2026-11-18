# jschan family source plugin

`JschanFamily.cs` is one complete editable single-file plugin for imageboards
running jschan. It references only .NET and `MeowChan.PluginSdk`.

- Entry type: `MeowChan.Plugins.Examples.JschanFamily.JschanFamilyPlugin`
- Plugin ID: `meowchan.source.jschan`
- Build project: `MeowChan.Plugin.JschanFamily.csproj` → `MeowChan.Plugin.JschanFamily.dll`
- Delivery: prebuilt bundle plus the editable `JschanFamily.cs`

## Sites

| Source ID | Name | Host | Maturity | Evidence |
| --- | --- | --- | --- | --- |
| `zzzchan` | ZZZchan | `zzzchan.xyz` | experimental | [Sites/Zzzchan](Sites/Zzzchan/README.md) |

The source ID is the ID of the former single-site plugin `meowchan.source.zzzchan`
and never changes. Each site has its own exact host, routes and on/off switch.

jschan answers JSON on every route, so the engine needs no per-site parsing
switches: routes `/{board}/`, `/{board}/index.html` and
`/{board}/thread/{thread}.html`; metadata `/{board}/catalog.json` and
`/{board}/thread/{thread}.json`; originals `/file/{filename}` with provider
SHA-256 and byte size (`phash` is never an integrity value); the opener at the
JSON root, `replies[]` for replies, public `postId` as identity.

jschan also publishes `/boards.json` for board discovery. The plugin SDK has no
board-list contract yet, so the family does not offer automatic board lists;
boards are added by link as before.

## Adding a site

Collect evidence under `Sites/<Site>/`, add one row to `Sites` in
`JschanFamily.cs` (new source ID, exact host, experimental), add synthetic
`<id>-thread.json` / `<id>-catalog.json` fixtures and the ID to
`EngineFamilyPluginTests`, and list the source in the desktop's
`ChanSourceFamilies` and `ExperimentalSourceActivationPolicy`.

## From the single-site plugin (1.0.x)

The family declares the former `zzzchan` source description byte for byte. On
upgrade the bundled `meowchan.source.zzzchan` identity is treated as replaced by
this plugin: its record stays untouched, no longer claims `zzzchan`, and the
site's on/off state is taken over from the old plugin's enabled state.
