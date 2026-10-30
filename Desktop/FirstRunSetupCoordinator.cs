using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal sealed record FirstRunSetupValues(string DownloadPath, long BandwidthBytesPerSecond, int ParallelDownloads);
internal sealed record FirstRunSetupCommitResult(bool Persisted, string? ActivationWarning = null);
internal enum FirstRunSetupOutcome { NotOffered, Deferred, Completed, PreparationFailed }
internal sealed record FirstRunSetupRunResult(FirstRunSetupOutcome Outcome, string? Message = null);

/// <summary>Small test seam, not a second persistence implementation. The host owns atomic completion.</summary>
internal interface IFirstRunSetupSession
{
    DesktopSessionState State { get; }
    Config Snapshot { get; }
    Task PersistPendingAsync();
    Task<FirstRunSetupCommitResult> CompleteAsync(FirstRunSetupValues values);
}

/// <summary>
/// Called once by the shell AFTER successful permitted Ready. No startup/engine work,
/// layout-derived consent, recovery defaults, timers, polling or separate marker files.
/// </summary>
internal static class FirstRunSetupCoordinator
{
    internal static readonly TimeSpan PendingWaitBudget = TimeSpan.FromSeconds(5);
    private sealed class RunOnce { internal Task<FirstRunSetupRunResult>? Task; }
    private static readonly ConditionalWeakTable<DesktopSessionHost, RunOnce> Runs = new();

    internal static Task<FirstRunSetupRunResult> RunAsync(Window owner, DesktopSessionHost host)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(host);
        Dispatcher.UIThread.VerifyAccess();
        RunOnce once = Runs.GetValue(host, _ => new RunOnce());
        // UI-thread-only entry; retain one run, including Later/failure, for this host lifetime.
        return once.Task ??= RunCoreAsync(new HostSession(host), async (config, accept, cancellation) =>
        {
            if (!owner.IsVisible || host.State != DesktopSessionState.Ready)
                return new(FirstRunSetupOutcome.Deferred);
            var dialog = new FirstRunSetupWindow(config, accept);
            return await dialog.ShowAsync(owner);
        });
    }

    internal static bool CanOffer(Config config, DesktopSessionState state) =>
        state == DesktopSessionState.Ready && config.AllowsOrdinaryWrites && config.FirstRunSetupVersion == 0 &&
        (config.Origin == ProfileOrigin.ConfirmedNew ||
            config.Origin == ProfileOrigin.ExistingValid && config.FirstRunSetupPending);

    internal static async Task<FirstRunSetupRunResult> RunCoreAsync(IFirstRunSetupSession session,
        Func<Config, Func<FirstRunSetupValues, Task<FirstRunSetupCommitResult>>, CancellationToken,
            Task<FirstRunSetupRunResult>> showDialog,
        CancellationToken cancellation = default, TimeSpan? pendingBudget = null)
    {
        Config initial = session.Snapshot;
        if (!CanOffer(initial, session.State)) return new(FirstRunSetupOutcome.NotOffered);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (!initial.FirstRunSetupPending)
            {
                // Only positive ConfirmedNew provenance authorizes this write. Once it
                // succeeds the snapshot may have ExistingValid origin; Pending carries intent.
                if (initial.Origin != ProfileOrigin.ConfirmedNew)
                    return new(FirstRunSetupOutcome.NotOffered);
                await session.PersistPendingAsync().WaitAsync(pendingBudget ?? PendingWaitBudget, cancellation);
            }
            Config pending = session.Snapshot;
            if (!CanOffer(pending, session.State) || !pending.FirstRunSetupPending)
                return new(FirstRunSetupOutcome.NotOffered);
            cancellation.ThrowIfCancellationRequested();
            return await showDialog(pending.Clone(), async values =>
            {
                Config current = session.Snapshot;
                if (!CanOffer(current, session.State) || !current.FirstRunSetupPending)
                    throw new InvalidOperationException(Texts.T("setup.noLongerReady"));
                ValidateValues(values, current.DownloadPathBaseDirectory);
                // No WaitAsync/timeout around an admitted completion: that would make
                // durable outcome uncertain and invite a duplicate acceptance.
                return await session.CompleteAsync(values);
            }, cancellation);
        }
        catch (OperationCanceledException) { return new(FirstRunSetupOutcome.Deferred); }
        catch (TimeoutException) { return new(FirstRunSetupOutcome.PreparationFailed, Texts.T("setup.pendingTimeout")); }
        catch (Exception) { return new(FirstRunSetupOutcome.PreparationFailed, Texts.T("setup.prepareFailed")); }
    }

    internal static void ValidateValues(FirstRunSetupValues values, string baseDirectory)
    {
        DownloadDestinationResult syntax = DownloadDestinationValidator.Normalize(values.DownloadPath, baseDirectory);
        if (syntax.NormalizedPath == null || !Path.IsPathFullyQualified(values.DownloadPath) ||
            !string.Equals(syntax.NormalizedPath, values.DownloadPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("An absolute normalized download path is required.", nameof(values));
        if (values.BandwidthBytesPerSecond <= 0 || values.ParallelDownloads <= 0)
            throw new ArgumentOutOfRangeException(nameof(values), "Positive download limits are required.");
    }

    internal static bool TryParseLimits(string? bandwidthMiB, string? parallel, IFormatProvider culture,
        out long bytesPerSecond, out int count)
    {
        bytesPerSecond = 0; count = 0;
        if (!decimal.TryParse(bandwidthMiB, NumberStyles.Number, culture, out decimal rate) || rate <= 0 ||
            !decimal.TryParse(parallel, NumberStyles.Number, culture, out decimal jobs) || jobs < 1 ||
            jobs > int.MaxValue || decimal.Truncate(jobs) != jobs) return false;
        try
        {
            decimal bytes = rate * 1024m * 1024m;
            if (bytes < 1 || bytes > long.MaxValue) return false;
            bytesPerSecond = checked((long)bytes);
            count = decimal.ToInt32(jobs);
            return true;
        }
        catch (OverflowException) { return false; }
    }

    private sealed class HostSession(DesktopSessionHost host) : IFirstRunSetupSession
    {
        public DesktopSessionState State => host.State;
        public Config Snapshot => host.Config.Clone();
        public Task PersistPendingAsync()
        {
            if (host.State != DesktopSessionState.Ready)
                throw new InvalidOperationException(Texts.T("setup.noLongerReady"));
            return host.SaveViewPreferencesAsync(config =>
            {
                if (host.State != DesktopSessionState.Ready || config.FirstRunSetupVersion != 0 || !config.AllowsOrdinaryWrites)
                    throw new InvalidOperationException(Texts.T("setup.noLongerReady"));
                // A winning Ready/layout batch may already have durably marked the
                // same new profile and advanced its origin; this is an idempotent no-op.
                if (config.FirstRunSetupPending && config.Origin is ProfileOrigin.ConfirmedNew or ProfileOrigin.ExistingValid) return;
                if (config.Origin != ProfileOrigin.ConfirmedNew)
                    throw new InvalidOperationException(Texts.T("setup.noLongerReady"));
                config.FirstRunSetupPending = true;
            });
        }
        // R0-owned hook: narrow atomic config+runtime publication, no Update/plugin
        // reconfiguration. Contract in S4/R0-REQUESTS.md; do not substitute full Settings.
        public Task<FirstRunSetupCommitResult> CompleteAsync(FirstRunSetupValues values)
            => host.CompleteFirstRunSetupAsync(values);
    }
}
