# vichan family source plugin

`VichanFamily.cs` is one complete editable single-file plugin that serves several
vichan-based imageboards. It references only .NET and `MeowChan.PluginSdk`; the
delivered compiler builds it without the .NET SDK or helper files.

- Entry type: `MeowChan.Plugins.Examples.VichanFamily.VichanFamilyPlugin`
- Plugin ID: `meowchan.source.vichan`
- Build project: `MeowChan.Plugin.VichanFamily.csproj` → `MeowChan.Plugin.VichanFamily.dll`
- Delivery: prebuilt bundle plus the editable `VichanFamily.cs`

## Sites

Every site is its own source with its own source ID, exact host, routes and
(on the Sources page) its own on/off switch. The source IDs are the IDs of the
former single-site plugins and never change: watches, archives, rules and
per-source permissions refer to them.

| Source ID | Name | Host | Maturity | Site quirks | Evidence |
| --- | --- | --- | --- | --- | --- |
| `lainchan` | Lainchan | `lainchan.org` | – | none | [Sites/Lainchan](Sites/Lainchan/README.md) |
| `leftypol` | Leftypol | `leftypol.org` | experimental | `files[]` schema, `board` cross-check, no catalog image count, decimal thread route | [Sites/Leftypol](Sites/Leftypol/README.md) |
| `sushichan` | SushiChan | `sushigirl.cafe` | experimental | `filedeleted` primary marker, decimal thread route | [Sites/SushiChan](Sites/SushiChan/README.md) |
| `uboachan` | Uboachan | `uboachan.net` | experimental | `ext:"deleted"` slots, MD5-or-SHA-1 digest field, empty names are absent, leading-zero IDs, decimal thread route | [Sites/Uboachan](Sites/Uboachan/README.md) |
| `wizchan` | Wizchan | `wizchan.org` | experimental | `/{board}/index.html` board route, decimal thread route | [Sites/Wizchan](Sites/Wizchan/README.md) |

Shared engine behaviour (all sites): canonical routes `/{board}/` and
`/{board}/res/{thread}.html`; metadata `/{board}/catalog.json` and
`/{board}/res/{thread}.json`; originals `/{board}/src/{tim}{ext}` (or the site's
`file_path` for `files[]`); primary file at slot 0, `extra_files` from slot 1;
media identity `{postId}-{slot}`; inert HTML `com`; one `IHostHttp` request per
read; 304 only with an accepted validator; only the exact thread endpoint's 404
is `ThreadNotFound`; catalogs are never complete and absence never deletes
history. No board discovery, previews, posting or login.

## Adding a site

1. Collect evidence under `Sites/<Site>/` (home page, catalog, thread, media
   path, 404, conditional request, rules page and robots.txt), as the existing
   sites do. No live requests from tests.
2. Add one row to the `Sites` table in `VichanFamily.cs`: a new, never reused
   source ID, the exact host, the maturity and only the quirk flags its evidence
   proves. A new site is experimental.
3. Add synthetic fixtures `tests/MeowChan.ExamplePlugins.Tests/Fixtures/<id>-thread.json`
   and `<id>-catalog.json` and the source ID to `EngineFamilyPluginTests`.
4. In the desktop host, list the source in `ChanSourceFamilies` (its default
   switch state) and, if it is experimental, in `ExperimentalSourceActivationPolicy`.

## From the single-site plugins (1.0.x)

Until 1.0.3 each site was its own plugin (`meowchan.source.lainchan`,
`.leftypol`, `.sushichan`, `.uboachan`, `.wizchan`). The family declares the same
source descriptions byte for byte (checked by `EngineFamilyPluginTests`). On
upgrade the host treats those bundled identities as replaced by this plugin
once it is delivered: the old records stay untouched, no longer claim their
sources, and each site's on/off state is taken over from the old plugin's
enabled state. A user's own copy of an old single-site file stays a separate
plugin and goes through the normal explicit source-conflict choice.

Two parser details were unified with the stricter variant the other sites
already used: a non-string, non-number `tim` (for example `null`) is now an
invalid payload on Lainchan too (it used to produce a `null.png` URL), and a
`resto` given as a decimal string is accepted there as well.
