using System;
using System.Collections.Generic;
using System.IO;
using MeowChan.Rules;
using MeowChan.Storage;

namespace MeowChan.Archive;

/// <summary>A stable media identity; changing URLs, bytes, rules, or targets does not create a new decision.</summary>
public sealed record CandidateKey
{
    public ResourceId Resource { get; }
    public string MediaId { get; }
    public string Variant { get; }

    public CandidateKey(ResourceId resource, string mediaId, string variant = "original")
    {
        ArgumentNullException.ThrowIfNull(resource);
        _ = MediaRuleScope.ForThread(resource.Source, resource.Board, resource.Thread!);
        MediaRuleScope.ValidateIdentity(mediaId, nameof(mediaId));
        MediaRuleScope.ValidateIdentity(variant, nameof(variant));
        Resource = resource;
        MediaId = mediaId;
        Variant = variant;
    }
}

/// <summary>
/// Provider metadata for acquisition. A stored decision keeps its original target binding while
/// subsequent observations may update URI, expected bytes, name, and extension. No completion flag is stored here.
/// </summary>
public sealed record CandidateMetadata
{
    public CandidateKey Key { get; }
    public Uri DownloadUri { get; }
    public string DestinationPath { get; }
    public long? ExpectedSize { get; }
    public string? ExpectedMd5 { get; }
    public string? OriginalFileName { get; }
    public string? LegacyPath { get; }
    public string? Extension { get; }
    public string? ExpectedSha256 { get; init; }
    public string? ExpectedSha1 { get; init; }
    public MeowChan.Storage.MediaProviderSizeHint? ProviderSizeHint { get; init; }
    public string? AcquisitionRevisionKey { get; init; }
    private IReadOnlyList<AttachmentRevisionReference> _attachmentReferences = MediaAttachmentReferences.Empty;
    public IReadOnlyList<AttachmentRevisionReference> AttachmentReferences
    {
        get => _attachmentReferences;
        init => _attachmentReferences = MediaAttachmentReferences.CopyOf(value);
    }

    public CandidateMetadata(CandidateKey key, Uri downloadUri, string destinationPath,
        long? expectedSize = null, string? expectedMd5 = null, string? originalFileName = null,
        string? legacyPath = null, string? extension = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        ArgumentNullException.ThrowIfNull(downloadUri);
        if (!downloadUri.IsAbsoluteUri || (downloadUri.Scheme != Uri.UriSchemeHttp && downloadUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("A candidate URI must be absolute HTTP(S).", nameof(downloadUri));
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath))
            throw new ArgumentException("A candidate destination must be an absolute path.", nameof(destinationPath));
        if (legacyPath != null && !Path.IsPathFullyQualified(legacyPath))
            throw new ArgumentException("A legacy candidate path must be absolute when supplied.", nameof(legacyPath));
        if (expectedSize < 0) throw new ArgumentOutOfRangeException(nameof(expectedSize));
        DownloadUri = downloadUri;
        DestinationPath = Path.GetFullPath(destinationPath);
        ExpectedSize = expectedSize;
        ExpectedMd5 = expectedMd5;
        OriginalFileName = originalFileName;
        LegacyPath = legacyPath == null ? null : Path.GetFullPath(legacyPath);
        Extension = string.IsNullOrWhiteSpace(extension) ? null : MediaTypeSelection.NormalizeExtension(extension);
    }
}

/// <summary>Selection is immutable first intent; current provider metadata is separate from that decision.</summary>
public sealed record CandidateDecision(CandidateMetadata Metadata, MediaSelectionResult Selection, DateTimeOffset DecidedAtUtc);
