using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginProtocol;
using MeowChan.PluginSdk;
using MeowChan.TestSupport;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MeowChan.DesktopTests;

public sealed class WorkerMulticallTests
{
    private static string Application => BuiltTestBinaries.AppHostDirectory(".", "MeowChan");
    private static string Apphost => Path.Combine(Application, OperatingSystem.IsWindows() ? "MeowChan.exe" : "MeowChan");
    private static string Toolchain => Path.Combine(Application, "plugin-compiler");
    private static PluginConfiguration Configuration(params string[] sources) =>
        new("config", new("1", []), sources.ToDictionary(source => source, _ => new SettingsSnapshot("1", [])));

    [Fact]
    public async Task WorkerHandshakeRunsWithoutADisplayAndExitsOnEof()
    {
        using TempDirectory workspace = new();
        ProcessStartInfo start = new(Apphost)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workspace.Path,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--meowchan-plugin-worker");
        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");
        start.Environment["MEOWCHAN_PROFILE"] = Path.Combine(workspace.Path, "unexpected-profile");
        using Process worker = Process.Start(start)!;
        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using FrameCodec codec = new(worker.StandardOutput.BaseStream, worker.StandardInput.BaseStream);
            WireEnvelope request = new(1, "request", "hello", "hello", "worker", "build", "config",
                null, null, null, null, null, WireJson.Element(new HelloRequest(["describe"])));
            await codec.WriteAsync(request, deadline.Token);
            WireEnvelope response = Assert.IsType<WireEnvelope>(await codec.ReadAsync(deadline.Token));
            Assert.Equal(("response", "hello"), (response.Direction, response.RequestId));
            Assert.Contains("describe", WireJson.Read<HelloResponse>(response.Payload).Operations);
            worker.StandardInput.Close();
            await worker.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, worker.ExitCode);
            Assert.False(Directory.Exists(start.Environment["MEOWCHAN_PROFILE"]));
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task DescribingAPluginDoesNotLoadAvaloniaAssemblies()
    {
        using TempDirectory workspace = new();
        PluginArtifact artifact = BuildFixture(workspace.Path, """
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name!.StartsWith("Avalonia", StringComparison.Ordinal))
                ? "loaded" : "absent"
            """);
        await using PluginWorkerManager manager = new();
        PluginDescription description = await manager.DescribeAsync(Launch(workspace), artifact);
        Assert.Equal("absent", Assert.Single(description.Information).Value);
    }

    [Fact]
    public async Task PluginWithCoreAssemblyReferenceIsRejectedBeforeLoading()
    {
        using TempDirectory workspace = new();
        PluginArtifact artifact = BuildFixture(workspace.Path, """typeof(MeowChan.Registry).FullName!""", addCoreReference: true);
        await using PluginWorkerManager manager = new();
        PluginWorkerException failure = await Assert.ThrowsAsync<PluginWorkerException>(() =>
            manager.DescribeAsync(Launch(workspace), artifact));
        Assert.Equal("forbidden_reference", failure.Code);
        Assert.Equal("libmeowchan", failure.Message);
    }

    [Fact]
    public async Task TimeoutReapsTheApphostWorkerAndItsChild()
    {
        using TempDirectory workspace = new();
        PluginArtifact artifact = BuildFixture(workspace.Path, "StartChild()", """
            private static string StartChild()
            {
                string command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sleep";
                string args = OperatingSystem.IsWindows() ? "/d /c ping -n 31 127.0.0.1 > nul" : "30";
                using Process child = Process.Start(new ProcessStartInfo(command, args)
                {
                    UseShellExecute = false, CreateNoWindow = true
                })!;
                return child.Id.ToString(CultureInfo.InvariantCulture);
            }
            """);
        await using PluginWorkerManager manager = new(new()
        {
            OperationTimeout = TimeSpan.FromSeconds(2), CancellationGrace = TimeSpan.FromMilliseconds(100)
        });
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace), artifact, Configuration(), new FixtureServices());
        int workerId = Assert.IsType<int>(worker.Snapshot.ProcessId);
        int childId = int.Parse(Assert.Single(worker.Description.Information).Value!, System.Globalization.CultureInfo.InvariantCulture);
        using Process apphostWorker = Process.GetProcessById(workerId);
        using Process child = Process.GetProcessById(childId);
        Assert.False(child.HasExited);
        PluginWorkerException failure = await Assert.ThrowsAsync<PluginWorkerException>(() =>
            worker.ValidateSettingsAsync(new(SettingScope.Plugin, new("1", []))));
        Assert.Equal("timeout", failure.Code);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await Task.WhenAll(apphostWorker.WaitForExitAsync(deadline.Token), child.WaitForExitAsync(deadline.Token));
    }

    [Fact]
    public async Task CompilerModeBuildsAnExampleAndWorkerEvaluatesIt()
    {
        using TempDirectory workspace = new();
        string source = Path.Combine(Application, "plugins", "sources", "FileRulesFilter.cs");
        string sdk = Assert.Single(typeof(ProcessEntry).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            attribute => attribute.Key == "MeowChan.PluginSdkSha256").Value!;
        Assert.Equal(sdk, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Toolchain, "MeowChan.PluginSdk.dll")))));
        await using PluginCompilationService compiler = new(new PluginCompilerOptions(Apphost, Toolchain, Path.Combine(workspace.Path, "cache"))
        {
            EntryArgument = "--meowchan-plugin-compiler"
        });
        PluginBuildResult build = await compiler.BuildAsync(source);
        Assert.True(build.Succeeded, string.Join(Environment.NewLine, build.Diagnostics));
        Assert.False(build.CacheHit);
        await using PluginWorkerManager manager = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace), build.Artifact!, Configuration(), new FixtureServices(),
            filterWorker: true);
        Assert.Equal("meowchan.filter.file-rules", worker.Description.PluginId);
        var request = new ImageFilterRequest("file-rules", new(new(new("4chan", "g", "42"), "media"), "42", 0, new("https://fixture.invalid/a.png")),
            "input", "media", "asset", 4, Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])), "rule", new("1", []),
            build.Artifact!.ExecutionRevision, "config", "grant");
        Assert.Equal(ImageFilterOutcome.Match, (await worker.EvaluateAsync(request, new("4chan/g/42", 1, "rule", "grant"))).Outcome);
        Assert.True((await compiler.BuildAsync(source)).CacheHit);
    }

    [Fact]
    public async Task PublishedSourcesUseTheApphostWorkerForCatalogThreadAndSearch()
    {
        using TempDirectory workspace = new();
        await using PluginWorkerManager manager = new();
        PluginWorkerLaunch launch = Launch(workspace);
        await using IPluginWorkerClient chan = await manager.StartAsync(launch, Shipped("MeowChan.Plugin.FourChan"), Configuration("4chan"), new FixtureServices());
        var catalog = Assert.IsType<CatalogSnapshot>(await chan.ReadCatalogAsync(new(new("4chan", "g")), new("catalog", 1)));
        Assert.Equal("42", Assert.Single(catalog.Snapshot.Threads).Id.ThreadId);
        var thread = Assert.IsType<MeowChan.PluginSdk.ThreadSnapshot>(await chan.ReadThreadAsync(new(new("4chan", "g", "42")), new("thread", 1)));
        Assert.Equal("42", Assert.Single(thread.Snapshot.Posts).PostId);
        await using IPluginWorkerClient booru = await manager.StartAsync(launch, Shipped("MeowChan.Plugin.Danbooru"), Configuration("danbooru"), new FixtureServices());
        var search = Assert.IsType<BooruSearchSnapshot>(await booru.SearchBooruPostsAsync(new("danbooru", "cat", 1, 20), new("search", 1)));
        Assert.Equal("1003", Assert.Single(search.Page.Posts).Id.PostId);
    }

    private static PluginWorkerLaunch Launch(TempDirectory workspace) =>
        new(Apphost, ["--meowchan-plugin-worker"], Path.Combine(workspace.Path, "workers"));

    private static PluginArtifact Shipped(string name) =>
        Artifact(Path.Combine(Application, "plugins", "assemblies", name + ".dll"));

    private static PluginArtifact Artifact(string path) =>
        new("compile", Path.GetFileNameWithoutExtension(path), path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), "sdk", "compiler", [])
        { VerifyAssemblyOnLoad = true };

    private static PluginArtifact BuildFixture(string directory, string observation, string members = "", bool addCoreReference = false)
    {
        string source = $$"""
            using System;
            using System.Collections.Generic;
            using System.Diagnostics;
            using System.Globalization;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using MeowChan.PluginSdk;
            public sealed class Fixture : IImageFilterPlugin, IPluginSettingsValidator
            {
                public PluginDescription Describe() => new("fixture", "Fixture", "1.0.0", SdkContract.Version, "Worker fixture", [],
                    [new("fixture", "Fixture", "Fixture filter", FilterDataAccess.CandidateMetadata, new("1", []), [])],
                    new("1", []), [new("state", "State", InformationKind.Value, {{observation}})]);
                public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginCallContext context, CancellationToken token)
                    => throw new NotSupportedException();
                public async Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request,
                    PluginCallContext context, CancellationToken token)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
                    return [];
                }
                {{members}}
            }
            """;
        var references = Directory.GetFiles(Path.Combine(Toolchain, "references"), "*.dll")
            .Append(typeof(IPlugin).Assembly.Location).Select(path => MetadataReference.CreateFromFile(path)).ToList();
        if (addCoreReference) references.Add(MetadataReference.CreateFromFile(typeof(Registry).Assembly.Location));
        string dll = Path.Combine(directory, "fixture.dll");
        var compilation = CSharpCompilation.Create("WorkerMulticallFixture", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using (Stream stream = File.Create(dll))
        {
            var result = compilation.Emit(stream);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        }
        return Artifact(dll);
    }

    private sealed class FixtureServices : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
        {
            string json = request.Url.AbsoluteUri switch
            {
                "https://a.4cdn.org/g/catalog.json" => """[{"threads":[{"no":42}]}]""",
                "https://a.4cdn.org/g/thread/42.json" => """{"posts":[{"no":42,"time":1700000000,"com":"fixture"}]}""",
                "https://danbooru.donmai.us/posts.json?tags=cat&page=1&limit=20" => """[{"id":1003,"rating":"g"}]""",
                _ => throw new InvalidOperationException("Unexpected request: " + request.Url)
            };
            return Task.FromResult<HostHttpResult>(new HostHttpResponse(200, request.Url,
                new Dictionary<string, string> { ["Content-Type"] = "application/json" }, Encoding.UTF8.GetBytes(json)));
        }

        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
