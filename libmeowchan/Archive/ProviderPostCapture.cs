using MeowChan.Downloader;

namespace MeowChan.Archive;

internal static class ProviderPostCapture
{
    internal static CapturedAttachment Attachment(DownloadItem item, int position) => new CapturedAttachment(position,
        item.MediaIdentifier, Url: item.DownloadUri.AbsoluteUri, OriginalFileName: item.OriginalFileName,
        PlannedPath: item.DestinationPath, ExpectedSize: item.ExpectedSize, ExpectedMd5: item.ExpectedMd5);
}
