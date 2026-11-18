// Test doubles only for the extracted production orchestration, not native coverage.
internal static class Contract
{
    private static readonly MainWindow Owner = new();
    internal static async Task Main()
    {
        Environment.SetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB", "1");
        Environment.SetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED", "1");
        Environment.SetEnvironmentVariable("MEOWCHAN_GALLERY_CLEANUP_TRACE", null);
        string fixture = Path.GetTempFileName();
        Environment.SetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE", fixture);
        try
        {
            await Schedule(GalleryErrorInteractionChecks.RunAsync,
                "retirement,clip,asset,image,layout,notice-False-False,notice-False-True,notice-True-False,notice-True-True,explicit,image-notice,missing,reader-False,reader-True");
            await Schedule(GalleryErrorInteractionChecks.RunRetirementClipAsync, "retirement,clip");
            await Schedule(GalleryErrorInteractionChecks.RunLayoutRootsAsync, "layout");
            await Schedule(GalleryErrorInteractionChecks.RunNotificationsAsync,
                "asset,image,notice-False-False,notice-False-True,notice-True-False,notice-True-True,image-notice");
            await Schedule(GalleryErrorInteractionChecks.RunFailureReaderReturnAsync,
                "asset,explicit,missing,reader-False,reader-True");

            // A bad decoder fixture must abort, not produce a shortened PASS.
            File.Delete(fixture);
            await Fails<FileNotFoundException>(GalleryErrorInteractionChecks.RunNotificationsAsync, "");
            await Fails<FileNotFoundException>(GalleryErrorInteractionChecks.RunFailureReaderReturnAsync, "");
            await Fails<FileNotFoundException>(GalleryErrorInteractionChecks.RunAsync, "retirement,clip");
            // These groups have no decoder work and must not depend on a prior
            // group's asset preparation (the configured fixture gate still applies).
            await Schedule(GalleryErrorInteractionChecks.RunRetirementClipAsync, "retirement,clip");
            await Schedule(GalleryErrorInteractionChecks.RunLayoutRootsAsync, "layout");
            File.WriteAllText(fixture, "fixture");
            GalleryPlaybackFeatureChecks.FailAt = "explicit";
            await Fails<InvalidOperationException>(GalleryErrorInteractionChecks.RunFailureReaderReturnAsync, "asset,explicit");
            GalleryPlaybackFeatureChecks.FailAt = "clip";
            await Fails<InvalidOperationException>(GalleryErrorInteractionChecks.RunAsync, "retirement,clip");
            GalleryPlaybackFeatureChecks.FailAt = "notice-False-True";
            await Fails<InvalidOperationException>(GalleryErrorInteractionChecks.RunNotificationsAsync,
                "asset,image,notice-False-False,notice-False-True");
            GalleryPlaybackFeatureChecks.FailAt = "reader-False";
            await Fails<InvalidOperationException>(GalleryErrorInteractionChecks.RunFailureReaderReturnAsync,
                "asset,explicit,missing,reader-False");
            GalleryPlaybackFeatureChecks.FailAt = null;
            foreach (var entry in Entries)
            {
                Environment.SetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE", null);
                await Fails<InvalidOperationException>(entry, "");
                Environment.SetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE", fixture);
                Environment.SetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB", null);
                Environment.SetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED", null);
                await Fails<InvalidOperationException>(entry, "");
                Environment.SetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB", "1");
                Environment.SetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED", "1");
            }
            await Fails<ArgumentOutOfRangeException>(owner => GalleryPlaybackFeatureChecks.RunErrorInteractionChecksAsync(owner, (GalleryErrorCheckGroup)999), "");
            Console.WriteLine("PASS: 5 schedules, fixture failure, check failure, authorization, invalid group");
        }
        finally { File.Delete(fixture); }
    }
    private static readonly Func<MainWindow, Task>[] Entries = [GalleryErrorInteractionChecks.RunAsync,
        GalleryErrorInteractionChecks.RunRetirementClipAsync, GalleryErrorInteractionChecks.RunLayoutRootsAsync,
        GalleryErrorInteractionChecks.RunNotificationsAsync, GalleryErrorInteractionChecks.RunFailureReaderReturnAsync];
    private static async Task Schedule(Func<MainWindow, Task> run, string expected)
    {
        GalleryPlaybackFeatureChecks.Calls.Clear();
        await run(Owner);
        Verify(expected);
    }
    private static async Task Fails<T>(Func<MainWindow, Task> run, string expected) where T : Exception
    {
        GalleryPlaybackFeatureChecks.Calls.Clear();
        bool threw = false;
        try { await run(Owner); } catch (T) { threw = true; }
        if (!threw) throw new Exception("Expected " + typeof(T).Name);
        Verify(expected);
    }
    private static void Verify(string expected)
    {
        string actual = string.Join(",", GalleryPlaybackFeatureChecks.Calls);
        if (actual != expected) throw new Exception($"Expected {expected}; actual {actual}");
        if (TemporaryWorkspacePaths.Created.Any(Directory.Exists)) throw new Exception("Workspace leaked.");
    }
}
internal sealed class MainWindow;
internal sealed class GalleryEntry;
internal static class SKColors { internal static object DarkSlateBlue => new(); }
internal static class TemporaryWorkspacePaths
{
    internal static readonly List<string> Created = [];
    internal static string CreateDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid());
        Directory.CreateDirectory(path); Created.Add(path); return path;
    }
}
internal static partial class GalleryPlaybackFeatureChecks
{
    internal static readonly List<string> Calls = [];
    internal static string? FailAt;
    private static Task Record(string name)
    {
        Calls.Add(name);
        if (name == FailAt) throw new InvalidOperationException(name);
        return Task.CompletedTask;
    }
    private static string FindSyntheticAudioFixture(string video) => video;
    private static async Task<GalleryEntry> AssetAsync(string directory, string key, string name, byte[] bytes)
    { await Record("asset"); return new(); }
    private static async Task<GalleryEntry> ImageAsync(string directory, string key, object color)
    { await Record("image"); return new(); }
    private static Task CheckExternalTransportRetirementAsync(MainWindow owner) => Record("retirement");
    private static Task CheckNativeClipFeedbackAsync(MainWindow owner) => Record("clip");
    private static Task CheckContextLayoutRootsAsync(MainWindow owner) => Record("layout");
    private static Task CheckNotificationTransportAsync(MainWindow owner, GalleryEntry clip, bool overlay, bool paused) => Record($"notice-{overlay}-{paused}");
    private static Task CheckExplicitActualFailureAsync(MainWindow owner, GalleryEntry clip) => Record("explicit");
    private static Task CheckImageNotificationAsync(MainWindow owner, GalleryEntry image) => Record("image-notice");
    private static Task CheckActualMediaUnavailableAsync(MainWindow owner, string directory) => Record("missing");
    private static Task CheckShellFailureAndReaderReturnAsync(MainWindow owner, GalleryEntry clip, bool paused) => Record($"reader-{paused}");
}
internal static class PerformanceDiagnostics
{
    internal static Trace BeginRun(string name, string mode, string fixture) => new();
    internal sealed class Trace : IDisposable
    {
        internal SnapshotData Snapshot() => new([]);
        public void Dispose() { }
    }
    internal sealed record SnapshotData(Event[] Events);
    internal sealed record Event(long Sequence, string Phase);
}
