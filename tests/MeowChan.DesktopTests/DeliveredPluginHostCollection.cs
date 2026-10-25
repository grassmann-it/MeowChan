namespace MeowChan.DesktopTests;

// Fresh full-payload sessions each describe every shipped plugin in real worker
// processes. Isolate unrelated sessions across test collections; tests retain
// their own explicit concurrent tasks and unchanged per-operation deadlines.
[CollectionDefinition("Delivered plugin hosts", DisableParallelization = true)]
public sealed class DeliveredPluginHostCollection { }
