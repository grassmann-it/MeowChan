using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using MeowChan.PluginProtocol;
using MeowChan.PluginSdk;

namespace MeowChan.PluginWorker;

internal sealed class WorkerServer
{
    private readonly FrameCodec _codec;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WireEnvelope>> _services = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _serviceSlots = new(8, 8);
    private WireEnvelope? _identity;
    private WireEnvelope? _active;
    private CancellationTokenSource? _activeCancellation;
    private Task? _activeTask;
    private string? _completedRequestId;
    private IPlugin? _plugin;
    private PluginDescription? _description;
    private PluginConfiguration? _configuration;
    private Dictionary<string, string> _resources = new();

    public WorkerServer(Stream input, Stream output) => _codec = new FrameCodec(input, output);
    public async Task<int> RunAsync()
    {
        try
        {
            while (await _codec.ReadAsync(_shutdown.Token).ConfigureAwait(false) is WireEnvelope envelope)
            {
                if (_identity == null)
                {
                    if (envelope.Operation != "hello" || envelope.Direction != "request")
                        throw new InvalidDataException("Handshake required.");
                    HelloRequest hello = WireJson.Read<HelloRequest>(envelope.Payload);
                    if (hello.RequiredOperations.Any(operation => !ProtocolVersion.Operations.Contains(operation)))
                        throw new InvalidDataException("Unsupported mandatory operation.");
                    _identity = envelope;
                    await ReplyAsync(envelope, new HelloResponse(ProtocolVersion.Operations)).ConfigureAwait(false);
                    continue;
                }
                CheckIdentity(envelope);
                if (envelope.Direction is "serviceResult" or "error")
                {
                    if (_services.TryRemove(envelope.RequestId, out TaskCompletionSource<WireEnvelope>? response))
                        response.TrySetResult(envelope);
                    continue;
                }
                if (envelope.Direction == "cancel")
                {
                    if (_active?.RequestId == envelope.RequestId && envelope.RequestId != Volatile.Read(ref _completedRequestId))
                        _activeCancellation?.Cancel();
                    continue;
                }
                if (envelope.Direction != "request") throw new InvalidDataException("Unexpected frame direction.");
                if (_active != null && _active.RequestId != Volatile.Read(ref _completedRequestId))
                    throw new InvalidDataException("Concurrent plugin calls are forbidden.");
                _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _active = envelope;
                // Do not let synchronous plugin entrypoints block the private pipe reader/cancellation.
                CancellationTokenSource callCancellation = _activeCancellation;
                _activeTask = Task.Run(() => ExecuteAsync(envelope, callCancellation), CancellationToken.None);
            }
            return 0;
        }
        finally
        {
            _shutdown.Cancel();
            foreach (TaskCompletionSource<WireEnvelope> response in _services.Values)
                response.TrySetException(new EndOfStreamException("Host disconnected."));
            // A malicious/noncooperative plugin is terminated by its owning host after grace.
            if (_activeTask != null) await Task.WhenAny(_activeTask, Task.Delay(100)).ConfigureAwait(false);
        }
    }

    private void CheckIdentity(WireEnvelope envelope)
    {
        if (envelope.WorkerInstance != _identity!.WorkerInstance || envelope.BuildRevision != _identity.BuildRevision
            || envelope.ConfigurationRevision != _identity.ConfigurationRevision)
            throw new InvalidDataException("Stale worker or execution revision.");
    }

    private async Task ExecuteAsync(WireEnvelope envelope, CancellationTokenSource callCancellation)
    {
        CancellationToken token = callCancellation.Token;
        try
        {
            if (envelope.Operation == "describe")
            {
                if (_plugin != null) throw new InvalidDataException("A worker loads exactly one plugin revision.");
                InitializeRequest initialize = WireJson.Read<InitializeRequest>(envelope.Payload);
                if (initialize.Configuration.Revision != envelope.ConfigurationRevision)
                    throw new InvalidDataException("Configuration revision mismatch.");
                Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(initialize.AssemblyPath);
                Type[] entries = assembly.GetExportedTypes().Where(type => typeof(IPlugin).IsAssignableFrom(type)
                    && type is { IsAbstract: false, IsInterface: false } && type.GetConstructor(Type.EmptyTypes) != null).ToArray();
                if (entries.Length != 1) throw new InvalidDataException("Exactly one public plugin entrypoint is required.");
                _plugin = (IPlugin)Activator.CreateInstance(entries[0])!;
                _description = _plugin.Describe();
                SdkValidation.ThrowIfInvalid(SdkValidation.ValidateDescription(_description));
                if (initialize.ValidateConfiguration) SdkValidation.ThrowIfInvalid(SdkValidation.ValidateConfiguration(_description, initialize.Configuration));
                if ((_description.Sources.Count > 0 && _plugin is not ISourcePlugin)
                    || (_description.Filters.Count > 0 && _plugin is not IImageFilterPlugin))
                    throw new InvalidDataException("Description capabilities do not match implemented interfaces.");
                _configuration = initialize.Configuration;
                _resources = initialize.ResourcePaths;
                token.ThrowIfCancellationRequested();
                await ReplyAsync(envelope, _description).ConfigureAwait(false);
                return;
            }
            if (_plugin == null || _configuration == null) throw new InvalidDataException("Describe required.");
            CallServices services = new(this, envelope, token);
            PluginCallContext context = new(_configuration, services, services, new Resources(_resources), new Log());
            switch (envelope.Operation)
            {
                case "catalog":
                    CatalogReadRequest catalog = WireJson.Read<CatalogReadRequest>(envelope.Payload);
                    CatalogReadResult catalogResult = await ((ISourcePlugin)_plugin).ReadCatalogAsync(catalog, context, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    await ReplyAsync<CatalogReadResult>(envelope, catalogResult).ConfigureAwait(false);
                    break;
                case "thread":
                    ThreadReadRequest thread = WireJson.Read<ThreadReadRequest>(envelope.Payload);
                    ThreadReadResult threadResult = await ((ISourcePlugin)_plugin).ReadThreadAsync(thread, context, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    await ReplyAsync<ThreadReadResult>(envelope, threadResult).ConfigureAwait(false);
                    break;
                case "filter":
                    ImageFilterResult result = await ((IImageFilterPlugin)_plugin).EvaluateAsync(WireJson.Read<ImageFilterRequest>(envelope.Payload), context, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    await ReplyAsync(envelope, result).ConfigureAwait(false);
                    break;
                case "settings":
                    IReadOnlyList<FieldValidationError> errors = _plugin is IPluginSettingsValidator validator
                        ? await validator.ValidateSettingsAsync(WireJson.Read<SettingsValidationRequest>(envelope.Payload), context, token).ConfigureAwait(false)
                        : Array.Empty<FieldValidationError>();
                    token.ThrowIfCancellationRequested();
                    await ReplyAsync(envelope, errors).ConfigureAwait(false);
                    break;
                default: throw new InvalidDataException("Unsupported mandatory operation: " + envelope.Operation);
            }
        }
        catch (OutOfMemoryException)
        {
            Console.Error.WriteLine("MEOWCHAN_WORKER_MEMORY_LIMIT");
            Environment.Exit(72);
            throw;
        }
        catch (Exception exception)
        {
            string code = exception is OperationCanceledException ? "cancelled" : exception is FrameLimitException ? "frame-too-large" : exception is InvalidDataException ? "protocol" : "plugin-error";
            string message = exception.Message.Length > 2048 ? exception.Message[..2048] : exception.Message;
            Volatile.Write(ref _completedRequestId, envelope.RequestId);
            try { await _codec.WriteAsync(envelope with { Direction = "error", Payload = WireJson.Element(new WireFailure(code, message)) }, _shutdown.Token).ConfigureAwait(false); }
            catch (Exception) when (_shutdown.IsCancellationRequested) { }
        }
        finally
        {
            await callCancellation.CancelAsync().ConfigureAwait(false);
            callCancellation.Dispose();
        }
    }

    private Task ReplyAsync<T>(WireEnvelope request, T response)
    {
        WireEnvelope reply = request with { Direction = "response", Payload = WireJson.Element(response) };
        // Publishing completion precedes writing the response: the host may issue its next
        // serial request as soon as these bytes arrive, before our async task continuation runs.
        Volatile.Write(ref _completedRequestId, request.RequestId);
        return _codec.WriteAsync(reply, _shutdown.Token);
    }

    private async Task<TResponse> ServiceAsync<TRequest, TResponse>(WireEnvelope parent, string operation,
        TRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string requestId = Guid.NewGuid().ToString("N");
        TaskCompletionSource<WireEnvelope> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_serviceSlots.Wait(0)) throw new InvalidOperationException("Too many concurrent host-service calls.");
        _services[requestId] = completion;
        try
        {
            await _codec.WriteAsync(parent with { Direction = "service", Operation = operation,
                RequestId = requestId, ParentRequestId = parent.RequestId, Payload = WireJson.Element(request) }, token).ConfigureAwait(false);
            WireEnvelope response = await completion.Task.WaitAsync(token).ConfigureAwait(false);
            if (response.ParentRequestId != parent.RequestId || response.Operation != operation
                || response.ResourceId != parent.ResourceId || response.Generation != parent.Generation
                || response.RuleRevision != parent.RuleRevision || response.GrantRevision != parent.GrantRevision)
                throw new InvalidDataException("Uncorrelated host-service response.");
            if (response.Direction == "error") throw new InvalidOperationException(WireJson.Read<WireFailure>(response.Payload).Message);
            return WireJson.Read<TResponse>(response.Payload);
        }
        finally { _services.TryRemove(requestId, out _); _serviceSlots.Release(); }
    }

    private sealed class CallServices(WorkerServer server, WireEnvelope parent, CancellationToken lifetime) : IHostHttp, IImageAssetReader
    {
        public async Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource combined = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
            return await server.ServiceAsync<HostHttpRequest, HostHttpResult>(parent, "http", request, combined.Token).ConfigureAwait(false);
        }
        public async Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource combined = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
            return await server.ServiceAsync<ImageReadRequest, ImageAssetChunk>(parent, "image", new(assetToken, offset, count), combined.Token).ConfigureAwait(false);
        }
    }
    private sealed class Resources(Dictionary<string, string> paths) : IPluginResources
    {
        public string? ResolvePath(string logicalName) => paths.GetValueOrDefault(logicalName);
    }
    private sealed class Log : IPluginLog
    {
        public void Write(PluginLogLevel level, string message) => Console.Error.WriteLine($"{level}: {message[..Math.Min(message.Length, 2048)]}");
    }
}
