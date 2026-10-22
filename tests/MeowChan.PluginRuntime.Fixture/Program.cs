using System.Buffers.Binary;
using MeowChan.PluginProtocol;
using MeowChan.PluginRuntime.Fixture;

if (args.Length == 0) return 0;
if (args[0] == "child") { await Task.Delay(Timeout.InfiniteTimeSpan); return 0; }
ProcessGroup.InitializeWorker();
Stream output = Console.OpenStandardOutput();
if (args[0] == "oversize")
{
    byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, 17 * 1024 * 1024);
    await output.WriteAsync(length); await output.FlushAsync(); await Task.Delay(5000); return 0;
}
if (args[0] == "json")
{
    await output.WriteAsync(new byte[] { 1, 0, 0, 0, (byte)'{' }); await output.FlushAsync(); await Task.Delay(5000); return 0;
}
if (args[0] == "closed") return 0;
using FrameCodec codec = new(Console.OpenStandardInput(), output);
while (await codec.ReadAsync(CancellationToken.None) is WireEnvelope request)
{
    if (request.Operation == "hello")
        await codec.WriteAsync(request with { Direction = "response", Payload = WireJson.Element(new HelloResponse(ProtocolVersion.Operations)), Version = args[0] == "version" ? 99 : 1 }, CancellationToken.None);
    else if (request.Operation == "describe")
        await codec.WriteAsync(request with { Direction = "response", Payload = WireJson.Element(new FixturePlugin().Describe()) }, CancellationToken.None);
    else
        await codec.WriteAsync(request with { Direction = "response", Generation = request.Generation + 1, Payload = WireJson.Element<MeowChan.PluginSdk.ThreadReadResult>(new MeowChan.PluginSdk.ThreadNotFound(new("fixture", "b", "1"), 404)) }, CancellationToken.None);
}
return 0;
