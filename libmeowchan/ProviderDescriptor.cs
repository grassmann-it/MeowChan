using System;
using System.Collections.Generic;

namespace MeowChan;

[Flags]
public enum ProviderCapabilities
{
    None = 0,
    ThreadWatch = 1,
    BoardCatalog = 2,
    PostCapture = 4,
    OriginalMedia = 8,
    Previews = 16
}

/// <summary>Optional passive description; existing IImageBoard implementations need not implement it.</summary>
public interface IProviderDescriptorSource
{
    ProviderCapabilities Capabilities { get; }
    IReadOnlyList<string> SupportedHosts { get; }
    string? AdapterVersion => null;
}

/// <summary>
/// Registered implementation capabilities, not proof of site availability. Null capabilities mean
/// unspecified. CaptureAvailable also requires initialized session content services.
/// </summary>
public sealed record ProviderDescriptor(string SourceId, string DisplayName, Uri BaseUri,
    ProviderCapabilities? Capabilities, IReadOnlyList<string> SupportedHosts, string? AdapterVersion,
    bool CaptureAvailable);
