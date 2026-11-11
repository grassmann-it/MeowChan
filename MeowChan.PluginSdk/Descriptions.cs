namespace MeowChan.PluginSdk;

using System.Text.Json.Serialization;

[Flags]
public enum SourceCapabilities
{
    None = 0,
    ThreadRead = 1,
    BoardCatalog = 2,
    PostCapture = 4,
    OriginalMedia = 8,
    Previews = 16,
    /// <summary>Booru post search (SDK 1.1.0). Never combined with the board/thread/capture capabilities.</summary>
    PostSearch = 32
}

[Flags]
public enum HttpHostPurpose { None = 0, Metadata = 1, OriginalMedia = 2, PreviewMedia = 4 }

public enum PostOrderKind { StableOrdinal, DecimalPostId }

/// <summary>Informational source maturity; it does not authorize relaxed media integrity checks.</summary>
public enum SourceMaturity { Unspecified, Experimental }
/// <summary>
/// Describes whether a plugin currently exposes an executable source/filter
/// capability. A blocked declaration is deliberately metadata-only: it may be
/// shipped to explain a researched source, but it must not claim a route or a
/// filter that the host could execute.
/// </summary>
public enum PluginDeclarationStatus { Available, Blocked }
public enum InformationKind { Text, Value, List }

public sealed record PluginDescription(string PluginId, string Name, string PluginVersion,
    string SdkVersion, string Description, IReadOnlyList<SourceDescription> Sources,
    IReadOnlyList<FilterDescription> Filters, SettingsSchema Settings,
    IReadOnlyList<InformationEntry> Information, string? Author = null,
    Uri? AuthorUrl = null, Uri? DocumentationUrl = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PluginDeclarationStatus DeclarationStatus { get; init; }
}

public sealed record SourceDescription(string SourceId, string Name, Uri BaseUri,
    SourceCapabilities Capabilities, IReadOnlyList<UrlRoute> Routes,
    IReadOnlyList<HttpHostDeclaration> HttpHosts, SettingsSchema Settings,
    PostOrderKind PostOrder = PostOrderKind.StableOrdinal)
{
    public SourceMaturity Maturity { get; init; }
}

/// <summary>Exact DNS host, without scheme, port, wildcard, credentials or path.</summary>
public sealed record HttpHostDeclaration(string Host, HttpHostPurpose Purpose);

public sealed record InformationEntry(string Key, string Label, InformationKind Kind,
    string? Value = null, IReadOnlyList<string>? Items = null, DateTimeOffset? AsOf = null,
    string? SourceId = null);
