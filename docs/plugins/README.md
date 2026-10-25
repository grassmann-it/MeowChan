# Plugin guide

MeowChan's 1.0 source tree supports C# source plugins and image filters in separate
processes. This guide describes that implementation; it does not announce a
published package or certify a desktop release. See [validation and current
limits](validation.md) for what each check establishes.

For a first try, open **Plugins** in the app menu or footer (under **Tools** in compact views) and follow the
[first-session recipe](using-plugins.md#first-session). The supplied prebuilt
plugins are already listed. Testing editable source uses a separate single-file
candidate and the delivered compiler, with no developer SDK required.

- [Install, edit, test and activate a plugin](using-plugins.md)
- [Write an SDK-only source](source-plugins.md)
- [Settings, image filters and scoped data access](filters-and-settings.md)
- [Additional local media filters](local-media-filters.md) — integration wave, separately validated from Alpha 2
- [Optional hosting, prebuilt bundles and resources](hosting-and-resources.md)
- [Validate a plugin and interpret failures](validation.md)
- [Six-source live results, 2026-10-24](live-sources-2026-10-24.md) — three full acquisition/offline passes; three original-size mismatches

The SDK targets **.NET 10**, with contract version **1.0.0**. `libmeowchan`,
`MeowChan.PluginSdk` and the optional host APIs are **not stable in 1.x**; minor
releases may change public APIs. A plugin must declare the exact SDK version
supported by its host. See [SDK contracts](../../MeowChan.PluginSdk/README.md)
and release notes under **Breaking (Library/SDK)** when upgrading.

## Start from maintained code

Use the linked source files directly. Each plugin entry below is a complete,
editable `.cs` file; there is no separate documentation copy to keep in sync.

| Plugin or sample | What it demonstrates | Scope |
| --- | --- | --- |
| [Lainchan.cs](../../plugins/Examples/Lainchan/Lainchan.cs) · [notes and evidence](../../plugins/Examples/Lainchan/README.md) | Thread/catalog parsing, field presence, ordered primary/additional files, validators | Source plugin; no archived-thread detection; see provider notes for evidence and limits |
| [Kohlchan.cs](../../plugins/Examples/Kohlchan/Kohlchan.cs) · [notes and evidence](../../plugins/Examples/Kohlchan/README.md) | LynxChan OP/reply shape, multiple files, original paths, final archived snapshot | Source plugin; catalog coverage remains limited; see provider notes for evidence and limits |
| [Wizchan.cs](../../plugins/Examples/Wizchan/Wizchan.cs) · [notes and evidence](../../plugins/Examples/Wizchan/README.md) | Independent SDK-only source, thread/catalog metadata and ordered attachments | Source plugin; see provider notes for evidence and limits |
| [Endchan.cs](../../plugins/Examples/Endchan/Endchan.cs) · [notes and evidence](../../plugins/Examples/Endchan/README.md) | Independent SDK-only source, LynxChan OP/replies and original-media metadata | Source plugin; see provider notes for evidence and limits |
| [ImageDimensionsFilter.cs](../../plugins/Examples/ImageDimensionsFilter/ImageDimensionsFilter.cs) | Scoped rule schema, domain validation, token-based original-byte reads | Static JPEG/PNG dimensions and aspect ratio; no OCR or general image decoding |
| [SyntheticPlugin.cs](../../samples/MeowChan.PluginSdk.Consumer/SyntheticPlugin.cs) · [consumer](../../samples/MeowChan.PluginSdk.Consumer/Program.cs) | Small independent SDK consumer, settings and passive information | Synthetic data only; references the SDK package |
| [Official source plugins](../../plugins/Official/README.md) | Existing 4chan/8kun source identities and provider differences | Official source implementations; registration is not a live-health claim |

In this separate integration branch the prepared delivery contains **eleven
plugins, six sources and nine editable files**: the existing six sources and
five filters (dimensions plus the four [local media filters](local-media-filters.md)).
The five new source adapters are staged outside the delivery inventory until
their data contracts and activation/host checks are complete. This inventory
is not a published release or packaged/native-platform acceptance result.
For the historical source-only baseline, see the
[dated local validation results](../architecture/1.0.0/source-erweiterungen/README.md#lokaler-abschlussnachweis)
for the tested candidate and its limits.

A plugin references `MeowChan.PluginSdk` and the delivered framework libraries.
It does not reference `libmeowchan`, Avalonia, the desktop application or host
implementation classes. Applications that use the core library may explicitly add
`MeowChan.PluginHost`; the core library does not require the plugin host or compiler.

Local plugin code is trusted code with the user's permissions. A worker process
separates failures and can be stopped; it is **not an OS sandbox**. HTTP declarations
and image grants control the host's services, not arbitrary C# file/network calls.
No marketplace, automatic plugin updater or automatic grant expansion is implied.
