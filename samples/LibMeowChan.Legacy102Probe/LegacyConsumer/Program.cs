using System;
using System.Text.Json;
using System.Threading.Tasks;
using MeowChan;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace LibMeowChan.Legacy102Probe.LegacyConsumer;

internal static class Program
{
    private const string ExpectedAdmissionMessage =
        "Await Registry.StartAsync(lease) before restoring or writing to a new profile-bound session.";

    public static async Task<int> Main(string[] args)
    {
        ProbeOptions options;
        try
        {
            options = ProbeOptions.Parse(args, allowAdmissionExpectation: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return WriteResult(started: false, startupAdmissionRejected: false,
                exceptionType: exception.GetType().FullName, exitCode: 2);
        }

        try
        {
            await RunPublished102LifecycleAsync(options.ProfileDirectory).ConfigureAwait(false);
            // The expectation flag is deliberately candidate-only. A successful old
            // sequence is the baseline observation, not a candidate admission pass.
            return WriteResult(started: true, startupAdmissionRejected: false,
                exceptionType: null, exitCode: options.ExpectStartupAdmissionRejection ? 1 : 0);
        }
        catch (InvalidOperationException exception)
            when (options.ExpectStartupAdmissionRejection && exception.Message == ExpectedAdmissionMessage)
        {
            // This is the documented 1.1.0 startup-admission rejection. Do not turn
            // any other crash, loader failure or profile error into a compatibility pass.
            return WriteResult(started: false, startupAdmissionRejected: true,
                exceptionType: exception.GetType().FullName, exitCode: 0);
        }
        catch (Exception exception)
        {
            // Type names only: enough to distinguish missing runtime dependencies
            // from lifecycle failures without leaking profile paths or exception text.
            string initializer = exception is TypeInitializationException typeError
                ? typeError.TypeName ?? "unknown" : "none";
            Console.Error.WriteLine($"Core probe failure: initializer={initializer}; cause={exception.GetBaseException().GetType().FullName}");
            return WriteResult(started: false, startupAdmissionRejected: false,
                exceptionType: exception.GetType().FullName, exitCode: 1);
        }
    }

    private static async Task RunPublished102LifecycleAsync(string profileDirectory)
    {
        // This is intentionally the published 1.0.2 composition: the old binary is
        // compiled against the released assembly and calls RestoreAsync, not StartAsync.
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

        await session.RestoreAsync().ConfigureAwait(false);
    }

    private static int WriteResult(bool started, bool startupAdmissionRejected,
        string? exceptionType, int exitCode)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            probe = "core102",
            started,
            startupAdmissionRejected,
            exceptionType
        }));
        return exitCode;
    }
}

internal sealed record ProbeOptions(string ProfileDirectory, bool ExpectStartupAdmissionRejection)
{
    internal static ProbeOptions Parse(string[] args, bool allowAdmissionExpectation)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? profile = null;
        bool expectRejection = false;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--profile":
                    if (profile is not null || ++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                        throw new ArgumentException("Exactly one non-empty --profile value is required.", nameof(args));
                    profile = args[index];
                    break;
                case "--expect-startup-admission-rejection":
                    if (!allowAdmissionExpectation || expectRejection)
                        throw new ArgumentException("The startup-admission expectation is not valid here.", nameof(args));
                    expectRejection = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.", nameof(args));
            }
        }

        if (profile is null || !System.IO.Path.IsPathFullyQualified(profile))
            throw new ArgumentException("--profile must be one absolute private profile path.", nameof(args));
        return new ProbeOptions(System.IO.Path.TrimEndingDirectorySeparator(profile), expectRejection);
    }
}
