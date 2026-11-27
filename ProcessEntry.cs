using System;
using System.IO;
using System.Runtime.Loader;
using MeowChan.PluginWorker;

namespace MeowChan;

internal static class ProcessEntry
{
    internal const string WorkerSwitch = "--meowchan-plugin-worker";
    internal const string CompilerSwitch = "--meowchan-plugin-compiler";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is [WorkerSwitch, ..]) return WorkerEntry.RunAsync().GetAwaiter().GetResult();
        if (args is [CompilerSwitch, ..]) return RunCompiler(args[1..]);
        return Program.Main(args);
    }

    internal static string PayloadDirectory()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        if (OperatingSystem.IsMacOS() && directory.Name == "MacOS" && directory.Parent?.Name == "Contents"
            && directory.Parent.Parent?.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true)
        {
            string payload = Path.Combine(directory.Parent.FullName, "Resources", "application");
            if (Directory.Exists(payload)) return payload;
        }
        return directory.FullName;
    }

    private static int RunCompiler(string[] args)
    {
        if (args is not ["--compile", string requestedDirectory]) return 2;
        try
        {
            string directory = Path.Combine(PayloadDirectory(), "plugin-compiler");
            if (!string.Equals(directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedDirectory)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return 2;
            // Only the per-build compiler process loads Roslyn from its delivered toolchain.
            var context = new CompilerLoadContext(directory);
            var assembly = context.LoadFromAssemblyPath(Path.Combine(directory, "MeowChan.PluginCompiler.dll"));
            return assembly.EntryPoint!.CreateDelegate<Func<string[], int>>()(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private sealed class CompilerLoadContext(string directory) : AssemblyLoadContext
    {
        protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName assemblyName) =>
            assemblyName.Name is "Microsoft.CodeAnalysis" or "Microsoft.CodeAnalysis.CSharp"
                ? LoadFromAssemblyPath(Path.Combine(directory, assemblyName.Name + ".dll"))
                : null;
    }
}
