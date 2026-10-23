using System.Collections.Concurrent;
using System.Diagnostics;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginProtocol;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Runtime;

internal sealed class WorkerConnection : IAsyncDisposable
{
    private readonly Process _process;
    private readonly int _processId;
    private readonly OwnedProcessTree _processTree;
    private readonly Func<long>? _temporaryBytes;
    private readonly FrameCodec _codec;
    private readonly PluginWorkerOptions _options;
    private readonly IPluginHostServices _services;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<string, Task> _serviceTasks = new();
    private readonly Task _reader;
    private readonly Task _logs;
    private readonly Task _temporaryMonitor;
    private readonly string _workingDirectory;
    private long _observedTemporaryBytes;
    private readonly BoundedLog _log;
    private readonly IDisposable _processLease;
    private Exception? _failure;
    private Task _failureCancellation = Task.CompletedTask;
    private int _disposed;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    public PluginWorkerIdentity Identity { get; }
    public PluginDescription Description { get; private set; } = null!;
    public bool IsAlive
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0 || _failure != null) return false;
            try { return !_process.HasExited; } catch (InvalidOperationException) { return false; }
        }
    }
    public PluginWorkerSnapshot Snapshot => new(Identity, IsAlive ? "ready" : "unavailable", _processId,
        (_failure as PluginWorkerException)?.Code ?? (_failure != null ? "worker-exit" : null), _failure?.Message, _log.Snapshot());

    private WorkerConnection(Process process, PluginWorkerIdentity identity, PluginWorkerOptions options,
        IPluginHostServices services, IDisposable processLease, string workingDirectory, Func<long>? temporaryBytes)
    {
        _workingDirectory = workingDirectory; _temporaryBytes = temporaryBytes;
        _processTree = new OwnedProcessTree(process);
        _process = process; _processId = process.Id; Identity = identity; _options = options; _services = services; _processLease = processLease;
        _codec = new(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        _log = new(options.MaximumLogBytes);
        _logs = ReadLogsAsync();
        _reader = ReadLoopAsync();
        _temporaryMonitor = MonitorTemporaryAsync();
    }

    public static async Task<WorkerConnection> StartAsync(PluginWorkerLaunch launch, PluginArtifact artifact,
        PluginConfiguration configuration, IPluginHostServices services, PluginWorkerOptions options,
        IDisposable processLease, CancellationToken token, bool describeOnly = false, Func<long>? temporaryBytes = null)
    {
        Process? process = null;
        WorkerConnection? connection = null;
        string workingDirectory = Path.Combine(Path.GetFullPath(launch.WorkingDirectory), "worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            Dictionary<string, string> resources = await ArtifactVerifier.VerifyAsync(artifact, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(workingDirectory);
            ProcessStartInfo start = new(Path.GetFullPath(launch.ExecutablePath))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };
            foreach (string argument in launch.Arguments) start.ArgumentList.Add(argument);
            start.Environment["DOTNET_GCHeapHardLimit"] = options.HeapLimitBytes.ToString("X");
            start.Environment["DOTNET_EnableDiagnostics"] = "0";
            process = Process.Start(start) ?? throw new PluginWorkerException("start", "Worker process could not be started.");
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException) { }
            connection = new WorkerConnection(process, new(Guid.NewGuid().ToString("N"), artifact.ExecutionRevision, configuration.Revision), options, services, processLease, workingDirectory, temporaryBytes);
            HelloResponse hello = await connection.InvokeAsync<HelloRequest, HelloResponse>("hello", new(ProtocolVersion.Operations), null, options.HandshakeTimeout, token).ConfigureAwait(false);
            connection._processTree.ConfirmGroup();
            if (hello.Operations == null || hello.Operations.Length > 32 || ProtocolVersion.Operations.Any(operation => !hello.Operations.Contains(operation)))
                throw new PluginWorkerException("protocol", "Worker does not implement the required operations.");
            PluginDescription description = await connection.InvokeAsync<InitializeRequest, PluginDescription>("describe",
                new(Path.GetFullPath(artifact.AssemblyPath), resources, configuration, !describeOnly), null, options.HandshakeTimeout, token).ConfigureAwait(false);
            SdkValidation.ThrowIfInvalid(SdkValidation.ValidateDescription(description));
            if (!describeOnly) SdkValidation.ThrowIfInvalid(SdkValidation.ValidateConfiguration(description, configuration));
            connection.Description = description;
            return connection;
        }
        catch
        {
            if (connection != null) await connection.DisposeAsync().ConfigureAwait(false);
            else
            {
                if (process != null)
                {
                    Kill(process);
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                    process.Dispose();
                }
                processLease.Dispose(); TryDeleteWorkspace(workingDirectory);
            }
            throw;
        }
    }

    public async Task<TResponse> InvokeAsync<TRequest, TResponse>(string operation, TRequest request,
        PluginOperationContext? context, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_failure != null) throw new PluginWorkerException("worker-exit", "Plugin worker is unavailable.", _failure);
        string id = Guid.NewGuid().ToString("N");
        WireEnvelope envelope = new(ProtocolVersion.Major, "request", operation, id, Identity.InstanceId,
            Identity.ExecutionRevision, Identity.ConfigurationRevision, context?.ResourceId, context?.Generation,
            context?.RuleRevision, context?.GrantRevision, null, WireJson.Element(request));
        using CancellationTokenSource deadline = new(timeout);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, _lifetime.Token);
        Pending pending = new(envelope, context, lifetime.Token, request);
        if (!_pending.TryAdd(id, pending)) throw new InvalidOperationException();
        try
        {
            await _codec.WriteAsync(envelope, lifetime.Token).ConfigureAwait(false);
            WireEnvelope response = await pending.Completion.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            if (response.Direction == "error")
            {
                WireFailure error = WireJson.Read<WireFailure>(response.Payload);
                if (string.IsNullOrWhiteSpace(error.Code) || error.Code.Length > 256 || error.Message == null || error.Message.Length > 2048)
                    throw new InvalidDataException("Malformed worker failure payload.");
                throw new PluginWorkerException(error.Code, error.Message);
            }
            return WireJson.Read<TResponse>(response.Payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || deadline.IsCancellationRequested)
        {
            // Keep the completion for the cancellation acknowledgment, but its lifetime already
            // forbids every host service and rejects the result for the original caller.
            try
            {
                using CancellationTokenSource grace = new(_options.CancellationGrace);
                await _codec.WriteAsync(envelope with { Direction = "cancel" }, grace.Token).ConfigureAwait(false);
                await pending.Completion.Task.WaitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (Exception) { Fail(new PluginWorkerException("unresponsive", "Worker did not cooperate with cancellation.")); _processTree.Kill(); }
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw new PluginWorkerException("timeout", "Plugin operation exceeded its deadline.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw _failure as PluginWorkerException ?? new PluginWorkerException("stopped", "Worker call was invalidated.", _failure);
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            Fail(new PluginWorkerException("protocol", exception.Message, exception)); _processTree.Kill();
            throw new PluginWorkerException("protocol", "Invalid or closed worker protocol.", exception);
        }
        finally
        {
            _pending.TryRemove(id, out _);
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _codec.ReadAsync(_lifetime.Token).ConfigureAwait(false) is WireEnvelope envelope)
            {
                if (envelope.WorkerInstance != Identity.InstanceId || envelope.BuildRevision != Identity.ExecutionRevision
                    || envelope.ConfigurationRevision != Identity.ConfigurationRevision)
                    throw new InvalidDataException("Stale worker or configuration revision.");
                if (envelope.Direction == "service")
                {
                    if (envelope.ParentRequestId == null || !_pending.TryGetValue(envelope.ParentRequestId, out Pending? parent)
                        || parent.Token.IsCancellationRequested) continue;
                    CheckCorrelation(envelope, parent.Envelope, true);
                    if (_serviceTasks.Count >= 8 || _serviceTasks.ContainsKey(envelope.RequestId))
                        throw new InvalidDataException("Host service request capacity or identity violation.");
                    Task task = HandleServiceAsync(envelope, parent);
                    _serviceTasks[envelope.RequestId] = task;
                    _ = task.ContinueWith(_ => _serviceTasks.TryRemove(envelope.RequestId, out Task? ignored), CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    continue;
                }
                if (envelope.Direction is not ("response" or "error")) throw new InvalidDataException("Invalid worker response direction.");
                if (!_pending.TryGetValue(envelope.RequestId, out Pending? pending)) continue;
                CheckCorrelation(envelope, pending.Envelope, false);
                pending.Completion.TrySetResult(envelope);
            }
            if (!_lifetime.IsCancellationRequested) throw new EndOfStreamException("Worker closed its private protocol channel.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            string code = exception is FrameLimitException ? "frame-too-large" : exception is EndOfStreamException ? "worker-exit" : "protocol";
            if (exception is EndOfStreamException)
            {
                try
                {
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    await _logs.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    if (_process.ExitCode == 72 && _log.Snapshot().Any(line => line.Contains("MEOWCHAN_WORKER_MEMORY_LIMIT", StringComparison.Ordinal)))
                        code = "memory-limit";
                }
                catch (Exception exitFailure) when (exitFailure is TimeoutException or InvalidOperationException) { }
            }
            Fail(new PluginWorkerException(code, code == "memory-limit" ? "Worker reached its configured managed heap limit." : exception.Message, exception));
            _processTree.Kill();
        }
    }

    private static void CheckCorrelation(WireEnvelope response, WireEnvelope request, bool service)
    {
        if ((!service && (response.Operation != request.Operation || response.ParentRequestId != null))
            || response.ResourceId != request.ResourceId || response.Generation != request.Generation
            || response.RuleRevision != request.RuleRevision || response.GrantRevision != request.GrantRevision)
            throw new InvalidDataException("Worker response does not match the live request context.");
    }

    private async Task HandleServiceAsync(WireEnvelope request, Pending parent)
    {
        try
        {
            parent.Token.ThrowIfCancellationRequested();
            if (parent.Context == null) throw new InvalidDataException("Host services are unavailable during Describe and settings validation.");
            switch (request.Operation)
            {
                case "http":
                    HostHttpRequest http = WireJson.Read<HostHttpRequest>(request.Payload);
                    SourceDescription source = Description.Sources.SingleOrDefault(item => item.SourceId == http.SourceId)
                        ?? throw new InvalidDataException("The worker requested HTTP for an undeclared source.");
                    SdkValidation.ThrowIfInvalid(SdkValidation.ValidateHttpRequest(http, source));
                    HostHttpResult result = await _services.SendHttpAsync(http, parent.Context, parent.Token).ConfigureAwait(false);
                    SdkValidation.ThrowIfInvalid(SdkValidation.ValidateHttpResult(http, result, source));
                    await ServiceReplyAsync<HostHttpResult>(request, result, parent.Token).ConfigureAwait(false);
                    break;
                case "image":
                    if (parent.Request is not ImageFilterRequest filter) throw new InvalidDataException("Image access requires a filter request.");
                    ImageReadRequest image = WireJson.Read<ImageReadRequest>(request.Payload);
                    if (image.AssetToken != filter.AssetToken) throw new InvalidDataException("Image token does not belong to this filter request.");
                    SdkValidation.ThrowIfInvalid(SdkValidation.ValidateImageRead(image.AssetToken, image.Offset, image.Count, filter.OriginalSize));
                    ImageAssetChunk chunk = await _services.ReadImageAsync(image.AssetToken, image.Offset, image.Count, parent.Context, parent.Token).ConfigureAwait(false);
                    if (chunk.Offset != image.Offset || chunk.Bytes.Length > image.Count || chunk.Offset + chunk.Bytes.Length > filter.OriginalSize
                        || chunk.IsEnd != (chunk.Offset + chunk.Bytes.Length == filter.OriginalSize))
                        throw new InvalidDataException("Invalid image service chunk.");
                    await ServiceReplyAsync(request, chunk, parent.Token).ConfigureAwait(false);
                    break;
                default: throw new InvalidDataException("Unknown mandatory host service.");
            }
        }
        catch (OperationCanceledException) when (parent.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            string message = exception.Message[..Math.Min(exception.Message.Length, 2048)];
            try { await _codec.WriteAsync(request with { Direction = "error", Payload = WireJson.Element(new WireFailure("host-service", message)) }, _lifetime.Token).ConfigureAwait(false); }
            catch (Exception) when (_lifetime.IsCancellationRequested) { }
        }
    }
    private Task ServiceReplyAsync<T>(WireEnvelope request, T response, CancellationToken token) =>
        _codec.WriteAsync(request with { Direction = "serviceResult", Payload = WireJson.Element(response) }, token);

    private async Task ReadLogsAsync()
    {
        char[] buffer = new char[2048];
        try
        {
            int count;
            while ((count = await _process.StandardError.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false)) > 0)
                _log.Add(new string(buffer, 0, count));
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) { }
    }
    public long GetTemporaryBytes() => Interlocked.Read(ref _observedTemporaryBytes);
    private long ScanTemporaryBytes()
    {
        try
        {
            long total = 0; int entries = 0;
            Stack<DirectoryInfo> directories = new();
            directories.Push(new DirectoryInfo(_workingDirectory));
            EnumerationOptions enumeration = new() { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            while (directories.TryPop(out DirectoryInfo? directory))
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                IEnumerable<FileSystemInfo> children;
                try { children = directory.EnumerateFileSystemInfos("*", enumeration); }
                catch (DirectoryNotFoundException) { continue; }
                foreach (FileSystemInfo child in children)
                {
                    if (++entries > 10000) return _options.MaximumTemporaryBytes + 1;
                    try
                    {
                        if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (child is DirectoryInfo folder) directories.Push(folder);
                        else if (child is FileInfo file) total = checked(total + file.Length);
                    }
                    catch (IOException) { } // Files can legitimately disappear between enumeration and stat.
                }
            }
            return total;
        }
        catch (DirectoryNotFoundException) { return 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return _options.MaximumTemporaryBytes + 1; }
    }
    private async Task MonitorTemporaryAsync()
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                long ownBytes = ScanTemporaryBytes();
                Interlocked.Exchange(ref _observedTemporaryBytes, ownBytes);
                long totalBytes = _temporaryBytes?.Invoke() ?? ownBytes;
                if (ownBytes > _options.MaximumTemporaryBytes || totalBytes > _options.MaximumTemporaryBytes)
                {
                    Fail(new PluginWorkerException("temporary-limit", "The shared plugin temporary workspace budget was exceeded."));
                    _processTree.Kill(); return;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Fail(new PluginWorkerException("temporary-budget", "The worker temporary workspace budget could not be monitored.", exception));
            _processTree.Kill();
        }
    }
    private static void TryDeleteWorkspace(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private void Fail(Exception exception)
    {
        Interlocked.CompareExchange(ref _failure, exception, null);
        foreach (Pending pending in _pending.Values) pending.Completion.TrySetException(_failure!);
        // A failed process invalidates every counter-request immediately, even if the
        // caller has not disposed/retried the client yet.
        _failureCancellation = _lifetime.CancelAsync();
    }
    private static void Kill(Process process)
    {
        try { ProcessGroup.KillWorkerGroup(process.Id); if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        try
        {
            Fail(new PluginWorkerException("stopped", "Plugin worker was stopped."));
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _failureCancellation.ConfigureAwait(false);
            _processTree.Kill();
            using CancellationTokenSource exitDeadline = new(TimeSpan.FromSeconds(5));
            try { await _processTree.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false); } catch (OperationCanceledException) when (exitDeadline.IsCancellationRequested) { }
            try { await Task.WhenAll(new[] { _reader, _logs, _temporaryMonitor }.Concat(_serviceTasks.Values)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
        }
        finally
        {
            // In particular, a failed native job query must still release handles and
            // admission before propagating its diagnostic to the owner.
            _processTree.Dispose(); _process.Dispose(); _codec.Dispose(); _lifetime.Dispose(); _processLease.Dispose(); TryDeleteWorkspace(_workingDirectory);
            if (!Directory.Exists(_workingDirectory)) Interlocked.Exchange(ref _observedTemporaryBytes, 0);
        }
    }
    private sealed record Pending(WireEnvelope Envelope, PluginOperationContext? Context, CancellationToken Token, object? Request)
    {
        public TaskCompletionSource<WireEnvelope> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
