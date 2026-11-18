using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Legacy102Probe;

internal static class Program
{
    private const string ProbeName = "sdk102";
    private const string ExpectedLegacyContractVersion = "1.0.0";
    private const string ExpectedCandidateContractVersion = "1.1.0";
    private static readonly Version ExpectedLegacyAssemblyVersion = new Version(1, 0, 2, 0);
    private static readonly Version ExpectedCandidateAssemblyVersion = new Version(1, 1, 0, 0);

    public static int Main(string[] args)
    {
        try
        {
            string pluginPath = ParsePluginPath(args);
            ProbeReport report = Run(pluginPath);
            Console.WriteLine(Encode(report));
            return report.LegacyLoaded && report.SourceContractOk && report.JsonRoundTripOk ? 0 : 1;
        }
        catch (ProbeArgumentException exception)
        {
            EmitFailure(exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            // Keep diagnostics stable and free of plugin paths, profile data and full exception text.
            EmitFailure(exception.GetType().Name);
            return 1;
        }
    }

    private static string ParsePluginPath(string[] args)
    {
        if (args.Length != 2 || args[0] != "--plugin" || !Path.IsPathFullyQualified(args[1]))
            throw new ProbeArgumentException("usage");
        string path = Path.GetFullPath(args[1]);
        if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.Directory))
            throw new ProbeArgumentException("plugin");
        return path;
    }

    private static ProbeReport Run(string pluginPath)
    {
        string pluginSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pluginPath))).ToLowerInvariant();
        Assembly candidateSdkAssembly = typeof(SdkContract).Assembly;
        Version? candidateAssemblyVersion = candidateSdkAssembly.GetName().Version;
        string candidateSdkVersion = ReadPublicConstant(candidateSdkAssembly, nameof(SdkContract.Version));
        bool candidateAssemblyIsExpected = ExpectedCandidateAssemblyVersion.Equals(candidateAssemblyVersion)
            && candidateSdkVersion == ExpectedCandidateContractVersion;

        ProbeLoadContext loadContext = new ProbeLoadContext(candidateSdkAssembly);
        Assembly pluginAssembly = loadContext.LoadFromAssemblyPath(pluginPath);
        AssemblyName? legacySdkReference = pluginAssembly.GetReferencedAssemblies()
            .FirstOrDefault(reference => string.Equals(reference.Name, candidateSdkAssembly.GetName().Name, StringComparison.Ordinal));
        bool legacyAssemblyIsExpected = ExpectedLegacyAssemblyVersion.Equals(legacySdkReference?.Version);

        Type[] pluginTypes = pluginAssembly.GetExportedTypes()
            .Where(type => typeof(IPlugin).IsAssignableFrom(type)
                && !type.IsAbstract && !type.IsInterface
                && type.GetConstructor(Type.EmptyTypes) is not null)
            .ToArray();
        if (pluginTypes.Length != 1)
            throw new InvalidOperationException("plugin_shape");

        object instance = Activator.CreateInstance(pluginTypes[0])
            ?? throw new InvalidOperationException("plugin_instance");
        if (instance is not IPlugin plugin || instance is not ISourcePlugin source)
            throw new InvalidOperationException("source_cast");

        PluginDescription description = plugin.Describe();
        string legacySdkVersion = description.SdkVersion;
        IReadOnlyList<ValidationIssue> descriptionIssues = SdkValidation.ValidateDescription(description);
        SourceDescription? sourceDescription = description.Sources.SingleOrDefault(value => value is not null);
        if (sourceDescription is null)
            throw new InvalidOperationException("source_description");

        PluginCallContext context = CreateProbeContext();
        SourceBoardId board = new SourceBoardId("legacy102", "board");
        CatalogReadRequest catalogRequest = new CatalogReadRequest(board, Page: 1);
        CatalogReadResult catalogResult = source.ReadCatalogAsync(catalogRequest, context).GetAwaiter().GetResult();
        IReadOnlyList<ValidationIssue> catalogIssues = SdkValidation.ValidateCatalogResult(
            catalogRequest, catalogResult, sourceDescription);
        SourceThreadId thread = new SourceThreadId("legacy102", "board", "thread-001");
        ThreadReadRequest threadRequest = new ThreadReadRequest(thread);
        ThreadReadResult threadResult = source.ReadThreadAsync(threadRequest, context).GetAwaiter().GetResult();
        IReadOnlyList<ValidationIssue> threadIssues = SdkValidation.ValidateThreadResult(
            threadRequest, threadResult, sourceDescription);

        bool returnedExpectedSourceData = catalogResult is CatalogSnapshot catalogSnapshot
            && catalogSnapshot.Snapshot.Board == board
            && catalogSnapshot.Snapshot.Threads.Count == 1
            && catalogSnapshot.Snapshot.Threads[0].Id == thread
            && threadResult is ThreadSnapshot threadSnapshot
            && threadSnapshot.Snapshot.Thread == thread
            && threadSnapshot.Snapshot.Posts.Count == 1;
        bool sourceContractOk = candidateAssemblyIsExpected
            && legacyAssemblyIsExpected
            && legacySdkVersion == ExpectedLegacyContractVersion
            && descriptionIssues.Count == 0
            && catalogIssues.Count == 0
            && threadIssues.Count == 0
            && returnedExpectedSourceData;

        byte[] json = SdkJson.Serialize<CatalogReadResult>(catalogResult);
        CatalogReadResult roundTrip = SdkJson.Deserialize<CatalogReadResult>(json);
        bool jsonRoundTripOk = roundTrip is CatalogSnapshot roundTripSnapshot
            && roundTripSnapshot.Snapshot.Board == board
            && roundTripSnapshot.Snapshot.Threads.Count == 1
            && roundTripSnapshot.Snapshot.Threads[0].Id == thread;

        return new ProbeReport(
            ProbeName,
            legacySdkVersion,
            candidateSdkVersion,
            pluginSha256,
            LegacyLoaded: true,
            SourceContractOk: sourceContractOk,
            JsonRoundTripOk: jsonRoundTripOk);
    }

    private static PluginCallContext CreateProbeContext()
    {
        SettingsSnapshot settings = new SettingsSnapshot("probe", Array.Empty<SettingValue>());
        PluginConfiguration configuration = new PluginConfiguration(
            "probe-revision",
            settings,
            new Dictionary<string, SettingsSnapshot>(StringComparer.Ordinal));
        return new PluginCallContext(configuration, new NoopHttp(), new NoopImages(), new NoopResources(), new NoopLog());
    }

    private static string ReadPublicConstant(Assembly assembly, string fieldName)
    {
        FieldInfo? field = assembly.GetType(typeof(SdkContract).FullName!, throwOnError: true)
            ?.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
        return field?.GetRawConstantValue() as string ?? string.Empty;
    }

    private static string Encode(ProbeReport report)
        => Encoding.UTF8.GetString(SdkJson.Serialize(report));

    private static void EmitFailure(string code)
    {
        ProbeReport report = new ProbeReport(
            ProbeName,
            string.Empty,
            string.Empty,
            string.Empty,
            LegacyLoaded: false,
            SourceContractOk: false,
            JsonRoundTripOk: false);
        Console.WriteLine(Encode(report));
        Console.Error.WriteLine($"SDK binary probe failed: {code}");
    }

    private sealed class ProbeLoadContext : AssemblyLoadContext
    {
        private readonly Assembly _candidateSdkAssembly;

        internal ProbeLoadContext(Assembly candidateSdkAssembly)
            : base("LegacySdk102Probe", isCollectible: true)
        {
            _candidateSdkAssembly = candidateSdkAssembly;
            Resolving += ResolveAssembly;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
            => string.Equals(assemblyName.Name, _candidateSdkAssembly.GetName().Name, StringComparison.Ordinal)
                ? _candidateSdkAssembly
                : null;

        private Assembly? ResolveAssembly(AssemblyLoadContext context, AssemblyName name)
            => string.Equals(name.Name, _candidateSdkAssembly.GetName().Name, StringComparison.Ordinal)
                ? _candidateSdkAssembly
                : null;
    }

    private sealed class NoopHttp : IHostHttp
    {
        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<HostHttpResult>(new HostHttpFailure(new FailureDetail("probe_no_http", "HTTP is unavailable in the offline probe.")));
    }

    private sealed class NoopImages : IImageAssetReader
    {
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ImageAssetChunk(offset, Array.Empty<byte>(), IsEnd: true));
    }

    private sealed class NoopResources : IPluginResources
    {
        public string? ResolvePath(string logicalName) => null;
    }

    private sealed class NoopLog : IPluginLog
    {
        public void Write(PluginLogLevel level, string message)
        {
        }
    }

    private sealed class ProbeArgumentException : Exception
    {
        internal ProbeArgumentException(string message)
            : base(message)
        {
        }
    }

    private sealed record ProbeReport(
        string Probe,
        string LegacySdkVersion,
        string CandidateSdkVersion,
        string LegacyAssemblySha256,
        bool LegacyLoaded,
        bool SourceContractOk,
        bool JsonRoundTripOk);
}
