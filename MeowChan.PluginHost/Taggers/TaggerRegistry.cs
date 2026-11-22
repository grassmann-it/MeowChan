using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Taggers;

/// <summary>Calls one tagger. In-process for tests and the reference double; the worker client supplies it for out-of-process plugins.</summary>
internal delegate Task<TaggerBatchResult> TaggerInvoker(TaggerBatchRequest request, PluginCallContext context,
    IProgress<TaggerBatchProgress>? progress, CancellationToken cancellationToken);

/// <summary>Services the host binds to every call of one plugin's taggers. Network access is not among them: see <see cref="TaggerRegistry"/>.</summary>
internal sealed record TaggerHostBinding(PluginConfiguration Configuration, IPluginResources Resources, IPluginLog Log,
    IHostHttp? ConsentedHttp = null);

/// <summary>A validated tagger of a registered plugin.</summary>
internal sealed record TaggerRegistration(string PluginId, string PluginVersion, TaggerDescription Tagger,
    TaggerHostBinding Binding, TaggerInvoker Invoke)
{
    internal bool NeedsNetwork => Tagger.NetworkHosts.Count > 0;

    /// <summary>What a consent covers: plugin version plus the exact, ordered host list. A new version or host list needs new consent.</summary>
    internal string NetworkConsentScope => PluginVersion + "\n" + string.Join("\n",
        Tagger.NetworkHosts.Select(host => host.Host.ToLowerInvariant()).Order(StringComparer.Ordinal));
}

internal enum TaggerRegistrationError { None, InvalidDescription, NoTaggers, DuplicatePlugin }

internal sealed class TaggerRegistrationException(TaggerRegistrationError error, IReadOnlyList<ValidationIssue>? issues = null)
    : Exception("The tagger registration was rejected: " + error + ".")
{
    internal TaggerRegistrationError Error { get; } = error;
    internal IReadOnlyList<ValidationIssue> Issues { get; } = issues ?? Array.Empty<ValidationIssue>();
}

/// <summary>
/// Registers taggers of validated plugin descriptions and holds the user's network consent. Offline is the default:
/// a tagger that declares no host never gets network access, and one that declares hosts is not runnable until the
/// user consented to exactly that plugin version and host list. Consent is session state here; persisting it is an
/// open decision (docs/development/tagger-contract-1.2.0.md). Thread-safe.
/// </summary>
internal sealed class TaggerRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TaggerRegistration[]> _plugins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _consent = new(StringComparer.Ordinal);

    internal IReadOnlyList<TaggerRegistration> Registrations
    {
        get { lock (_sync) return _plugins.Values.SelectMany(value => value).ToArray(); }
    }

    /// <summary>Validates the whole description (SDK rules) and registers each of its taggers with one invoker.</summary>
    internal IReadOnlyList<TaggerRegistration> Register(PluginDescription description, TaggerHostBinding binding, TaggerInvoker invoke)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(invoke);
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateDescription(description);
        if (issues.Count != 0) throw new TaggerRegistrationException(TaggerRegistrationError.InvalidDescription, issues);
        if (description.DeclarationStatus == PluginDeclarationStatus.Blocked || description.TaggersOrEmpty().Count == 0)
            throw new TaggerRegistrationException(TaggerRegistrationError.NoTaggers);
        TaggerRegistration[] registrations = description.TaggersOrEmpty()
            .Select(tagger => new TaggerRegistration(description.PluginId, description.PluginVersion, tagger, binding, invoke)).ToArray();
        lock (_sync)
        {
            if (_plugins.ContainsKey(description.PluginId)) throw new TaggerRegistrationException(TaggerRegistrationError.DuplicatePlugin);
            _plugins.Add(description.PluginId, registrations);
        }
        return registrations;
    }

    /// <summary>Removes the plugin's taggers and every consent given for them.</summary>
    internal bool Unregister(string pluginId)
    {
        lock (_sync)
        {
            foreach (string key in _consent.Keys.Where(key => key.StartsWith(pluginId + "\n", StringComparison.Ordinal)).ToArray())
                _consent.Remove(key);
            return _plugins.Remove(pluginId);
        }
    }

    internal TaggerRegistration? Find(string pluginId, string taggerKey)
    {
        lock (_sync)
            return _plugins.TryGetValue(pluginId, out TaggerRegistration[]? values)
                ? values.FirstOrDefault(value => value.Tagger.Key == taggerKey) : null;
    }

    /// <summary>Records the user's consent for the registration's current version and host list. Only the UI calls this, after showing the hosts.</summary>
    internal void GrantNetworkConsent(TaggerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!registration.NeedsNetwork) return;
        lock (_sync) _consent[ConsentKey(registration)] = registration.NetworkConsentScope;
    }

    internal void RevokeNetworkConsent(TaggerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_sync) _consent.Remove(ConsentKey(registration));
    }

    /// <summary>True for an offline tagger, or when the stored consent covers exactly the current version and hosts.</summary>
    internal bool MayRun(TaggerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!registration.NeedsNetwork) return true;
        lock (_sync)
            return _consent.TryGetValue(ConsentKey(registration), out string? scope) && scope == registration.NetworkConsentScope;
    }

    private static string ConsentKey(TaggerRegistration registration) => registration.PluginId + "\n" + registration.Tagger.Key;
}

/// <summary>Network access of a tagger call: none for offline taggers, and only the declared hosts after consent.</summary>
internal sealed class TaggerHttp(IReadOnlyList<HttpHostDeclaration> hosts, IHostHttp? inner) : IHostHttp
{
    internal static TaggerHttp Offline { get; } = new(Array.Empty<HttpHostDeclaration>(), null);

    public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool declared = request.Url is { IsAbsoluteUri: true, Scheme: "https" }
            && hosts.Any(host => string.Equals(host.Host, request.Url.Host, StringComparison.OrdinalIgnoreCase));
        if (inner is null || !declared)
            return Task.FromResult<HostHttpResult>(new HostHttpFailure(new FailureDetail("tagger_network_denied",
                "This tagger has no network access to this destination.")));
        return inner.SendAsync(request, cancellationToken);
    }
}
