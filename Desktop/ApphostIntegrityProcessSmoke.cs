using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Desktop.Updates;

namespace MeowChan.Desktop;

/// <summary>Explicit packaged production-policy proof; the sole HTTP seam has no network leg.</summary>
internal sealed class ApphostIntegrityProcessSmoke : IDisposable
{
    internal const string Switch = "--smoke-source-policy-process";
    internal const string Source = "wizchan", Owner = "meowchan.source.wizchan";
    internal const string ThreadUrl = "https://wizchan.org/demo/res/123.html";
    internal const string OriginalUrl = "https://wizchan.org/demo/src/1000-0.png";
    internal ApphostWorkflowSmoke Descriptor { get; }
    internal string Phase { get; }
    internal FixtureHttp Http { get; }
    private ApphostIntegrityProcessSmoke(ApphostWorkflowSmoke descriptor, string phase)
    { Descriptor = descriptor; Phase = phase; Http = new() { Offline = phase != "acquire" }; }

    internal static ApphostIntegrityProcessSmoke Parse(string path, string profile)
    {
        var descriptor = ApphostWorkflowSmoke.Parse(path, profile);
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        string phase = json.RootElement.GetProperty("policyPhase").GetString() ?? "";
        if (descriptor.Phase != "download" || descriptor.PreviousProfile != null || phase is not ("acquire" or "revoke" or "deny" or "recovery"))
            throw new ArgumentException("Policy proof requires owned acquire/revoke/deny/recovery phases on one profile.");
        if (phase == "acquire")
        {
            if (Directory.Exists(profile) || File.Exists(profile) || new DirectoryInfo(profile).LinkTarget != null)
                throw new IOException("Policy acquisition requires a new profile.");
        }
        else
        {
            ApphostWorkflowSmoke.ValidateProfileTree(profile);
            if (File.ReadAllText(Path.Combine(profile, ".workflow-profile")) != descriptor.OwnerToken)
                throw new IOException("Policy proof profile ownership differs.");
            string previous = Path.Combine(profile, "apphost-policy-process.json");
            FileInfo file = new(previous);
            if (!file.Exists || file.LinkTarget != null || file.Length is <= 0 or > 65536)
                throw new IOException("Policy proof needs bounded prior process evidence.");
            using var prior = JsonDocument.Parse(File.ReadAllBytes(previous));
            var value = prior.RootElement;
            if (!value.GetProperty("completed").GetBoolean() || value.GetProperty("failure").ValueKind != JsonValueKind.Null
                || value.GetProperty("phase").GetString() != (phase == "revoke" ? "acquire" : phase == "deny" ? "revoke" : "deny")
                || value.GetProperty("profile").GetString() != profile || value.GetProperty("ownerToken").GetString() != descriptor.OwnerToken)
                throw new IOException("Preceding production-policy process did not complete.");
        }
        return new(descriptor, phase);
    }

    internal DesktopSessionHost CreateSession()
    {
        if (Phase == "acquire")
        {
            if (Directory.Exists(Descriptor.Profile)) throw new IOException("Policy profile already exists.");
            Directory.CreateDirectory(Descriptor.Profile);
            new Config { DownloadPath = Descriptor.DownloadRoot, DownloadsPaused = true, SaveUrlsOnExit = true,
                AutomaticUpdateChecks = false, MinimizeToTray = false, CloseToTray = false,
                MaxParallelDownloads = 1, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0,
                WorkspaceMode = "Barebones", Theme = "Dark", Language = "en" }.SaveConfig(Descriptor.Profile);
            File.WriteAllText(Path.Combine(Descriptor.Profile, ".workflow-profile"), Descriptor.OwnerToken);
        }
        // Restarts load Config through the normal Host constructor. No grant or config is rewritten here.
        return new DesktopSessionHost(Descriptor.Profile, providers: null,
            updateSessionFactory: (path, settings) => new UpdateSession(path, settings, Http.Client(), disableEnvironment: () => "1"),
            pluginTransportFactory: Http.Client);
    }
    public void Dispose() { Http.Dispose(); Descriptor.Dispose(); }

    internal sealed class FixtureHttp : HttpMessageHandler
    {
        private readonly ApphostPluginFixtures _bytes = new();
        internal byte[] Payload => _bytes.Landscape;
        internal bool Offline { get; init; }
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal int Unexpected;
        internal HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string uri = request.RequestUri?.AbsoluteUri ?? "missing";
            Requests.Enqueue(uri);
            byte[]? bytes = null;
            string type = "image/png";
            if (!Offline && request.Method == HttpMethod.Get)
            {
                if (uri == OriginalUrl) bytes = Payload;
                if (uri == "https://wizchan.org/demo/res/123.json")
                {
                    type = "application/json";
                    bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { posts = new[] { new {
                        no = 123, resto = 0, sub = "Production policy OS restart", com = "Controlled fixture",
                        time = 1700000000, tim = "1000-0", ext = ".png", filename = "landscape",
                        fsize = Payload.Length + 86, md5 = Convert.ToBase64String(MD5.HashData(new byte[] { 9, 9, 9 })) } } }));
                }
            }
            if (bytes == null)
            { Interlocked.Increment(ref Unexpected); throw new InvalidOperationException("No network leg: unexpected policy fixture request " + uri); }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new(type);
            return Task.FromResult(response);
        }
        protected override void Dispose(bool disposing) { if (disposing) _bytes.Dispose(); base.Dispose(disposing); }
    }
}
