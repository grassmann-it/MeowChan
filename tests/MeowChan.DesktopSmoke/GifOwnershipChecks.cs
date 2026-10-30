using System.Buffers;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Testing;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Controlled managed ownership failures only; NOT a native-codec failure or pixel-playback proof.
internal static class GifOwnershipChecks
{
    internal static Task RunAsync(MainWindow owner) => GalleryPlaybackFeatureChecks.RunGifOwnershipChecksAsync(owner);
}

internal static partial class GalleryPlaybackFeatureChecks
{
    private sealed record GifTaskResult(bool Success, bool Cancelled, Exception? Error);

    internal static async Task RunGifOwnershipChecksAsync(MainWindow owner)
    {
        string directory = TemporaryWorkspacePaths.CreateDirectory("meowchan-gif-ownership-");
        try
        {
            string path = FindOwnershipGif();
            byte[] bytes = await File.ReadAllBytesAsync(path);
            Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Equals(
                "07ab9ab287a15e595a12797259548fa347c5a8c3bd9f5580b2b3e74b6093632d", StringComparison.OrdinalIgnoreCase),
                "Ownership fixture must use the pinned synthetic GIF entry.");
            GalleryEntry gif = await AssetAsync(directory, "ownership-gif", "animated-loop.gif", bytes);
            GalleryEntry png = await ImageAsync(directory, "ownership-recovery", SKColors.Coral);
            await CheckGifUnadoptedOwnershipAsync(owner, gif);
            await CheckGifFirstFrameOwnershipAsync(owner, gif, png, failDispose: true);
            await CheckGifFirstFrameOwnershipAsync(owner, gif, png, failDispose: false);
            Console.WriteLine("SMOKE GIF ownership: three actual Viewer Show/Stop controlled-fake cases passed; no physical native failure proof.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string FindOwnershipGif()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (DirectoryInfo? parent = new(start); parent != null; parent = parent.Parent)
            {
                string candidate = Path.Combine(parent.FullName, "tests", "Fixtures", "gif-playback", "animated-loop.gif");
                if (File.Exists(candidate)) return candidate;
            }
        throw new FileNotFoundException("Pinned ownership GIF fixture is absent; no generation or download.");
    }

    private static async Task<GifTaskResult> ObserveGifTaskAsync(Task task, string stage)
    {
        Exception? error = null;
        try { await task.WaitAsync(Deadline); }
        catch (Exception failure) { error = failure; }
        // A canceled/failed waiter is not a resource-release certificate. Demand actual task termination.
        Require(task.IsCompleted, "GIF ownership task did not terminate after gate release: " + stage);
        var result = new GifTaskResult(task.IsCompletedSuccessfully, task.IsCanceled, error);
        Console.WriteLine("SMOKE gif-ownership-task " + JsonSerializer.Serialize(new
        { stage, result.Success, result.Cancelled, errorType = error?.GetType().Name,
            retirementOwner = (error as GifRetirementException)?.Owner.GetType().Name }));
        return result;
    }

    private static GifRetirementException RequireGifRetirement(GifTaskResult result, object expectedOwner, string stage)
    {
        Require(!result.Success && !result.Cancelled && result.Error is GifRetirementException,
            "Actual Stop did not propagate typed retirement failure: " + stage);
        var failure = (GifRetirementException)result.Error!;
        Require(OwnsExactGifResource(failure.Owner, expectedOwner), "Retirement lost the strong exact owner (including composite ownership unit): " + stage);
        return failure;
    }

    private static bool OwnsExactGifResource(object? owner, object expected, int depth = 0) =>
        ReferenceEquals(owner, expected) || depth < 8 && owner is object[] unit
            && unit.Any(item => OwnsExactGifResource(item, expected, depth + 1));

    private static async Task CheckGifUnadoptedOwnershipAsync(MainWindow owner, GalleryEntry gif)
    {
        Fixture fixture = new(owner); // Deliberately NOT await using: Stop is expected to fault.
        var decoder = new OwnershipDecoder(failFirstFrame: false, failDispose: true);
        var entered = new TaskCompletionSource<GalleryGifPlayback>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? show = null, stop = null;
        Image image = Find<Image>(fixture.Viewer, "GalleryImage");
        int paints = 0;
        void SourceChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        { if (e.Property == Image.SourceProperty && image.Source != null) paints++; }
        image.PropertyChanged += SourceChanged;
        try
        {
            await fixture.ReadyAsync();
            fixture.Viewer.OpenGifDecoderForTests = async (_, _) => { await Task.Yield(); return decoder; };
            fixture.Viewer.BeforeGifAdoptionForTests = player => { entered.TrySetResult(player); return release.Task; };
            decoder.ReleaseDecode.TrySetResult();
            show = fixture.Viewer.ShowAsync(gif, CancellationToken.None);
            GalleryGifPlayback player = await entered.Task.WaitAsync(Deadline);
            Require(!show.IsCompleted && image.Source == null && paints == 0 && player.Evidence.FramesPresented == 0
                && decoder.DecodeCalls == 1 && decoder.LastIndex == 0 && decoder.DisposeCalls == 0 && decoder.ResourceHeld,
                "Factory painted/released the good first frame before adoption permission.");
            stop = fixture.Viewer.StopAsync();
            await Task.Delay(60);
            Require(!stop.IsCompleted && !show.IsCompleted && decoder.ResourceHeld && paints == 0,
                "Stop/cancellation declared retirement while unadopted open was held.");
            release.TrySetResult();
            await ObserveGifTaskAsync(show, "unadopted-show");
            GifTaskResult stopped = await ObserveGifTaskAsync(stop, "unadopted-stop");
            GifRetirementException first = RequireGifRetirement(stopped, player, "unadopted-stop");
            // Show may have been treated as obsolete/canceled; that is explicitly NOT the release result.
            Require(decoder.DisposeCalls == 1 && decoder.ResourceHeld && image.Source == null
                && paints == 0 && player.Evidence.FramesPresented == 0, "Failed unadopted retirement painted or released its fake owner.");
            GifTaskResult second = await ObserveGifTaskAsync(fixture.Viewer.StopAsync(), "unadopted-second-stop");
            RequireGifRetirement(second, first.Owner, "unadopted-second-stop");
            Require(decoder.DisposeCalls == 1 && decoder.ResourceHeld, "Second Stop retried/reset failed ownership instead of retaining it.");
            await HoldAsync(TimeSpan.FromMilliseconds(100), () => Require(paints == 0 && image.Source == null
                && player.Evidence.FramesPresented == 0, "Unadopted stale GIF painted after failed Stop."));
        }
        finally
        {
            release.TrySetResult(); decoder.ReleaseDecode.TrySetResult();
            await CleanupGifOwnershipAsync(fixture, decoder, show, stop);
            image.PropertyChanged -= SourceChanged;
        }
    }

    private static async Task CheckGifFirstFrameOwnershipAsync(MainWindow owner, GalleryEntry gif, GalleryEntry png, bool failDispose)
    {
        Fixture fixture = new(owner); // Manual cleanup must not mask the expected retirement error.
        var decoder = new OwnershipDecoder(failFirstFrame: true, failDispose);
        Task? show = null, stop = null;
        Image image = Find<Image>(fixture.Viewer, "GalleryImage");
        int paints = 0;
        var reports = new List<GalleryDiagnosticNotice>();
        void SourceChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        { if (e.Property == Image.SourceProperty && image.Source != null) paints++; }
        image.PropertyChanged += SourceChanged;
        fixture.Viewer.DiagnosticNotice += reports.Add;
        try
        {
            await fixture.ReadyAsync();
            fixture.Viewer.OpenGifDecoderForTests = async (_, _) => { await Task.Yield(); return decoder; };
            show = fixture.Viewer.ShowAsync(gif, CancellationToken.None);
            await decoder.DecodeEntered.Task.WaitAsync(Deadline);
            Require(!show.IsCompleted && paints == 0 && image.Source == null && decoder.ResourceHeld
                && decoder.DecodeCalls == 1 && decoder.LastIndex == 0, "Decode0 barrier did not own its actual pending open.");
            if (failDispose)
            {
                stop = fixture.Viewer.StopAsync();
                await Task.Delay(60);
                Require(!stop.IsCompleted && decoder.ResourceHeld && decoder.DisposeCalls == 0, "Stop used data cancellation as a release certificate.");
            }
            decoder.ReleaseDecode.TrySetResult(); // Simulated noncancelable codec returns its real decode error.
            GifTaskResult shown = await ObserveGifTaskAsync(show, failDispose ? "decode-and-dispose-fail-show" : "ordinary-decode-fail-show");
            if (failDispose)
            {
                GifTaskResult stopped = await ObserveGifTaskAsync(stop!, "decode-and-dispose-fail-stop");
                GifRetirementException first = RequireGifRetirement(stopped, decoder, "factory-decoder-owner");
                RequireGifRetirement(await ObserveGifTaskAsync(fixture.Viewer.StopAsync(), "factory-second-stop"), first.Owner, "factory-second-stop");
                Require(decoder.DisposeCalls == 1 && decoder.ResourceHeld && paints == 0 && image.Source == null,
                    "Failed first-frame factory cleanup forgot/released/painted its decoder owner.");
            }
            else
            {
                Require(shown.Success && !shown.Cancelled && decoder.DisposeSucceeded && decoder.DisposeCalls == 1
                    && paints == 0 && image.Source == null && fixture.Viewer.GifPlayback == null
                    && Find<TextBlock>(fixture.Viewer, "GalleryStatus").IsEffectivelyVisible
                    && reports.Any(report => report.MediaUnavailable && report.Entry?.Key == gif.Key),
                    "Ordinary decode failure with successful cleanup was not a neutral recoverable unavailable state.");
                Task next = fixture.Viewer.ShowAsync(png, CancellationToken.None);
                Require((await ObserveGifTaskAsync(next, "ordinary-recovery-PNG")).Success, "Ordinary data failure poisoned PNG navigation.");
                await UntilAsync(() => image.Source is Bitmap, "Recovery PNG never became visible.");
                Require(fixture.Viewer.CaptureState().Key == png.Key && fixture.Viewer.GifPlayback == null
                    && decoder.DecodeCalls == 1 && decoder.DisposeCalls == 1, "Recovery selected/stirred an old GIF decoder.");
                stop = fixture.Viewer.StopAsync();
                Require((await ObserveGifTaskAsync(stop, "ordinary-recovery-stop")).Success, "Successful factory cleanup poisoned later Stop.");
                Require((await ObserveGifTaskAsync(fixture.Viewer.StopAsync(), "ordinary-recovery-second-stop")).Success, "Ordinary decode error became sticky retirement poison.");
            }
        }
        finally
        {
            decoder.ReleaseDecode.TrySetResult();
            await CleanupGifOwnershipAsync(fixture, decoder, show, stop);
            image.PropertyChanged -= SourceChanged;
            fixture.Viewer.DiagnosticNotice -= reports.Add;
        }
    }

    private static async Task CleanupGifOwnershipAsync(Fixture fixture, OwnershipDecoder decoder, Task? show, Task? stop)
    {
        // Fixture-only finally: no product unlock/reset, no conversion of a failed Dispose into success.
        try
        {
            if (show != null) await ObserveGifTaskAsync(show, "finally-show");
            await ObserveGifTaskAsync(stop ?? fixture.Viewer.StopAsync(), "finally-stop");
        }
        finally
        {
            decoder.ReleaseSimulatedResourceForTestFinally();
            fixture.Window.Content = null;
            fixture.Window.Close(); // Do not call normal Fixture.DisposeAsync: its expected Stop fault would mask assertions.
        }
    }

    private sealed class OwnershipDecoder(bool failFirstFrame, bool failDispose) : IGifFrameDecoder
    {
        private object? _resource = new(); // No native pointer/handle/codec; retained for ownership assertions only.
        private Task? _dispose;
        internal TaskCompletionSource DecodeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDecode { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DecodeCalls { get; private set; }
        internal int LastIndex { get; private set; } = -1;
        internal int DisposeCalls { get; private set; }
        internal bool DisposeSucceeded { get; private set; }
        internal bool ResourceHeld => _resource != null;
        public GalleryImageInfo Info => new(2, 2, "GIF", StillFrame: false);
        public int FrameCount => 2;
        public int RepetitionCount => -1;
        public int DurationMs(int index) => 100;
        public async Task<GifDecodedFrame> DecodeAsync(int index, CancellationToken cancellationToken)
        {
            DecodeCalls++; LastIndex = index; DecodeEntered.TrySetResult();
            await ReleaseDecode.Task; // Deliberately ignores cancellation once work entered; cancellation is not release.
            if (failFirstFrame) throw new InvalidDataException("Controlled GIF Decode0 failure.");
            byte[] pixels = ArrayPool<byte>.Shared.Rent(16);
            for (int offset = 0; offset < 16; offset += 4)
            { pixels[offset] = 0; pixels[offset + 1] = 0; pixels[offset + 2] = 255; pixels[offset + 3] = 255; }
            return new GifDecodedFrame(pixels, 2, 2, 8, index, 100); // Factory owns/disposes this pooled frame.
        }
        public ValueTask DisposeAsync()
        {
            if (_dispose != null) return new(_dispose);
            DisposeCalls++;
            DisposeSucceeded = !failDispose;
            return new(_dispose = failDispose ? Task.FromException(new IOException("Controlled GIF retirement failure.")) : Task.CompletedTask);
        }
        // Test marker is held through ALL assertions; only the test's finally releases it.
        internal void ReleaseSimulatedResourceForTestFinally() => _resource = null;
    }
}
