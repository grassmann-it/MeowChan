using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace MeowChan.Desktop.Updates;

internal sealed record UpdateView(bool IsChecking = false, DateTimeOffset? LastCheckedUtc = null,
    UpdateInfo? Update = null, UpdateCheckResult? ManualResult = null, bool EnvironmentDisabled = false);

/// <summary>Owns the existing checker and its transport for one leased desktop profile.</summary>
internal sealed class UpdateSession : IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly UpdateService _service;
    private readonly SemanticVersion _current;
    private readonly TimeProvider _time;
    private readonly Func<string?> _disable;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private UpdateView _view = new();
    private bool _prereleases;
    private Task? _background;
    private Task? _stop;
    private int _manualPending;

    internal UpdateSession(string profileDirectory, Config config, HttpClient? client = null,
        TimeProvider? timeProvider = null, Func<string?>? disableEnvironment = null,
        SemanticVersion? currentVersion = null)
    {
        _client = client ?? UpdateService.CreateHttpClient();
        _time = timeProvider ?? TimeProvider.System;
        _disable = disableEnvironment ?? (() => Environment.GetEnvironmentVariable("MEOWCHAN_DISABLE_UPDATE_CHECK"));
        string version = typeof(UpdateSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(UpdateSession).Assembly.GetName().Version!.ToString(3);
        _current = currentVersion ?? SemanticVersion.Parse(version);
        _prereleases = config.IncludePrereleaseUpdates;
        _service = new UpdateService(_client, new UpdateStateStore(profileDirectory), new UpdateServiceOptions
        {
            CurrentVersion = _current, AutomaticChecksEnabled = config.AutomaticUpdateChecks,
            IncludePrereleases = _prereleases
        }, _time, _disable);
        _view = new(EnvironmentDisabled: _disable() == "1");
    }

    internal event Action? Changed;
    internal UpdateView View { get { lock (_gate) return _view; } }

    internal void Start()
    {
        lock (_gate)
            if (_background == null && _stop == null) _background = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(30), _time, _lifetime.Token).ConfigureAwait(false);
            while (true)
            {
                await CheckAsync(UpdateCheckKind.Automatic).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromHours(24), _time, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Log.Information(error, "Background update checking stopped."); }
    }

    internal async Task ConfigureAsync(Config config)
    {
        await _operations.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await _service.ConfigureAsync(config.AutomaticUpdateChecks, config.IncludePrereleaseUpdates, _lifetime.Token).ConfigureAwait(false);
            _prereleases = config.IncludePrereleaseUpdates;
            await RefreshCoreAsync(clearManual: true).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    internal async Task CheckAsync(UpdateCheckKind kind = UpdateCheckKind.Manual)
    {
        bool manual = kind == UpdateCheckKind.Manual;
        if (manual && Interlocked.CompareExchange(ref _manualPending, 1, 0) != 0) return;
        bool acquired = false;
        try
        {
            // Publish before waiting for an automatic request, so repeated clicks cannot queue more checks.
            if (manual) Publish(View with { IsChecking = true, ManualResult = null });
            await _operations.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            acquired = true;
            UpdateCheckResult result = await _service.CheckAsync(kind, _lifetime.Token).ConfigureAwait(false);
            await RefreshCoreAsync().ConfigureAwait(false);
            UpdateView view = View;
            // A manual check deliberately reveals even a previously skipped release.
            if (manual)
                Publish(view with { ManualResult = result,
                    Update = result.Status == UpdateCheckStatus.Available ? result.Update : view.Update });
        }
        finally
        {
            if (manual)
            {
                Publish(View with { IsChecking = false });
                Interlocked.Exchange(ref _manualPending, 0);
            }
            if (acquired) _operations.Release();
        }
    }

    internal async Task SkipAsync()
    {
        await _operations.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (View.Update is not { } update) return;
            await _service.SkipVersionAsync(update.Version, _lifetime.Token).ConfigureAwait(false);
            await RefreshCoreAsync(clearManual: true).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private async Task RefreshAsync()
    {
        await _operations.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try { await RefreshCoreAsync().ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    private async Task RefreshCoreAsync(bool clearManual = false)
    {
        try
        {
            UpdateState state = await _service.GetStateAsync(_lifetime.Token).ConfigureAwait(false);
            bool disabled = _disable() == "1";
            UpdateInfo? update = state.FoundUpdate is { } found && !disabled && found.Version.CompareTo(_current) > 0
                && !found.Version.Equals(state.SkippedVersion) && (_prereleases || !found.Version.IsPrerelease) ? found : null;
            Publish(View with { LastCheckedUtc = state.LastCheckedUtc, Update = update,
                ManualResult = clearManual ? null : View.ManualResult, EnvironmentDisabled = disabled });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Information(error, "Could not read profile update state.");
        }
    }

    private void Publish(UpdateView view)
    {
        Action? changed;
        lock (_gate) { _view = view; changed = Changed; }
        if (changed == null) return;
        foreach (Action observer in changed.GetInvocationList())
            try { observer(); } catch (Exception error) { Log.Warning(error, "Update observer failed."); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_stop ??= StopCoreAsync());
    }

    private async Task StopCoreAsync()
    {
        _lifetime.Cancel();
        if (_background != null) await _background.ConfigureAwait(false);
        await _operations.WaitAsync().ConfigureAwait(false);
        try { _client.Dispose(); lock (_gate) Changed = null; }
        finally { _operations.Release(); }
    }
}
