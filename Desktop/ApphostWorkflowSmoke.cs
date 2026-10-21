using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Boards.FourChan;
using MeowChan.Interfaces;

namespace MeowChan.Desktop;

/// <summary>Explicit packaged-app acceptance with an owned temporary profile and a transport that cannot reach the network.</summary>
internal sealed class ApphostWorkflowSmoke : IDisposable
{
    internal const string Switch = "--smoke-workflow";
    internal const string ThreadUrl = "https://boards.4chan.org/g/thread/123";
    internal static readonly byte[] Payload = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAACAAAAAYCAIAAAAUMWhjAAAAJElEQVR4nGMwmraFpohh1IJRC0YtGLVg1IJRC0YtGLVgaFgAACLJdD2wiEkfAAAAAElFTkSuQmCC");
    internal string Root { get; }
    internal string Profile { get; }
    internal string? PreviousProfile { get; }
    internal string Phase { get; }
    internal string OwnerToken { get; }
    internal double ExpectedScale { get; }
    internal string DownloadRoot => Path.Combine(Root, Phase == "shutdown" ? "downloads-shutdown" : "downloads");
    internal Transport Http { get; } = new();
    private HttpClient? _client;

    private ApphostWorkflowSmoke(string root, string profile, string? previous, string phase, string token, double scale)
    { Root = root; Profile = profile; PreviousProfile = previous; Phase = phase; OwnerToken = token; ExpectedScale = scale; }

    internal static ApphostWorkflowSmoke Parse(string descriptor, string profile)
    {
        if (!Path.IsPathFullyQualified(descriptor)) throw new ArgumentException("Workflow descriptor must be absolute.");
        descriptor = Path.GetFullPath(descriptor);
        RequireRegularFile(descriptor, 16384);
        try
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(descriptor));
            JsonElement value = json.RootElement;
            string root = Path.GetFullPath(value.GetProperty("root").GetString() ?? string.Empty);
            string phase = value.GetProperty("phase").GetString() ?? string.Empty;
            string token = value.GetProperty("ownerToken").GetString() ?? string.Empty;
            double scale = value.GetProperty("expectedScale").GetDouble();
            string? previous = value.GetProperty("previousProfile").ValueKind == JsonValueKind.Null
                ? null : value.GetProperty("previousProfile").GetString();
            if (value.GetProperty("schemaVersion").GetInt32() != 1 || phase is not ("download" or "restore" or "removed" or "shutdown")
                || scale is not (1 or 1.5 or 2) || token.Length != 32 || !token.All(char.IsAsciiHexDigit))
                throw new ArgumentException("Invalid workflow descriptor contract.");
            DirectoryInfo directory = new(root);
            if (!directory.Exists || directory.LinkTarget != null || !directory.Name.StartsWith("meowchan-workflow-", StringComparison.Ordinal)
                || directory.Parent == null || !SamePath(CanonicalDirectory(directory.Parent.FullName), CanonicalDirectory(Path.GetTempPath()))
                || !SamePath(Path.GetDirectoryName(descriptor), root)
                || Path.GetFileName(descriptor) != phase + ".json" || !SamePath(profile, Path.Combine(root, "profile-" + phase)))
                throw new ArgumentException("Workflow paths must belong to the new owned temporary root.");
            string marker = Path.Combine(root, ".workflow-owner");
            RequireRegularFile(marker, 128);
            if (File.ReadAllText(marker).Trim() != token) throw new ArgumentException("Workflow owner token does not match.");
            string? requiredPrevious = phase == "restore" ? "download" : phase == "removed" ? "restore" : null;
            if (requiredPrevious == null ? previous != null : !SamePath(previous, Path.Combine(root, "profile-" + requiredPrevious)))
                throw new ArgumentException("Workflow restore source does not match its preceding phase.");
            if (previous != null)
            {
                previous = Path.GetFullPath(previous);
                ValidateProfileTree(previous);
                string ownership = Path.Combine(previous, ".workflow-profile");
                RequireRegularFile(ownership, 128);
                if (File.ReadAllText(ownership).Trim() != token) throw new ArgumentException("The preceding profile is not owned by this workflow.");
                string evidence = Path.Combine(previous, "apphost-workflow.json");
                RequireRegularFile(evidence, 65536);
                using JsonDocument prior = JsonDocument.Parse(File.ReadAllBytes(evidence));
                if (!prior.RootElement.GetProperty("completed").GetBoolean() || prior.RootElement.GetProperty("phase").GetString() != requiredPrevious
                    || prior.RootElement.GetProperty("failure").ValueKind != JsonValueKind.Null)
                    throw new ArgumentException("The preceding workflow phase did not complete.");
            }
            return new ApphostWorkflowSmoke(root, profile, previous, phase, token, scale);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new ArgumentException("Invalid workflow descriptor.", error); }
    }

    internal DesktopSessionHost CreateSession()
    {
        // Parse and normal startup both require this directory to be absent. Recheck before writing.
        if (Directory.Exists(Profile) || File.Exists(Profile) || new DirectoryInfo(Profile).LinkTarget != null)
            throw new IOException("Workflow profile already exists.");
        Directory.CreateDirectory(Profile);
        if (PreviousProfile != null)
        {
            ValidateProfileTree(PreviousProfile);
            foreach (string directory in Directory.EnumerateDirectories(PreviousProfile, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(Profile, Path.GetRelativePath(PreviousProfile, directory)));
            foreach (string file in Directory.EnumerateFiles(PreviousProfile, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(Profile, Path.GetRelativePath(PreviousProfile, file)));
        }
        File.WriteAllText(Path.Combine(Profile, ".workflow-profile"), OwnerToken);
        Config config = PreviousProfile == null ? new Config
        {
            DownloadPath = DownloadRoot, WorkspaceMode = "Barebones", Theme = "Dark", Language = "en",
            RecheckTimer = 60, MaxParallelDownloads = 1, MinimizeToTray = false, CloseToTray = false
        } : Config.LoadConfig(Profile);
        if (!SamePath(config.DownloadPath, DownloadRoot) || config.MinimizeToTray || config.CloseToTray)
            throw new IOException("Restored fixture settings refer outside the workflow or alter close behavior.");
        config.SaveConfig(Profile);
        if (PreviousProfile != null) Http.AllowMedia();
        _client = new HttpClient(Http);
        return new DesktopSessionHost(Profile, new IImageBoard[] { new FourChanImageBoard(_client) });
    }

    internal static void ValidateProfileTree(string path)
    {
        if (!Directory.Exists(path) || new DirectoryInfo(path).LinkTarget != null)
            throw new ArgumentException("Workflow restore profile must be a regular directory.");
        // Inspect one level at a time; never recurse through a link before rejecting it.
        long bytes = 0; int count = 0;
        void Visit(string directory)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 256 || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Workflow restore profile contains links or too many entries.");
                if (Directory.Exists(entry)) Visit(entry);
                else
                {
                    bytes += new FileInfo(entry).Length;
                    if (bytes > 64L * 1024 * 1024) throw new ArgumentException("Workflow restore profile exceeds 64 MiB.");
                }
            }
        }
        Visit(path);
    }

    private static void RequireRegularFile(string path, long limit)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.LinkTarget != null || file.Length is <= 0 || file.Length > limit)
            throw new ArgumentException("Workflow input is not a bounded regular file.");
    }
    internal static bool SamePath(string? first, string second) => first != null && string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string CanonicalDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            DirectoryInfo part = new(Path.Combine(current, segment));
            current = part.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? part.FullName;
        }
        return current;
    }
    public void Dispose() => _client?.Dispose();

    /// <summary>No HttpClientHandler or socket exists behind this transport; every unexpected URI fails closed.</summary>
    internal sealed class Transport : HttpMessageHandler
    {
        private readonly TaskCompletionSource _allow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests, _cancelled, _unexpected;
        internal int MediaRequests => Volatile.Read(ref _requests);
        internal int CancelledRequests => Volatile.Read(ref _cancelled);
        internal int UnexpectedRequests => Volatile.Read(ref _unexpected);
        internal void AllowMedia() => _allow.TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri? uri = request.RequestUri;
            if (request.Method == HttpMethod.Get && uri?.AbsoluteUri == "https://i.4cdn.org/g/777.png")
            {
                Interlocked.Increment(ref _requests);
                try { await _allow.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { Interlocked.Increment(ref _cancelled); throw; }
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
            }
            if (request.Method == HttpMethod.Get && uri?.AbsoluteUri == "https://a.4cdn.org/boards.json")
                return Json("{\"boards\":[{\"board\":\"g\",\"title\":\"Technology\"}]}");
            if (request.Method == HttpMethod.Get && uri?.AbsoluteUri == "https://a.4cdn.org/g/thread/123.json")
                return Json(JsonSerializer.Serialize(new
                {
                    posts = new[] { new { no = 123, sub = "Packaged offline workflow", com = "Owned fixture &amp; verified bytes.",
                        time = 1700000000, tim = 777, filename = "fixture", ext = ".png", fsize = Payload.Length,
                        md5 = Convert.ToBase64String(MD5.HashData(Payload)) } }
                }));
            Interlocked.Increment(ref _unexpected);
            throw new InvalidOperationException("Network blocked: unexpected workflow fixture request " + uri);
        }
        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
}
