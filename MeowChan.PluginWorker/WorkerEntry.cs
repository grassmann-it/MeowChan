using System;
using System.IO;
using System.Threading.Tasks;
using MeowChan.PluginProtocol;

namespace MeowChan.PluginWorker;

internal static class WorkerEntry
{
    internal static async Task<int> RunAsync()
    {
        // Preserve private protocol streams before redirecting plugin Console writes to diagnostics.
        ProcessGroup.InitializeWorker();
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);
        try
        {
            WorkspaceQuota.Accept();
            using WorkerServer server = new(input, output);
            return await server.RunAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }
}
