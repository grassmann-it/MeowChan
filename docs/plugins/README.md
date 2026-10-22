# Plugin guide

MeowChan's 1.0 source tree supports C# source plugins and image filters in separate
processes. This guide describes that implementation; it does not announce a
published package or certify a desktop release. See [validation and current
limits](validation.md) for what each check establishes.

- [Install, edit, test and activate a plugin](using-plugins.md)
- [Write an SDK-only source](source-plugins.md)
- [Settings, image filters and scoped data access](filters-and-settings.md)
- [Optional hosting, prebuilt bundles and resources](hosting-and-resources.md)
- [Validate a plugin and interpret failures](validation.md)

The SDK targets **.NET 10**, with contract version **1.0.0**. `libmeowchan`,
`MeowChan.PluginSdk` and the optional host APIs are **not stable in 1.x**; minor
releases may change public APIs. A plugin must declare the exact SDK version
supported by its host. See [SDK contracts](../../MeowChan.PluginSdk/README.md)
and release notes under **Breaking (Library/SDK)** when upgrading.

## Start from maintained code

Use the linked source files directly. Each plugin entry below is a complete,
editable `.cs` file; there is no separate documentation copy to keep in sync.

| Example | What it demonstrates | Scope |
| --- | --- | --- |
| [Lainchan.cs](../../plugins/Examples/Lainchan/Lainchan.cs) · [notes and evidence](../../plugins/Examples/Lainchan/README.md) | Thread/catalog parsing, field presence, ordered primary/additional files, validators | Example source; limited public metadata observations, no invented archive flag |
| [Kohlchan.cs](../../plugins/Examples/Kohlchan/Kohlchan.cs) · [notes and evidence](../../plugins/Examples/Kohlchan/README.md) | LynxChan OP/reply shape, multiple files, original paths, final archived snapshot | Example source; full live catalog parsing remains unconfirmed in the bounded evidence collection |
| [ImageDimensionsFilter.cs](../../plugins/Examples/ImageDimensionsFilter/ImageDimensionsFilter.cs) | Scoped rule schema, domain validation, token-based original-byte reads | Static JPEG/PNG dimensions and aspect ratio; no OCR or general image decoding |
| [SyntheticPlugin.cs](../../samples/MeowChan.PluginSdk.Consumer/SyntheticPlugin.cs) · [consumer](../../samples/MeowChan.PluginSdk.Consumer/Program.cs) | Small independent SDK consumer, settings and passive information | Synthetic data only; references the SDK package |
| [Official source plugins](../../plugins/Official/README.md) | Existing 4chan/8kun source identities and provider differences | Official source implementations; registration is not a live-health claim |

A plugin references `MeowChan.PluginSdk` and the delivered framework libraries.
It does not reference `libmeowchan`, Avalonia, the desktop application or host
implementation classes. Applications that use the core library may explicitly add
`MeowChan.PluginHost`; the core library does not require the plugin host or compiler.

Local plugin code is trusted code with the user's permissions. A worker process
separates failures and can be stopped; it is **not an OS sandbox**. HTTP declarations
and image grants control the host's services, not arbitrary C# file/network calls.
No marketplace, automatic plugin updater or automatic grant expansion is implied.
