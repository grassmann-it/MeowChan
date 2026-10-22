namespace MeowChan.PluginSdk;

[Flags]
public enum SourceCapabilities
{
    None = 0,
    ThreadRead = 1,
    BoardCatalog = 2,
    PostCapture = 4,
    OriginalMedia = 8,
    Previews = 16
}

[Flags]
public enum HttpHostPurpose { None = 0, Metadata = 1, OriginalMedia = 2, PreviewMedia = 4 }

public enum PostOrderKind { StableOrdinal, DecimalPostId }
public enum InformationKind { Text, Value, List }

public sealed record PluginDescription(string PluginId, string Name, string PluginVersion,
    string SdkVersion, string Description, IReadOnlyList<SourceDescription> Sources,
    IReadOnlyList<FilterDescription> Filters, SettingsSchema Settings,
    IReadOnlyList<InformationEntry> Information, string? Author = null,
    Uri? AuthorUrl = null, Uri? DocumentationUrl = null);

public sealed record SourceDescription(string SourceId, string Name, Uri BaseUri,
    SourceCapabilities Capabilities, IReadOnlyList<UrlRoute> Routes,
    IReadOnlyList<HttpHostDeclaration> HttpHosts, SettingsSchema Settings,
    PostOrderKind PostOrder = PostOrderKind.StableOrdinal);

/// <summary>Exact DNS host, without scheme, port, wildcard, credentials or path.</summary>
public sealed record HttpHostDeclaration(string Host, HttpHostPurpose Purpose);

public sealed record InformationEntry(string Key, string Label, InformationKind Kind,
    string? Value = null, IReadOnlyList<string>? Items = null, DateTimeOffset? AsOf = null,
    string? SourceId = null);
