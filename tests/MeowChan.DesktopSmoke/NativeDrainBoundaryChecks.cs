using System.Diagnostics;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

// The shell refuses to release the profile while a native gallery cleanup has faulted, so the probe in
// PreferenceStartupChecks.RunRetainedNativeDrainBoundaryAsync deliberately leaves a process that never
// closes. This scenario is its parent: it starts one such child run of this same assembly (same Xvfb,
// own private profile), waits for the child's receipt, proves from outside that the child still holds the
// profile lease, terminates it and proves that the lease is free afterwards.
internal static class NativeDrainBoundaryChecks
{
    private const string ChildMarker = "MEOWCHAN_NATIVE_DRAIN_CHILD";
    private const string ReceiptPrefix = "SMOKE retained native boundary: ";
    private static readonly TimeSpan ChildDeadline = TimeSpan.FromSeconds(120);

    internal static Task RunAsync(MainWindow shell)
        => Environment.GetEnvironmentVariable(ChildMarker) == "1"
            ? PreferenceStartupChecks.RunRetainedNativeDrainBoundaryAsync(shell)
            : SuperviseChildAsync();

    private static async Task SuperviseChildAsync()
    {
        ProcessStartInfo info = new(Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown."))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        // Started through the dotnet host the assembly is an argument; an apphost needs none.
        if (Path.GetFileNameWithoutExtension(info.FileName) == "dotnet")
            info.ArgumentList.Add(typeof(NativeDrainBoundaryChecks).Assembly.Location);
        info.Environment[ChildMarker] = "1";
        info.Environment.Remove("MEOWCHAN_PROFILE");
        info.Environment.Remove("OCHAN_PROFILE");

        List<string> lines = [];
        TaskCompletionSource<string> receipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(object? _, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (lines) lines.Add(e.Data);
            if (e.Data.StartsWith(ReceiptPrefix, StringComparison.Ordinal)) receipt.TrySetResult(e.Data[ReceiptPrefix.Length..]);
        }

        using Process child = new() { StartInfo = info, EnableRaisingEvents = true };
        child.OutputDataReceived += OnLine;
        child.ErrorDataReceived += OnLine;
        child.Exited += (_, _) => receipt.TrySetException(new InvalidOperationException("The child run ended before its receipt."));
        child.Start();
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
        string? profile = null;
        try
        {
            JsonElement root;
            try { root = JsonDocument.Parse(await receipt.Task.WaitAsync(ChildDeadline)).RootElement; }
            catch (Exception error)
            {
                string output;
                lock (lines) output = string.Join(Environment.NewLine, lines.TakeLast(40));
                throw new InvalidOperationException("The retained native boundary child gave no receipt: " + error.Message
                    + Environment.NewLine + output, error);
            }
            profile = root.GetProperty("profile").GetString() ?? throw new InvalidOperationException("The receipt names no profile.");
            Require(root.GetProperty("status").GetString() == "observed-retained-requires-parent-exit-proof"
                && root.GetProperty("internalCleanupAttempted").GetBoolean() && root.GetProperty("updatesQuiescent").GetBoolean()
                && !root.GetProperty("profileReleased").GetBoolean() && !root.GetProperty("workspaceDetached").GetBoolean()
                && !root.GetProperty("allowClose").GetBoolean() && !root.GetProperty("decoderInstantiated").GetBoolean(),
                "The child's receipt does not describe a retained, undisturbed native boundary.");
            Require(!child.HasExited, "The child exited on its own although its native drain faulted.");

            bool held = false;
            try { using ProfileLease unexpected = ProfileLease.Acquire(profile); }
            catch (IOException) { held = true; }
            Require(held, "A second process could take the profile although the faulted native drain retained it.");

            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            using (ProfileLease released = ProfileLease.Acquire(profile)) { }
            Console.WriteLine("SMOKE native drain boundary: faulted native cleanup kept the profile lease against a second process, "
                + "the second Close was refused, and the lease was free again after the process ended.");
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            if (profile != null)
                try { Directory.Delete(profile, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
