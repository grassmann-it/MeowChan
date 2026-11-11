using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;
using Xunit.Abstractions;

namespace MeowChan.Tests.Transfer;

// Real process death (SIGKILL of the whole test-host tree) at each import checkpoint, not a
// thrown exception. Opt-in with MEOWCHAN_CRASH_RUN=1; the child half runs only when spawned.
public sealed class NativeV2ImportCrashTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    [CrashRunTheory]
    [InlineData(nameof(NativeMediaImportPhase.PreparingSaved))]
    [InlineData(nameof(NativeMediaImportPhase.OperationsParentCreated))]
    [InlineData(nameof(NativeMediaImportPhase.CandidateDirectoryCreated))]
    [InlineData(nameof(NativeMediaImportPhase.CandidateReady))]
    [InlineData(nameof(NativeMediaImportPhase.PreparedSaved))]
    [InlineData(nameof(NativeMediaImportPhase.CandidateVerified))]
    [InlineData(nameof(NativeMediaImportPhase.BeforeSqlCommit))]
    [InlineData(nameof(NativeMediaImportPhase.SqlCommitted))]
    public async Task KillingTheProcessAtAnImportCheckpointLeavesAnOpenableCatalogAndAnHonestGuard(string phase)
    {
        string marker = Path.Combine(Path.GetTempPath(), "meowchan-crash-" + Guid.NewGuid().ToString("N"));
        ProcessStartInfo start = new("dotnet")
        {
            ArgumentList = { "test", typeof(NativeV2ImportCrashTests).Assembly.Location, "--filter",
                "FullyQualifiedName~" + nameof(NativeV2ImportCrashTests) + "." + nameof(ChildCommitsUntilTheCheckpointAndWaits) },
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.Environment["MEOWCHAN_CRASH_CHILD"] = phase;
        start.Environment["MEOWCHAN_CRASH_MARKER"] = marker;
        start.Environment.Remove("MEOWCHAN_CRASH_RUN");
        using Process child = Process.Start(start)!;
        _ = child.StandardOutput.ReadToEndAsync();
        _ = child.StandardError.ReadToEndAsync();
        string? root = null;
        try
        {
            using CancellationTokenSource timeout = new(Deadline);
            while (!File.Exists(marker))
            {
                Assert.False(child.HasExited, "The child ended before reaching " + phase);
                await Task.Delay(50, timeout.Token);
            }
            string[] parts = File.ReadAllText(marker).Trim().Split('\n');
            root = parts[1];
            // SIGKILL exactly the test host that holds the checkpoint, then the launcher tree.
            using Process host = Process.GetProcessById(int.Parse(parts[0]));
            host.Kill();
            await host.WaitForExitAsync().WaitAsync(Deadline);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(Deadline);
        }
        try
        {
            string profile = Path.Combine(root, "profile");
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            bool published = NativeV2ImportFixture.PayloadFiles(profile).ContainsValue(NativeV2ImportFixture.Hash(NativeV2ImportFixture.Original));
            string guard;
            try { await catalog.EnsureNativeMediaRecoveryClearAsync(); guard = "clear"; }
            catch (InvalidDataException error) { guard = "blocked: " + error.Message; }
            output.WriteLine($"phase={phase} guard={guard} payloadOnDisk={published}");
            // A committed SQL transaction survives the kill and lets startup continue.
            if (phase == nameof(NativeMediaImportPhase.SqlCommitted)) { Assert.Equal("clear", guard); Assert.True(published); }
            // Any earlier kill leaves a durable Preparing/Prepared row that keeps blocking startup:
            // the open recovery gap (no resume or discard path exists yet).
            else Assert.StartsWith("blocked", guard);
        }
        finally
        {
            File.Delete(marker);
            if (root != null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [CrashChildFact]
    public async Task ChildCommitsUntilTheCheckpointAndWaits()
    {
        string target = Environment.GetEnvironmentVariable("MEOWCHAN_CRASH_CHILD")!;
        string marker = Environment.GetEnvironmentVariable("MEOWCHAN_CRASH_MARKER")!;
        // Never disposed: the parent kills this process while the checkpoint holds.
        NativeV2ImportFixture f = await NativeV2ImportFixture.CreateAsync();
        ProfileAdmissionToken admission = await f.ExclusiveAsync();
        await f.CommitAsync(admission, checkpoint: phase =>
        {
            if (phase.ToString() != target) return;
            File.WriteAllText(marker + ".tmp", Environment.ProcessId + "\n" + f.Files.Root);
            File.Move(marker + ".tmp", marker);
            Thread.Sleep(Timeout.Infinite);
        });
        throw new InvalidOperationException("The checkpoint " + target + " was not reached.");
    }
}

public sealed class CrashRunTheoryAttribute : TheoryAttribute
{
    public CrashRunTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEOWCHAN_CRASH_RUN") != "1")
            Skip = "Real SIGKILL runs spawn test hosts; opt in with MEOWCHAN_CRASH_RUN=1.";
    }
}

public sealed class CrashChildFactAttribute : FactAttribute
{
    public CrashChildFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEOWCHAN_CRASH_CHILD")))
            Skip = "Child half of the SIGKILL runs; started only by the parent test.";
    }
}
