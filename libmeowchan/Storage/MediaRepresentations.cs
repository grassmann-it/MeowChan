using System;
using MeowChan.Archive;

namespace MeowChan.Storage;

// Passive observations, never source permission, acquisition or proof of a stored Original.
// Kept internal while the complete transfer/recovery/publication path is integrated.
internal sealed record MediaOccurrence(ResourceId Resource, string PostId, int Position,
    string? OriginalRevisionKey);
internal sealed record MediaPassiveObservation(string OperationId, string PackageFingerprint,
    DateTimeOffset ImportedAtUtc);
internal enum MediaObservedContentStatus { Contained, Omitted, Unknown, Unavailable, Failed, Cancelled }
internal enum MediaDerivedKind { Preview, VideoPosterPng }
internal enum MediaDerivedStatus { Contained, Omitted, Unavailable }
internal sealed record MediaOriginalEvidence(MediaOccurrence Occurrence, CapturedAttachment Metadata,
    MediaObservedContentStatus IncomingStatus, MediaActualIntegrity? OriginalByteVersion,
    MediaPassiveObservation Origin);
internal sealed record MediaDerivedBlob(string RelativeFileName, MediaActualIntegrity Actual);
internal sealed record MediaDerivedRepresentation(MediaOccurrence Parent, MediaActualIntegrity SourceVersion,
    MediaDerivedKind Kind, MediaDerivedStatus IncomingStatus, MediaDerivedBlob? Blob,
    MediaPassiveObservation Origin);
