using MeowChan.Downloader;

namespace MeowChan.Archive;

internal static class ProviderPostCapture
{
    internal static CapturedAttachment Attachment(DownloadItem item, int position, int? width = null, int? height = null) => new CapturedAttachment(position,
        item.MediaIdentifier, Variant: item.Variant, Url: item.DownloadUri.AbsoluteUri, OriginalFileName: item.OriginalFileName,
        PlannedPath: item.DestinationPath, ExpectedSize: item.ExpectedSize, ExpectedMd5: item.ExpectedMd5)
        { Width = width, Height = height, ExpectedSha256 = item.ExpectedSha256, ExpectedSha1 = item.ExpectedSha1, ProviderSizeHint = item.ProviderSizeHint };
}
