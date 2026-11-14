using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class GalleryFramePngTests
{
    // R07 (review 14.11. night): the software frame is encoded and written on a worker thread; failures are false, not thrown.
    [Fact]
    public async Task ASoftwareFrameIsEncodedOffTheCallingThreadAndFailuresAreReported()
    {
        using TempDirectory profile = new();
        int caller = Environment.CurrentManagedThreadId, writer = caller;
        bool pool = false;
        GalleryFramePng.Writing = () => { writer = Environment.CurrentManagedThreadId; pool = Thread.CurrentThread.IsThreadPoolThread; };
        try
        {
            byte[] bgra = new byte[3 * 2 * 4];
            for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 0x20; bgra[i + 1] = 0x40; bgra[i + 2] = 0x80; bgra[i + 3] = 0xFF; }
            string path = Path.Combine(profile.Path, "frame.png");
            Assert.True(await GalleryFramePng.WriteAsync(new GalleryFramePixels(3, 2, bgra), path));
            Assert.NotEqual(caller, writer);
            Assert.True(pool);
            using (SkiaSharp.SKBitmap decoded = SkiaSharp.SKBitmap.Decode(path))
            {
                Assert.Equal((3, 2), (decoded.Width, decoded.Height));
                Assert.Equal(new SkiaSharp.SKColor(0x80, 0x40, 0x20, 0xFF), decoded.GetPixel(2, 1));
            }

            Assert.False(await GalleryFramePng.WriteAsync(new GalleryFramePixels(3, 2, bgra), Path.Combine(profile.Path, "missing", "frame.png")));
            Assert.False(File.Exists(Path.Combine(profile.Path, "missing", "frame.png")));
        }
        finally { GalleryFramePng.Writing = null; }
    }
}
