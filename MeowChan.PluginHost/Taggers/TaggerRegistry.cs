using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Taggers;

/// <summary>Calls one tagger. In-process for tests and the reference double; the worker client supplies it for out-of-process plugins.</summary>
internal delegate Task<TaggerBatchResult> TaggerInvoker(TaggerBatchRequest request, PluginCallContext context,
    IProgress<TaggerBatchProgress>? progress, CancellationToken cancellationToken);

/// <summary>Services the host binds to every call of one plugin's taggers. Network access is not among them: see <see cref="TaggerRegistry"/>.</summary>
internal sealed record TaggerHostBinding(PluginConfiguration Configuration, IPluginResources Resources, IPluginLog Log,
    // Network capability comes from a host-bound transport (manual admission, no automatic redirects).
    IHostHttp? ConsentedHttp = null, string? ExecutionRevision = null, bool RequireVerifiedModelHash = false,
    string? ManifestSha256 = null);

/// <summary>A validated tagger of a registered plugin.</summary>
internal sealed record TaggerRegistration(string PluginId, string PluginVersion, TaggerDescription Tagger,
    TaggerHostBinding Binding, TaggerInvoker Invoke)
{
    // Snapshot the declarations so grants cannot change when a caller retains the original list.
    internal IReadOnlyList<HttpHostDeclaration> NetworkHosts { get; } = Array.AsReadOnly(Tagger.NetworkHosts
        .Select(host => new HttpHostDeclaration(new Uri("https://" + host.Host).IdnHost, HttpHostPurpose.Metadata)).ToArray());

    internal bool NeedsNetwork => NetworkHosts.Count > 0;
}

internal sealed class TaggerGrant : IDisposable
{
    private readonly CancellationTokenSource _revoked = new();

    internal TaggerGrant(TaggerRegistration registration)
    {
        Registration = registration;
        Token = _revoked.Token;
    }

    private TaggerRegistration Registration { get; }

    internal bool Permits(TaggerRegistration registration) =>
        ReferenceEquals(registration, Registration) && !Token.IsCancellationRequested;

    // This instance and its token are one grant revision, even across re-granting the same registration.
    internal CancellationToken Token { get; }

    public void Dispose() => _ = RevokeAsync();

    private async Task RevokeAsync()
    {
        using (_revoked)
            await _revoked.CancelAsync().ConfigureAwait(false);
    }
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
/// a tagger with network capability needs a grant for this registration and verified execution revision.
/// Consent is session state. Thread-safe.
/// </summary>
internal sealed class TaggerRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TaggerRegistration[]> _plugins = new(StringComparer.Ordinal);
    private readonly Dictionary<TaggerRegistration, TaggerGrant?> _grants = new(ReferenceEqualityComparer.Instance);

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
            foreach (TaggerRegistration registration in registrations)
                _grants.Add(registration, registration.NeedsNetwork ? null : new TaggerGrant(registration));
        }
        return registrations;
    }

    /// <summary>Removes the plugin's taggers and every consent given for them.</summary>
    internal bool Unregister(string pluginId)
    {
        lock (_sync)
        {
            if (!_plugins.Remove(pluginId, out TaggerRegistration[]? registrations)) return false;
            foreach (TaggerRegistration registration in registrations)
                if (_grants.Remove(registration, out TaggerGrant? grant)) grant?.Dispose();
            return true;
        }
    }

    internal TaggerRegistration? Find(string pluginId, string taggerKey)
    {
        lock (_sync)
            return _plugins.TryGetValue(pluginId, out TaggerRegistration[]? values)
                ? values.FirstOrDefault(value => value.Tagger.Key == taggerKey) : null;
    }

    /// <summary>Records consent for the active revision and its snapshotted HTTPS origins.</summary>
    internal void GrantNetworkConsent(TaggerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!registration.NeedsNetwork || string.IsNullOrWhiteSpace(registration.Binding.ExecutionRevision)
            || registration.Binding.ConsentedHttp is null) return;
        lock (_sync)
        {
            if (!_grants.TryGetValue(registration, out TaggerGrant? previous)) return;
            previous?.Dispose();
            _grants[registration] = new TaggerGrant(registration);
        }
    }

    internal void RevokeNetworkConsent(TaggerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!registration.NeedsNetwork) return;
        lock (_sync)
        {
            if (!_grants.TryGetValue(registration, out TaggerGrant? previous)) return;
            previous?.Dispose();
            _grants[registration] = null;
        }
    }

    internal TaggerGrant? GrantFor(TaggerRegistration registration)
    {
        lock (_sync) return _grants.GetValueOrDefault(registration);
    }

    internal bool MayRun(TaggerRegistration registration) => GrantFor(registration) is not null;
}
