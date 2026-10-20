using MeowChan.Desktop;
using SkiaSharp;

namespace MeowChan.DesktopTests;

public sealed class GalleryViewerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ActualPixelsHaveOnePhysicalPixelPerSourcePixel(double displayScale)
    {
        const int pixels = 2401;
        double logicalWidth = pixels / displayScale;
        Assert.Equal(pixels, logicalWidth * displayScale, 8);
        Assert.Equal(0, GalleryGeometry.Pan(1000, 100, 1, displayScale, 600));
        Assert.Equal((pixels / displayScale - 600) / 2,
            GalleryGeometry.Pan(10000, pixels, 1, displayScale, 600), 8);
        double fit = GalleryGeometry.Fit(pixels, 1601, 600, 400, displayScale);
        Assert.True(pixels * fit / displayScale <= 600);
        Assert.True(1601 * fit / displayScale <= 400);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void DecoderMatrixRecognizesAndActuallyDecodesEachFormat(string format, string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        using MemoryStream stream = new(bytes);
        GalleryImageInfo info = GalleryImageDecoder.Inspect(stream);
        Assert.Equal(17, info.Width); Assert.Equal(11, info.Height);
        Assert.Equal(format, info.Format.ToUpperInvariant());
        Assert.Equal(format == "GIF", info.StillFrame);
        using SKBitmap? decoded = SKBitmap.Decode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal(17, decoded.Width); Assert.Equal(11, decoded.Height);
        Assert.Throws<InvalidDataException>(() => GalleryImageDecoder.Inspect(new MemoryStream(bytes), maximumPixels: 100));
    }
    [Fact]
    public void UnsupportedBytesFailBeforePixelAllocation()
    {
        using MemoryStream bytes = new("not an image"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => GalleryImageDecoder.Inspect(bytes));
    }
    public static IEnumerable<object[]> Formats()
    {
        yield return new object[] { "JPEG", "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAALABEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDBooormP00KKKKAP/Z" };
        yield return new object[] { "PNG", "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=" };
        yield return new object[] { "WEBP", "UklGRkQAAABXRUJQVlA4IDgAAACwAgCdASoRAAsAPm0skkWkIqGYBABABsSgC7AC0kAA/uwmXGE0k6P/2ln//bQ//9tD/lzFfAAAAA==" };
        yield return new object[] { "BMP", "Qk1yAgAAAAAAADYAAAAoAAAAEQAAAAsAAAABABgAAAAAADwCAADEDgAAxA4AAAAAAAAAAAAAUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcAFAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3ABQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNwAUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcAFAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3ABQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNwAUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcAFAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3ABQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNxQKNwAUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcUCjcAFAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3FAo3AA=" };
        yield return new object[] { "GIF", "R0lGODdhEQALAIEAANwoUAAAAAAAAAAAACwAAAAAEQALAAAIGAABCBxIsKDBgwgTKlzIsKHDhxAjSoQYEAA7" };
    }
}
