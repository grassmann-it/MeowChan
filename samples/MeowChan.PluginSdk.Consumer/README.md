# Public SDK consumer

This sample references the `MeowChan.PluginSdk` NuGet package, with no project,
libmeowchan, Desktop, worker, or compiler dependency. The SDK is not stable in
1.x; minor releases may change its public API.

`SyntheticPlugin.cs` is an independently consumable source entry point. It
describes real thread/catalog payloads, two attachments on one post, an empty
source-settings schema, a nonempty plugin-settings schema, optional domain
validation, and host HTTP calls. The executable supplies local synthetic HTTP
responses and performs no network or media transfer. The `.test` host is not a
supported imageboard.

After packing the SDK into a local feed, restore this project using that feed
and run it. For example, from the repository root:

```sh
dotnet pack MeowChan.PluginSdk/MeowChan.PluginSdk.csproj -c Release -o artifacts/plugin-sdk-feed
dotnet restore samples/MeowChan.PluginSdk.Consumer/MeowChan.PluginSdk.Consumer.csproj --source artifacts/plugin-sdk-feed
dotnet run --project samples/MeowChan.PluginSdk.Consumer/MeowChan.PluginSdk.Consumer.csproj --no-restore
```

These commands are author instructions, not evidence that the sample was run.
