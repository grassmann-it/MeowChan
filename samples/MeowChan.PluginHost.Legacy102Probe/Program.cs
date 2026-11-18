using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginSdk;
using SdkThreadSnapshot = MeowChan.PluginSdk.ThreadSnapshot;

namespace MeowChan.PluginHost.Legacy102Probe;

internal static class Program
{
    private const string Scope = "legacy102-hostworker";
    private const string ExpectedPluginId = "legacy102.probe";
    private const string ExpectedPluginVersion = "1.0.2";
    private const string ExpectedDeclaredSdkVersion = "1.0.0";
    private const string ExpectedSourceId = "legacy102";
    private const string ExpectedBoardId = "board";
    private const string ExpectedThreadId = "thread-001";
    private const string ExpectedCatalogRevision = "legacy102-catalog-1";
    private const string ExpectedThreadRevision = "legacy102-thread-1";
    private const string ExpectedPostId = "post-001";
    private const string ExpectedPostText = "Deterministic SDK 1.0.2 payload";
    private const SourceFieldPresence ExpectedPostFields = SourceFieldPresence.PostedAt
        | SourceFieldPresence.Subject
        | SourceFieldPresence.Author
        | SourceFieldPresence.Text
        | SourceFieldPresence.Attachments;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(15);

    public static async Task<int> Main(string[] args)
    {
        ProbeOptions options;
        try
        {
            options = ProbeOptions.Parse(args);
        }
        catch (ProbeArgumentException exception)
        {
            EmitDiagnostic(exception.Code);
            Console.WriteLine(JsonSerializer.Serialize(EmptyReport()));
            return 2;
        }

        try
        {
            ProbeReport report = await RunAsync(options).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(report));
            return report.Passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            // Do not expose plugin/profile paths or child diagnostics in the
            // result. The runner receives one bounded JSON object per invocation.
            EmitDiagnostic(exception.GetType().Name);
            Console.WriteLine(JsonSerializer.Serialize(EmptyReport()));
            return 1;
        }
    }

    private static async Task<ProbeReport> RunAsync(ProbeOptions options)
    {
        string pluginHashBefore = string.Empty;
        string pluginHashAfter = string.Empty;
        string declaredSdkVersion = string.Empty;
        bool workerProcessObserved = false;
        bool describeOk = false;
        bool sourceCallsOk = false;
        Exception? failure = null;
        PluginWorkerManager? manager = null;
        IPluginWorkerClient? worker = null;
        PrivateEnvironment? privateEnvironment = null;
        bool inputFilesValidated = false;

        try
        {
            ValidateInputFiles(options);
            inputFilesValidated = true;
            EnsureFreshProfile(options.ProfilePath);
            pluginHashBefore = TryHash(options.PluginPath);
            privateEnvironment = PrivateEnvironment.Apply(options.ProfilePath);

            using CancellationTokenSource deadline = new(ProbeTimeout);
            CancellationToken cancellationToken = deadline.Token;
            string dotnetHost = ResolveDotnetHost();
            PluginWorkerLaunch launch = new(dotnetHost, [options.WorkerPath], options.ProfilePath);
            PluginArtifact artifact = CreateArtifact(options.PluginPath, pluginHashBefore);

            manager = new PluginWorkerManager(new PluginWorkerOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(10),
                OperationTimeout = TimeSpan.FromSeconds(30),
                CancellationGrace = TimeSpan.FromSeconds(2),
                MaximumProductionWorkers = 1,
                MaximumTestWorkers = 1
            });

            // DescribeAsync is the public manager describe path. It starts a
            // real short-lived current worker with host services unavailable.
            PluginDescription described = await manager.DescribeAsync(launch, artifact, cancellationToken).ConfigureAwait(false);
            declaredSdkVersion = described.SdkVersion;
            describeOk = IsExpectedDescription(described)
                && SdkValidation.ValidateDescription(described).Count == 0;

            PluginConfiguration configuration = CreateConfiguration();
            worker = await manager.StartAsync(launch, artifact, configuration, NoHostServices.Instance,
                testWorker: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            workerProcessObserved = worker.Snapshot.ProcessId is > 0;

            // StartAsync performs a second real Describe through the current
            // child worker. Validate that independent observation as well.
            describeOk &= IsExpectedDescription(worker.Description);
            sourceCallsOk = await ReadExpectedSourceAsync(worker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            failure = await DisposeBoundAsync(worker, failure).ConfigureAwait(false);
            failure = await DisposeBoundAsync(manager, failure).ConfigureAwait(false);
            try { privateEnvironment?.Dispose(); }
            catch (Exception exception) { failure ??= exception; }
            if (inputFilesValidated) pluginHashAfter = TryHash(options.PluginPath);
        }

        bool pluginUnchanged = !string.IsNullOrEmpty(pluginHashBefore)
            && string.Equals(pluginHashBefore, pluginHashAfter, StringComparison.Ordinal);
        bool passed = failure is null && pluginUnchanged && workerProcessObserved && describeOk && sourceCallsOk;
        if (failure is not null)
            EmitDiagnostic(failure.GetType().Name);
        else if (!pluginUnchanged)
            EmitDiagnostic("plugin-hash-changed");

        return new ProbeReport(
            Scope,
            passed,
            pluginHashBefore,
            declaredSdkVersion,
            workerProcessObserved,
            describeOk,
            sourceCallsOk);
    }

    private static async Task<bool> ReadExpectedSourceAsync(
        IPluginWorkerClient worker, CancellationToken cancellationToken)
    {
        SourceBoardId board = new(ExpectedSourceId, ExpectedBoardId);
        SourceThreadId thread = new(ExpectedSourceId, ExpectedBoardId, ExpectedThreadId);
        PluginOperationContext operation = new("legacy102/board/thread-001", 1);

        CatalogReadResult catalog = await worker.ReadCatalogAsync(
            new CatalogReadRequest(board, Page: 1), operation, cancellationToken).ConfigureAwait(false);
        ThreadReadResult threadResult = await worker.ReadThreadAsync(
            new ThreadReadRequest(thread), operation, cancellationToken).ConfigureAwait(false);

        bool catalogOk = catalog is CatalogSnapshot
        {
            Snapshot.Board: var catalogBoard,
            Snapshot.Threads: var threads,
            Snapshot.IsComplete: true,
            Snapshot.Validators.Revision: ExpectedCatalogRevision
        }
        && catalogBoard == board
        && threads.Count == 1
        && threads[0] is
        {
            Id: var catalogThread,
            Title: "Synthetic legacy thread",
            OpeningText: "Deterministic catalog fixture",
            ReplyCount: 1,
            AttachmentCount: 1
        }
        && catalogThread == thread;

        bool threadOk = threadResult is SdkThreadSnapshot
        {
            Snapshot.Thread: var returnedThread,
            Snapshot.Title: "Synthetic legacy thread",
            Snapshot.SourceState: SourceThreadState.Active,
            Snapshot.Posts: var posts,
            Snapshot.Validators.Revision: ExpectedThreadRevision
        }
        && returnedThread == thread
        && posts.Count == 1
        && posts[0] is
        {
            PostId: ExpectedPostId,
            Ordinal: 1,
            Fields: ExpectedPostFields,
            Subject: "Legacy fixture",
            Author: "probe",
            Text: ExpectedPostText,
            Attachments: var attachments
        }
        && attachments is { Count: 1 }
        && attachments[0] is
        {
            Position: 0,
            MediaId: "media-001",
            Variant: "original",
            ExpectedSize: 16,
            MimeType: "application/octet-stream"
        };

        return catalogOk && threadOk;
    }

    private static bool IsExpectedDescription(PluginDescription description)
    {
        SourceDescription? source = description.Sources.SingleOrDefault(item => item.SourceId == ExpectedSourceId);
        return description.PluginId == ExpectedPluginId
            && description.Name == "Legacy 1.0.2 SDK probe"
            && description.PluginVersion == ExpectedPluginVersion
            && description.SdkVersion == ExpectedDeclaredSdkVersion
            && description.Description == "Deterministic offline source used to exercise the published SDK binary."
            && description.Sources.Count == 1
            && source is not null
            && source.Name == "Legacy 1.0.2 source"
            && source.Capabilities == (SourceCapabilities.ThreadRead
                | SourceCapabilities.BoardCatalog
                | SourceCapabilities.PostCapture)
            && source.Routes.Count == 2
            && source.HttpHosts.Count == 1
            && source.Settings.Version == "legacy102-settings-1"
            && description.Settings.Version == "legacy102-settings-1"
            && description.Filters.Count == 0
            && description.Information.Count == 1
            && description.Information[0].Key == "fixture"
            && description.Information[0].Value == "legacy-102";
    }

    private static PluginConfiguration CreateConfiguration()
    {
        SettingsSnapshot empty = new("legacy102-settings-1", []);
        return new PluginConfiguration(
            "legacy102-hostworker-config",
            empty,
            new Dictionary<string, SettingsSnapshot>(StringComparer.Ordinal)
            {
                [ExpectedSourceId] = empty
            });
    }

    private static PluginArtifact CreateArtifact(string pluginPath, string pluginHash)
    {
        string sdkPath = typeof(IPlugin).Assembly.Location;
        return new PluginArtifact(
            "legacy102-hostworker-compile",
            "legacy102-hostworker-execution",
            pluginPath,
            pluginHash,
            HashFile(sdkPath),
            "legacy102-hostworker-probe",
            []);
    }

    private static void ValidateInputFiles(ProbeOptions options)
    {
        if (IsForbiddenPath(options.PluginPath) || IsForbiddenPath(options.WorkerPath))
            throw new ProbeArgumentException("private-input");
        RequireFile(options.PluginPath, "plugin");
        RequireFile(options.WorkerPath, "worker");
        if (!Path.GetExtension(options.PluginPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new ProbeArgumentException("plugin-extension");
        if (!Path.GetExtension(options.WorkerPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new ProbeArgumentException("worker-extension");
    }

    private static void EnsureFreshProfile(string path)
    {
        if (IsForbiddenPath(path)) throw new ProbeArgumentException("profile-private");
        if (File.Exists(path)) throw new ProbeArgumentException("profile-file");
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new ProbeArgumentException("profile-not-new");
        Directory.CreateDirectory(path);
    }

    private static void RequireFile(string path, string label)
    {
        if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.Directory))
            throw new ProbeArgumentException(label);
    }

    private static string ResolveDotnetHost()
    {
        bool isWindows = OperatingSystem.IsWindows();
        string fileName = isWindows ? "dotnet.exe" : "dotnet";
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (IsUsableDotnet(configured, fileName)) return Path.GetFullPath(configured!);

        string? processPath = Environment.ProcessPath;
        if (IsUsableDotnet(processPath, fileName)) return Path.GetFullPath(processPath!);

        foreach (string raw in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            string directory = raw.Trim().Trim('"');
            if (directory.Length == 0 || !Path.IsPathFullyQualified(directory)) continue;
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("No usable dotnet host.");
    }

    private static bool IsUsableDotnet(string? path, string fileName)
        => !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && string.Equals(Path.GetFileName(path), fileName,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && File.Exists(path);

    private static async Task<Exception?> DisposeBoundAsync(IAsyncDisposable? disposable, Exception? existing)
    {
        if (disposable is null) return existing;
        try
        {
            await disposable.DisposeAsync().AsTask().WaitAsync(DisposeTimeout).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return existing ?? exception;
        }
        return existing;
    }

    private static string TryHash(string path)
    {
        try { return File.Exists(path) ? HashFile(path) : string.Empty; }
        catch { return string.Empty; }
    }

    private static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsForbiddenPath(string path)
    {
        foreach (string part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Equals(".ochan", StringComparison.OrdinalIgnoreCase)
                || part.Equals("authstore", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return path.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase);
    }

    private static ProbeReport EmptyReport() => new(Scope, false, string.Empty, string.Empty, false, false, false);

    private static void EmitDiagnostic(string code)
        => Console.Error.WriteLine($"Legacy 1.0.2 host/worker probe failed: {code}");

    private sealed record ProbeReport(
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("passed")] bool Passed,
        [property: JsonPropertyName("legacyPluginSha256")] string LegacyPluginSha256,
        [property: JsonPropertyName("declaredSdkVersion")] string DeclaredSdkVersion,
        [property: JsonPropertyName("workerProcessObserved")] bool WorkerProcessObserved,
        [property: JsonPropertyName("describeOk")] bool DescribeOk,
        [property: JsonPropertyName("sourceCallsOk")] bool SourceCallsOk);

    private sealed record ProbeOptions(string PluginPath, string WorkerPath, string ProfilePath)
    {
        internal static ProbeOptions Parse(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);
            string? plugin = null;
            string? worker = null;
            string? profile = null;
            for (int index = 0; index < args.Length; index++)
            {
                string value = args[index];
                if (index + 1 >= args.Length || value is not ("--plugin" or "--worker" or "--profile"))
                    throw new ProbeArgumentException("usage");
                string argument = args[++index];
                if (string.IsNullOrWhiteSpace(argument) || !Path.IsPathFullyQualified(argument))
                    throw new ProbeArgumentException("absolute-path");
                string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(argument));
                if (value == "--profile" && string.Equals(normalized, Path.GetPathRoot(normalized),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new ProbeArgumentException("profile-root");
                switch (value)
                {
                    case "--plugin" when plugin is null: plugin = normalized; break;
                    case "--worker" when worker is null: worker = normalized; break;
                    case "--profile" when profile is null: profile = normalized; break;
                    default: throw new ProbeArgumentException("duplicate-option");
                }
            }
            if (plugin is null || worker is null || profile is null)
                throw new ProbeArgumentException("usage");
            return new ProbeOptions(plugin, worker, profile);
        }
    }

    private sealed class ProbeArgumentException(string code) : Exception
    {
        internal string Code { get; } = code;
    }

    private sealed class NoHostServices : IPluginHostServices
    {
        internal static readonly NoHostServices Instance = new();

        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request,
            PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Offline probe forbids HTTP host services.");

        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count,
            PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Offline probe forbids image host services.");
    }

    private sealed class PrivateEnvironment : IDisposable
    {
        private static readonly string[] PrivateDirectoryVariables =
        [
            "HOME", "TMPDIR", "TMP", "TEMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
            "DOTNET_CLI_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME",
            "XDG_RUNTIME_DIR"
        ];
        private readonly Dictionary<string, string?> previous;
        private bool disposed;

        private PrivateEnvironment(Dictionary<string, string?> previous) => this.previous = previous;

        internal static PrivateEnvironment Apply(string profile)
        {
            string privateRoot = Path.Combine(profile, "private");
            Directory.CreateDirectory(privateRoot);
            Dictionary<string, string?> previous = new(StringComparer.Ordinal);
            try
            {
                foreach (string name in PrivateDirectoryVariables)
                {
                    string directory = Path.Combine(privateRoot, name);
                    Directory.CreateDirectory(directory);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    previous[name] = Environment.GetEnvironmentVariable(name);
                    Environment.SetEnvironmentVariable(name, directory);
                }
                previous["DOTNET_NOLOGO"] = Environment.GetEnvironmentVariable("DOTNET_NOLOGO");
                previous["DOTNET_CLI_TELEMETRY_OPTOUT"] = Environment.GetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT");
                previous["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = Environment.GetEnvironmentVariable("DOTNET_SKIP_FIRST_TIME_EXPERIENCE");
                Environment.SetEnvironmentVariable("DOTNET_NOLOGO", "1");
                Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
                Environment.SetEnvironmentVariable("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1");
                return new PrivateEnvironment(previous);
            }
            catch
            {
                foreach ((string name, string? value) in previous)
                    Environment.SetEnvironmentVariable(name, value);
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach ((string name, string? value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
