namespace MeowChan.Downloader;

using System;
using System.IO;
using MeowChan.Interfaces;

public class DownloadItem
{
    public Uri DownloadUri { get; }
    public string DestinationPath { get; }
    public IImageBoard ImageBoard { get; }
    public IThread Thread { get; }
    public string MediaIdentifier { get; }
    public string Variant { get; }
    public long? ExpectedSize { get; }
    public string? ExpectedMd5 { get; }
    public string? OriginalFileName { get; }
    public string? LegacyPath { get; }
    internal IOriginalMediaFilter? Filter { get; set; }
    internal string? FilterPostId { get; set; }
    internal int FilterPosition { get; set; }

#pragma warning disable RS0027 // Preserve the existing optional-argument API while adding a distinct explicit-variant overload.
    public DownloadItem(Uri downloadUri, string destinationPath, IImageBoard imageBoard, IThread thread, string mediaIdentifier,
        long? expectedSize = null, string? expectedMd5 = null, string? originalFileName = null, string? legacyPath = null)
        : this(downloadUri, destinationPath, imageBoard, thread, mediaIdentifier, expectedSize, expectedMd5, originalFileName, legacyPath, "original") { }

#pragma warning restore RS0027
    public DownloadItem(Uri downloadUri, string destinationPath, IImageBoard imageBoard, IThread thread, string mediaIdentifier,
        long? expectedSize, string? expectedMd5, string? originalFileName, string? legacyPath, string variant)
    {
        if (downloadUri == null)
        {
            throw new ArgumentNullException(nameof(downloadUri));
        }

        if (string.IsNullOrEmpty(destinationPath))
        {
            throw new ArgumentNullException(nameof(destinationPath));
        }

        if (imageBoard == null)
        {
            throw new ArgumentNullException(nameof(imageBoard));
        }

        if (thread == null)
        {
            throw new ArgumentNullException(nameof(thread));
        }

        if (string.IsNullOrEmpty(mediaIdentifier))
        {
            throw new ArgumentNullException(nameof(mediaIdentifier));
        }

        DestinationPath = Path.GetFullPath(destinationPath);

        DownloadUri = downloadUri;
        ImageBoard = imageBoard;
        Thread = thread;
        MediaIdentifier = mediaIdentifier;
        MeowChan.Rules.MediaRuleScope.ValidateIdentity(variant, nameof(variant));
        Variant = variant;
        if (expectedSize < 0) throw new ArgumentOutOfRangeException(nameof(expectedSize));
        ExpectedSize = expectedSize;
        ExpectedMd5 = expectedMd5;
        OriginalFileName = originalFileName;
        LegacyPath = legacyPath;

    }
}
