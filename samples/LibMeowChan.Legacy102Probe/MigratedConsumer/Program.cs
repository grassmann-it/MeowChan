using System;
using System.Text.Json;
using System.Threading.Tasks;
using MeowChan;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace LibMeowChan.Legacy102Probe.MigratedConsumer;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ProbeOptions options;
        try
        {
            options = ProbeOptions.Parse(args);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return WriteResult(started: false, exceptionType: exception.GetType().FullName, exitCode: 2);
        }

        try
        {
            await RunCurrent110LifecycleAsync(options.ProfileDirectory).ConfigureAwait(false);
            return WriteResult(started: true, exceptionType: null, exitCode: 0);
        }
        catch (Exception exception)
        {
            // Type names only: enough to distinguish missing runtime dependencies
            // from lifecycle failures without leaking profile paths or exception text.
            string initializer = exception is TypeInitializationException typeError
                ? typeError.TypeName ?? "unknown" : "none";
            Console.Error.WriteLine($"Core probe failure: initializer={initializer}; cause={exception.GetBaseException().GetType().FullName}");
            return WriteResult(started: false, exceptionType: exception.GetType().FullName, exitCode: 1);
        }
    }

    private static async Task RunCurrent110LifecycleAsync(string profileDirectory)
    {
        // The migration target uses the current public lifecycle. The lease, both
        // stores and Registry are the same private profile-bound composition as the
        // old probe; only startup is changed to StartAsync(lease).
        using ProfileLease lease = ProfileLease.Acquire(profileDirectory);
        SessionStore sessionStore = new SessionStore(profileDirectory, lease);
        WorkStateStore workStore = new WorkStateStore(profileDirectory, lease);
        DownloadQueue queue = new DownloadQueue(
            maxParallelDownloads: 1,
            maxBandwidthBytesPerSecond: 1024 * 1024);
        await using Registry session = new Registry(
            providers: Array.Empty<IImageBoard>(),
            queue: queue,
            outputDirectory: profileDirectory,
            threadInterval: TimeSpan.FromMinutes(1),
            boardIntervalSeconds: 60,
            sessionStore: sessionStore,
            persistSession: true,
            workStore: workStore);

        await session.StartAsync(lease).ConfigureAwait(false);
    }

    private static int WriteResult(bool started, string? exceptionType, int exitCode)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            probe = "core110",
            started,
            startupAdmissionRejected = false,
            exceptionType
        }));
        return exitCode;
    }
}

internal sealed record ProbeOptions(string ProfileDirectory)
{
    internal static ProbeOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? profile = null;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] != "--profile")
                throw new ArgumentException($"Unknown argument '{args[index]}'.", nameof(args));
            if (profile is not null || ++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                throw new ArgumentException("Exactly one non-empty --profile value is required.", nameof(args));
            profile = args[index];
        }

        if (profile is null || !System.IO.Path.IsPathFullyQualified(profile))
            throw new ArgumentException("--profile must be one absolute private profile path.", nameof(args));
        return new ProbeOptions(System.IO.Path.TrimEndingDirectorySeparator(profile));
    }
}
