using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace MeowChan.Desktop.Updates;

/// <summary>
/// UI-independent R10-15a checking. Use one service per leased profile and a dedicated client.
/// The caller owns the client; no downloads, browser launches, identifiers or telemetry are sent.
/// </summary>
public sealed class UpdateService
{
    private static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(24);
    private readonly HttpClient _client;
    private readonly UpdateStateStore _store;
    private UpdateServiceOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<string?> _disableEnvironment;
    private readonly Action<UpdateCheckError, Exception?> _diagnostic;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _checkGeneration;
    private UpdateCheckResult? _lastResult;

    public UpdateService(HttpClient client, UpdateStateStore store, UpdateServiceOptions options,
        TimeProvider? timeProvider = null, Func<string?>? disableEnvironment = null,
        Action<UpdateCheckError, Exception?>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.CurrentVersion);
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(options), "Request timeout must be positive and at most five minutes.");
        if (options.MaximumResponseBytes is < 1 or > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "Response limit must be between 1 byte and 4 MiB.");
        _client = client;
        _store = store;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _disableEnvironment = disableEnvironment ?? (() => Environment.GetEnvironmentVariable("MEOWCHAN_DISABLE_UPDATE_CHECK"));
        _diagnostic = diagnostic ?? ((error, exception) => Log.Information(exception, "Update check failed: {Reason}", error));
    }

    /// <summary>Creates the dedicated production transport: no redirects, cookies, credentials or default headers.</summary>
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<UpdateCheckResult> CheckAfterStartupAsync(CancellationToken cancellationToken = default)
    {
        UpdateCheckResult? disabled = Disabled(UpdateCheckKind.Automatic);
        if (disabled is not null) return disabled;
        await Task.Delay(TimeSpan.FromSeconds(30), _time, cancellationToken).ConfigureAwait(false);
        return await CheckAsync(UpdateCheckKind.Automatic, cancellationToken).ConfigureAwait(false);
    }

    internal async Task ConfigureAsync(bool automaticChecks, bool includePrereleases, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _options = _options with { AutomaticChecksEnabled = automaticChecks, IncludePrereleases = includePrereleases };
            _lastResult = null;
        }
        finally { _gate.Release(); }
    }

    public async Task<UpdateState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _store.Load(); }
        finally { _gate.Release(); }
    }

    public async Task SkipVersionAsync(SemanticVersion? version, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _store.Save(_store.Load() with { SkippedVersion = version }, cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<UpdateCheckResult> CheckAsync(UpdateCheckKind kind, CancellationToken cancellationToken = default)
    {
        if (kind is not (UpdateCheckKind.Automatic or UpdateCheckKind.Manual)) throw new ArgumentOutOfRangeException(nameof(kind));
        cancellationToken.ThrowIfCancellationRequested();
        UpdateCheckResult? disabled = Disabled(kind);
        if (disabled is not null) return disabled;
        long generation = Interlocked.Read(ref _checkGeneration);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            disabled = Disabled(kind);
            if (disabled is not null) return disabled;
            if (generation != _checkGeneration && _lastResult is not null)
            {
                if (_lastResult.Status is not (UpdateCheckStatus.Available or UpdateCheckStatus.Skipped)) return _lastResult;
                bool skipped = kind == UpdateCheckKind.Automatic && _lastResult.Update!.Version.Equals(_store.Load().SkippedVersion);
                return _lastResult with { Status = skipped ? UpdateCheckStatus.Skipped : UpdateCheckStatus.Available };
            }
            UpdateState state = _store.Load();
            DateTimeOffset now = _time.GetUtcNow();
            if (kind == UpdateCheckKind.Automatic && state.LastCheckedUtc is { } last && now - last < AutomaticInterval)
                return new UpdateCheckResult(UpdateCheckStatus.Throttled, EligibleUpdate(state));
            // Persist the attempt before HTTP, so failed/cancelled attempts also respect the daily limit after restart.
            state = state with { LastCheckedUtc = now };
            _store.Save(state, cancellationToken);
            UpdateCheckResult result = await FetchAsync(state, kind, cancellationToken).ConfigureAwait(false);
            if (result.Status is UpdateCheckStatus.UpToDate or UpdateCheckStatus.Available or UpdateCheckStatus.Skipped)
                _store.Save(state with { FoundUpdate = result.Update }, cancellationToken);
            _lastResult = result;
            Interlocked.Increment(ref _checkGeneration);
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _diagnostic(UpdateCheckError.Persistence, error);
            UpdateCheckResult failed = new(UpdateCheckStatus.Failed, Error: UpdateCheckError.Persistence);
            _lastResult = failed;
            Interlocked.Increment(ref _checkGeneration);
            return failed;
        }
        finally { _gate.Release(); }
    }

    private async Task<UpdateCheckResult> FetchAsync(UpdateState state, UpdateCheckKind kind, CancellationToken callerToken)
    {
        using CancellationTokenSource timeout = new(_options.RequestTimeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, timeout.Token);
        try
        {
            // An injected client must also be dedicated: reject ambient auth/custom headers rather than leak them.
            foreach (System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>> header in _client.DefaultRequestHeaders)
                if (!string.Equals(header.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Update transport must not carry default headers other than User-Agent.");
            Uri endpoint = new(_options.IncludePrereleases
                ? "https://api.github.com/repos/grassmann-it/MeowChan/releases?per_page=100"
                : "https://api.github.com/repos/grassmann-it/MeowChan/releases/latest");
            using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
            request.Headers.UserAgent.ParseAdd("MeowChan/" + _options.CurrentVersion);
            using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return Failure(UpdateCheckError.RateLimited);
            if (!response.IsSuccessStatusCode) return Failure(UpdateCheckError.Http);
            byte[] bytes = await ReadBoundedAsync(response.Content, linked.Token).ConfigureAwait(false);
            UpdateInfo? newest = UpdateReleaseParser.Parse(bytes, _options.IncludePrereleases, _options.RuntimeIdentifier);
            if (newest is null && !_options.IncludePrereleases) return Failure(UpdateCheckError.InvalidResponse);
            UpdateInfo? update = newest is not null && newest.Version.CompareTo(_options.CurrentVersion) > 0 ? newest : null;
            if (update is null) return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
            bool skipped = kind == UpdateCheckKind.Automatic && update.Version.Equals(state.SkippedVersion);
            return new UpdateCheckResult(skipped ? UpdateCheckStatus.Skipped : UpdateCheckStatus.Available, update);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { return Failure(UpdateCheckError.Timeout); }
        catch (HttpRequestException error) { return Failure(UpdateCheckError.Network, error); }
        catch (JsonException error) { return Failure(UpdateCheckError.InvalidResponse, error); }
        catch (InvalidDataException error) { return Failure(UpdateCheckError.InvalidResponse, error); }
        catch (IOException error) { return Failure(UpdateCheckError.Network, error); }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _options.MaximumResponseBytes) throw new InvalidDataException("Release response is too large.");
        using Stream source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream destination = new();
        byte[] buffer = new byte[Math.Min(16 * 1024, _options.MaximumResponseBytes + 1)];
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, _options.MaximumResponseBytes - (int)destination.Length + 1)), cancellationToken).ConfigureAwait(false);
            if (read == 0) return destination.ToArray();
            if (destination.Length + read > _options.MaximumResponseBytes) throw new InvalidDataException("Release response is too large.");
            destination.Write(buffer, 0, read);
        }
    }

    private UpdateInfo? EligibleUpdate(UpdateState state) => state.FoundUpdate is { } found
        && found.Version.CompareTo(_options.CurrentVersion) > 0 && !found.Version.Equals(state.SkippedVersion)
        && (_options.IncludePrereleases || !found.Version.IsPrerelease) ? found : null;

    private UpdateCheckResult? Disabled(UpdateCheckKind kind) => _disableEnvironment() == "1"
        ? new UpdateCheckResult(UpdateCheckStatus.Disabled, DisabledReason: UpdateDisabledReason.Environment)
        : kind == UpdateCheckKind.Automatic && !_options.AutomaticChecksEnabled
            ? new UpdateCheckResult(UpdateCheckStatus.Disabled, DisabledReason: UpdateDisabledReason.AutomaticChecksOff) : null;

    private UpdateCheckResult Failure(UpdateCheckError error, Exception? exception = null)
    {
        _diagnostic(error, exception);
        return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: error);
    }
}
