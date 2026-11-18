using System.Text;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class VerifiedVideoFileTests
{
    [Fact]
    public async Task MaterializedInputIsByteExactAndDeletedAfterNativeOwnershipEnds()
    {
        byte[] expected = Encoding.UTF8.GetBytes("verified media bytes\0with a seekable boundary");
        string path;
        using (MemoryStream source = new(expected, writable: false))
        using (VerifiedVideoFile file = await VideoNativeInput.MaterializeAsync(source, "fixture.VP8", CancellationToken.None))
        {
            path = file.Path;
            Assert.EndsWith(".vp8", path, StringComparison.Ordinal);
            Assert.Equal(expected, await File.ReadAllBytesAsync(path));
            Assert.Equal(0, source.Position);
        }
        Assert.False(File.Exists(path));
    }
}
