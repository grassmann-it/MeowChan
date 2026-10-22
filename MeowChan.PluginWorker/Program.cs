using MeowChan.PluginWorker;

// Preserve private protocol streams before redirecting plugin Console writes to diagnostics.
MeowChan.PluginProtocol.ProcessGroup.InitializeWorker();
Stream input = Console.OpenStandardInput();
Stream output = Console.OpenStandardOutput();
Console.SetOut(Console.Error);
try { return await new WorkerServer(input, output).RunAsync(); }
catch (Exception exception) { Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message); return 1; }
