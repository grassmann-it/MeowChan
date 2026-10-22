using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration;

/// <summary>
/// Profile-owned atomic store. A separate stable lock file serializes writers across processes;
/// the revision is rechecked under that lock. Corrupt state is never replaced with defaults.
/// </summary>
public sealed class PluginProfileStore : IPluginProfileStore
{
    private const int FormatVersion = 1;
    private const int MaximumStoreBytes = 24 * 1024 * 1024;
    private readonly string directory;
    private readonly string path;
    private readonly string lockPath;

    public PluginProfileStore(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        directory = Path.Combine(Path.GetFullPath(profileDirectory), "plugin-state");
        path = Path.Combine(directory, "state.json");
        lockPath = Path.Combine(directory, "state.lock");
    }

    public string StorePath => path;
    public event Action<PluginLifecycleChange>? LifecycleChanged;
    public event Action<PluginGrantChange>? GrantChanged;

    public Task<PluginProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default) => ReadCoreAsync(cancellationToken);

    public Task<PluginProfileSnapshot> ObserveCandidateAsync(PluginCandidateObservation candidate, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        candidate = Clone(candidate);
        ValidateObservation(candidate);
        return MutateAsync(expectedRevision, state =>
        {
            PluginActivationSnapshot plugin = FindOrCreate(state, candidate.PluginId);
            List<PluginCandidateSnapshot> candidates = plugin.Candidates.Where(item => item.CandidateId != candidate.CandidateId).ToList();
            if (state.Plugins.Any(item => item.PluginId != candidate.PluginId && item.Candidates.Any(existing => existing.CandidateId == candidate.CandidateId)))
                throw new ArgumentException("A candidate identity cannot move to another plugin implicitly.");
            candidates.Add(ToSnapshot(candidate));
            return Replace(state, plugin with { Candidates = candidates });
        }, cancellationToken);
    }

    public Task<PluginProfileSnapshot> ApplyStartupAsync(IReadOnlyList<PluginCandidateObservation> candidates, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        PluginCandidateObservation[] observations = Clone(candidates.ToArray());
        foreach (PluginCandidateObservation candidate in observations) ValidateObservation(candidate);
        if (observations.Select(item => item.CandidateId).Distinct(StringComparer.Ordinal).Count() != observations.Length)
            throw new ArgumentException("Startup inventory contains duplicate candidate identities.");
        return MutateAsync(expectedRevision, state =>
        {
            // A source file changing its declared plugin identity is an unavailable update of
            // that existing candidate, not permission to transfer its configuration or rights.
            Dictionary<string, string> candidateOwners = state.Plugins.SelectMany(plugin => plugin.Candidates
                .Select(candidate => (candidate.CandidateId, plugin.PluginId)))
                .ToDictionary(item => item.CandidateId, item => item.PluginId, StringComparer.Ordinal);
            PluginCandidateObservation[] startup = observations.Select(candidate =>
                candidateOwners.TryGetValue(candidate.CandidateId, out string? owner) && owner != candidate.PluginId
                    ? new PluginCandidateObservation(candidate.CandidateId, owner, null, null,
                        $"Candidate plugin identity changed from '{owner}' to '{candidate.PluginId}'. Install it as a separate candidate to select the new identity explicitly.")
                    : candidate).ToArray();
            // Preserve the old ownership of active source IDs before any new candidates are considered.
            Dictionary<string, string> sourceOwners = new(StringComparer.Ordinal);
            foreach (PluginActivationSnapshot old in state.Plugins.Where(plugin => plugin.Active is not null))
                foreach (SourceDescription source in old.Active!.Description.Sources) sourceOwners.TryAdd(source.SourceId, old.PluginId);
            foreach ((string sourceId, string owner) in state.SelectedSourceOwners) sourceOwners[sourceId] = owner;
            List<PluginActivationSnapshot> plugins = new();
            IEnumerable<string> pluginIds = state.Plugins.Select(plugin => plugin.PluginId)
                .Concat(startup.Select(candidate => candidate.PluginId)).Distinct(StringComparer.Ordinal);
            foreach (string pluginId in pluginIds)
            {
                PluginActivationSnapshot old = FindOrCreate(state, pluginId);
                PluginCandidateObservation[] present = startup.Where(candidate => candidate.PluginId == pluginId).ToArray();
                List<PluginCandidateSnapshot> inventory = old.Candidates
                    .Where(candidate => !present.Any(item => item.CandidateId == candidate.CandidateId))
                    .Select(candidate => candidate with { IsPresent = false, Problem = "Candidate is missing from the startup inventory." }).ToList();
                inventory.AddRange(present.Select(ToSnapshot));
                string? selected = old.SelectedCandidateId;
                if (selected is null && present.Length == 1) selected = present[0].CandidateId;
                PluginActivationSnapshot next = old with { Candidates = inventory, SelectedCandidateId = selected, Active = null,
                    LastGood = old.Active ?? old.LastGood, LifecycleRevision = checked(old.LifecycleRevision + 1), Problem = null };
                PluginReleaseSnapshot? release = null;
                if (old.Pinned is not null)
                    release = old.Pending is not null && SameBuild(old.Pending, old.Pinned) ? old.Pending : old.Pinned;
                else if (selected is null)
                    next = next with { Problem = present.Length > 1 ? "Multiple candidates require an explicit selection." : "Plugin is missing." };
                else
                {
                    PluginCandidateSnapshot? candidate = inventory.FirstOrDefault(item => item.CandidateId == selected);
                    if (candidate is null || !candidate.IsPresent || candidate.Problem is not null || candidate.Artifact is null || candidate.Description is null)
                        next = next with { Problem = candidate?.Problem ?? "Selected candidate is unavailable." };
                    else
                    {
                        PluginReleaseSnapshot? basis = MatchingBasis(old, candidate.CandidateId);
                        if (basis?.ExecutionRevision == candidate.Artifact.ExecutionRevision &&
                            SdkJson.Serialize(basis.Description).AsSpan().SequenceEqual(SdkJson.Serialize(candidate.Description)))
                            release = basis;
                        else
                        {
                            ConfigurationMigrationResult migration = PluginSettingsMigration.Migrate(basis, candidate.Description);
                            if (migration.Configuration is null)
                                next = next with { Problem = "Settings correction required: " + string.Join("; ", migration.Issues.Select(issue => issue.Path + ": " + issue.Message)) };
                            else release = new(candidate.CandidateId, candidate.Artifact, candidate.Description, migration.Configuration);
                        }
                    }
                }
                if (release is not null)
                {
                    string[] conflicts = release.Description.Sources.Where(source => sourceOwners.TryGetValue(source.SourceId, out string? owner)
                        && owner != pluginId).Select(source => source.SourceId).ToArray();
                    // With no prior owner, two new descriptions are ambiguous: neither wins by enumeration order.
                    string[] newConflicts = release.Description.Sources.Where(source => !sourceOwners.ContainsKey(source.SourceId)
                        && startup.Any(other => other.PluginId != pluginId && other.Description?.Sources.Any(item => item.SourceId == source.SourceId) == true))
                        .Select(source => source.SourceId).ToArray();
                    if (conflicts.Length + newConflicts.Length != 0)
                        next = next with { Problem = "Source identity conflict requires explicit selection: " + string.Join(", ", conflicts.Concat(newConflicts)) };
                    else
                    {
                        PluginReleaseSnapshot? lastGood = old.LastGood;
                        if (old.Active is not null && !SameBuild(old.Active, release)) lastGood = old.Active;
                        next = next with { Active = release, Pending = null, LastGood = lastGood ?? release,
                            Pinned = old.Pinned is null ? null : release,
                            LifecycleRevision = checked(old.LifecycleRevision + 1),
                            Problem = present.Length > 1 ? "Multiple candidates exist; the selected candidate is retained." : null };
                    }
                }
                plugins.Add(next);
            }
            return state with { Plugins = plugins };
        }, cancellationToken);
    }

    public Task<PluginProfileSnapshot> SelectCandidateAsync(string pluginId, string candidateId, long expectedRevision,
        CancellationToken cancellationToken = default) => MutateAsync(expectedRevision, state =>
    {
        PluginActivationSnapshot plugin = Find(state, pluginId);
        PluginCandidateSnapshot candidate = AvailableCandidate(plugin, candidateId);
        ConfigurationMigrationResult migration = PluginSettingsMigration.Migrate(MatchingBasis(plugin, candidateId), candidate.Description!);
        // Selecting a candidate with incompatible settings records the choice, retaining the old values for explicit correction.
        PluginReleaseSnapshot? pending = migration.Configuration is null ? null : new(candidateId, candidate.Artifact!, candidate.Description!, migration.Configuration);
        Dictionary<string, string> selectedSources = new(state.SelectedSourceOwners, StringComparer.Ordinal);
        foreach (SourceDescription source in candidate.Description!.Sources) selectedSources[source.SourceId] = pluginId;
        return Replace(state, plugin with { SelectedCandidateId = candidateId, Pinned = null, Pending = pending,
            Problem = migration.Configuration is null ? "Selected candidate needs settings correction: " + string.Join("; ", migration.Issues.Select(issue => issue.Message)) : null })
            with { SelectedSourceOwners = selectedSources };
    }, cancellationToken);

    public Task<PluginProfileSnapshot> SaveConfigurationAsync(string pluginId, string candidateId, PluginConfiguration configuration,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        configuration = Clone(configuration);
        return MutateAsync(expectedRevision, state =>
        {
            PluginActivationSnapshot plugin = Find(state, pluginId);
            PluginReleaseSnapshot? pinned = plugin.Pinned?.CandidateId == candidateId ? plugin.Pinned : null;
            PluginCandidateSnapshot? candidate = pinned is null ? AvailableCandidate(plugin, candidateId) : null;
            PluginDescription description = pinned?.Description ?? candidate!.Description!;
            ThrowValidation(SdkValidation.ValidateConfiguration(description, configuration));
            Dictionary<string, SettingsSnapshot> sources = description.Sources.ToDictionary(source => source.SourceId,
                source => PluginSettingsMigration.WithDefaults(source.Settings, configuration.SourceSettings[source.SourceId]), StringComparer.Ordinal);
            PluginConfiguration saved = new(Guid.NewGuid().ToString("N"),
                PluginSettingsMigration.WithDefaults(description.Settings, configuration.PluginSettings), sources);
            PluginReleaseSnapshot pending = new(candidateId, pinned?.Artifact ?? candidate!.Artifact!, description, saved);
            return Replace(state, plugin with { SelectedCandidateId = candidateId, Pending = pending, Problem = null });
        }, cancellationToken);
    }

    public Task<PluginProfileSnapshot> SelectRollbackAsync(string pluginId, long expectedRevision,
        CancellationToken cancellationToken = default) => MutateAsync(expectedRevision, state =>
    {
        PluginActivationSnapshot plugin = Find(state, pluginId);
        PluginReleaseSnapshot release = plugin.LastGood ?? throw new InvalidOperationException("No retained good release is available for rollback.");
        Dictionary<string, string> selectedSources = new(state.SelectedSourceOwners, StringComparer.Ordinal);
        foreach (SourceDescription source in release.Description.Sources) selectedSources[source.SourceId] = pluginId;
        return Replace(state, plugin with { SelectedCandidateId = release.CandidateId, Pending = release, Pinned = release, Problem = null })
            with { SelectedSourceOwners = selectedSources };
    }, cancellationToken);

    public async Task<PluginProfileSnapshot> SetEnabledAsync(string pluginId, bool enabled, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        PluginLifecycleChange? change = null;
        PluginProfileSnapshot result = await MutateAsync(expectedRevision, state =>
        {
            PluginActivationSnapshot plugin = Find(state, pluginId);
            if (plugin.Enabled == enabled) return state;
            PluginActivationSnapshot updated = plugin with { Enabled = enabled, LifecycleRevision = checked(plugin.LifecycleRevision + 1) };
            change = new(pluginId, enabled, updated.LifecycleRevision);
            return Replace(state, updated);
        }, cancellationToken).ConfigureAwait(false);
        if (change is not null) LifecycleChanged?.Invoke(change);
        return result;
    }

    public async Task<PluginProfileSnapshot> SetGrantAsync(string pluginId, string filterKey, PluginGrantScope scope,
        FilterDataAccess access, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireIdentity(scope.SourceId);
        if (scope.BoardId is not null) RequireIdentity(scope.BoardId);
        if (scope.RuleId is not null) RequireIdentity(scope.RuleId);
        const FilterDataAccess allowed = FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage;
        if ((access & ~allowed) != 0) throw new ArgumentOutOfRangeException(nameof(access));
        PluginGrantSnapshot? changed = null;
        PluginProfileSnapshot result = await MutateAsync(expectedRevision, state =>
        {
            PluginActivationSnapshot plugin = Find(state, pluginId);
            if (access != FilterDataAccess.None && !KnownDescriptions(plugin).Any(description => description.Filters.Any(filter => filter.Key == filterKey)))
                throw new ArgumentException("The filter capability is not declared by this plugin.");
            PluginGrantSnapshot? previous = state.Grants.FirstOrDefault(grant => grant.PluginId == pluginId && grant.FilterKey == filterKey && grant.Scope == scope);
            changed = new(pluginId, filterKey, scope, access, checked((previous?.Revision ?? 0) + 1));
            List<PluginGrantSnapshot> grants = state.Grants.Where(grant => grant != previous).ToList();
            grants.Add(changed);
            return state with { Grants = grants };
        }, cancellationToken).ConfigureAwait(false);
        GrantChanged?.Invoke(new(changed!));
        return result;
    }

    public Task<PluginProfileSnapshot> ReportActivationFailureAsync(string pluginId, string executionRevision, string problem,
        long expectedRevision, CancellationToken cancellationToken = default) => MutateAsync(expectedRevision, state =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        PluginActivationSnapshot plugin = Find(state, pluginId);
        if (plugin.Active?.ExecutionRevision != executionRevision) throw new InvalidOperationException("Failure belongs to a stale activation.");
        return Replace(state, plugin with { Active = null, Problem = problem, LifecycleRevision = checked(plugin.LifecycleRevision + 1) });
    }, cancellationToken);

    private async Task<PluginProfileSnapshot> MutateAsync(long expectedRevision, Func<PluginProfileSnapshot, PluginProfileSnapshot> update,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        await using FileStream lease = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        PluginProfileSnapshot state = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
        if (state.Revision != expectedRevision) throw new PluginProfileConflictException(expectedRevision, state.Revision);
        PluginProfileSnapshot next = update(state) with { Revision = checked(state.Revision + 1) };
        ValidateState(next);
        byte[] payload = SdkJson.Serialize(next);
        if (payload.Length > SdkLimits.MaximumMetadataBytes * 2) throw new IOException("Plugin profile payload exceeds its bounded store size.");
        Envelope envelope = new(FormatVersion, Convert.ToHexString(SHA256.HashData(payload)), payload);
        byte[] bytes = SdkJson.Serialize(envelope);
        if (bytes.Length > MaximumStoreBytes) throw new IOException("Plugin profile state exceeds its bounded store size.");
        string temporary = Path.Combine(directory, ".state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return Clone(next);
    }

    private async Task<PluginProfileSnapshot> ReadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes;
            try
            {
                await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaximumStoreBytes) throw new JsonException("Store size limit exceeded.");
                bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { return Empty(); }
            catch (DirectoryNotFoundException) { return Empty(); }
            Envelope envelope = JsonSerializer.Deserialize<Envelope>(bytes, SdkJson.CreateOptions()) ?? throw new JsonException("Null envelope.");
            if (envelope.Version != FormatVersion || envelope.Payload is null ||
                !string.Equals(envelope.Sha256, Convert.ToHexString(SHA256.HashData(envelope.Payload)), StringComparison.Ordinal))
                throw new JsonException("Store version or integrity check failed.");
            PluginProfileSnapshot state = SdkJson.Deserialize<PluginProfileSnapshot>(envelope.Payload);
            ValidateState(state);
            return state;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or OverflowException or NullReferenceException or EndOfStreamException)
        { throw new PluginProfileCorruptException(path, exception); }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(20, cancellationToken).ConfigureAwait(false); }
        }
    }

    private static PluginProfileSnapshot Empty() => new(0, Array.Empty<PluginActivationSnapshot>(), Array.Empty<PluginGrantSnapshot>());
    private static T Clone<T>(T value) => SdkJson.Deserialize<T>(SdkJson.Serialize(value));
    private static PluginActivationSnapshot Find(PluginProfileSnapshot state, string pluginId) => state.Plugins.SingleOrDefault(plugin => plugin.PluginId == pluginId)
        ?? throw new KeyNotFoundException("Unknown plugin: " + pluginId);
    private static PluginActivationSnapshot FindOrCreate(PluginProfileSnapshot state, string pluginId) => state.Plugins.SingleOrDefault(plugin => plugin.PluginId == pluginId)
        ?? new(pluginId, true, 0, null, null, null, null, null, Array.Empty<PluginCandidateSnapshot>(), null);
    private static PluginProfileSnapshot Replace(PluginProfileSnapshot state, PluginActivationSnapshot plugin) => state with
    { Plugins = state.Plugins.Where(item => item.PluginId != plugin.PluginId).Append(plugin).ToArray() };
    private static PluginCandidateSnapshot ToSnapshot(PluginCandidateObservation candidate) => new(candidate.CandidateId, candidate.PluginId,
        candidate.Artifact, candidate.Description, true, candidate.Problem);
    private static bool SameBuild(PluginReleaseSnapshot a, PluginReleaseSnapshot b) => a.CandidateId == b.CandidateId && a.ExecutionRevision == b.ExecutionRevision;
    private static PluginReleaseSnapshot? MatchingBasis(PluginActivationSnapshot plugin, string candidateId) =>
        new[] { plugin.Pending, plugin.Active, plugin.LastGood }.FirstOrDefault(release => release?.CandidateId == candidateId);
    private static PluginCandidateSnapshot AvailableCandidate(PluginActivationSnapshot plugin, string candidateId) => plugin.Candidates.SingleOrDefault(candidate =>
        candidate.CandidateId == candidateId && candidate.IsPresent && candidate.Problem is null && candidate.Artifact is not null && candidate.Description is not null)
        ?? throw new InvalidOperationException("Selected candidate has no validated build and description.");
    private static IEnumerable<PluginDescription> KnownDescriptions(PluginActivationSnapshot plugin) => plugin.Candidates.Select(candidate => candidate.Description)
        .Concat(new[] { plugin.Active?.Description, plugin.Pending?.Description, plugin.LastGood?.Description, plugin.Pinned?.Description }).OfType<PluginDescription>();
    private static void ThrowValidation(IReadOnlyList<ValidationIssue> issues)
    { if (issues.Count != 0) throw new PluginConfigurationValidationException(issues); }
    private static void RequireIdentity(string value)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > 512) throw new ArgumentException("A bounded stable identity is required."); }
    private static void ValidateObservation(PluginCandidateObservation candidate)
    {
        RequireIdentity(candidate.CandidateId); RequireIdentity(candidate.PluginId);
        if (candidate.Description is not null)
        {
            if (candidate.Description.PluginId != candidate.PluginId) throw new ArgumentException("Candidate and description plugin identities differ.");
            ThrowValidation(SdkValidation.ValidateDescription(candidate.Description));
        }
        if (candidate.Problem is null && (candidate.Artifact is null || candidate.Description is null))
            throw new ArgumentException("A successful candidate requires both artifact and description.");
        if (candidate.Artifact is not null)
        { RequireIdentity(candidate.Artifact.ExecutionRevision); RequireIdentity(candidate.Artifact.CompilationFingerprint); }
    }

    private static void ValidateState(PluginProfileSnapshot state)
    {
        if (state.Revision < 0 || state.Plugins is null || state.Grants is null || state.SelectedSourceOwners is null)
            throw new ArgumentException("Invalid profile collections or revision.");
        if (state.Plugins.Select(plugin => plugin.PluginId).Distinct(StringComparer.Ordinal).Count() != state.Plugins.Count)
            throw new ArgumentException("Duplicate plugin state.");
        foreach ((string sourceId, string pluginId) in state.SelectedSourceOwners)
        {
            RequireIdentity(sourceId);
            if (!state.Plugins.Any(plugin => plugin.PluginId == pluginId)) throw new ArgumentException("Unknown selected source owner.");
        }
        HashSet<string> candidateOwners = new(StringComparer.Ordinal);
        foreach (PluginActivationSnapshot plugin in state.Plugins)
        {
            RequireIdentity(plugin.PluginId);
            if (plugin.LifecycleRevision < 0 || plugin.Candidates is null) throw new ArgumentException("Invalid lifecycle state.");
            foreach (PluginCandidateSnapshot candidate in plugin.Candidates)
            {
                if (!candidateOwners.Add(candidate.CandidateId) || candidate.PluginId != plugin.PluginId) throw new ArgumentException("Conflicting candidate ownership.");
                ValidateObservation(new(candidate.CandidateId, candidate.PluginId, candidate.Artifact, candidate.Description, candidate.Problem));
            }
            foreach (PluginReleaseSnapshot release in new[] { plugin.Active, plugin.Pending, plugin.LastGood, plugin.Pinned }.OfType<PluginReleaseSnapshot>())
            {
                if (release.Description.PluginId != plugin.PluginId) throw new ArgumentException("Release belongs to a different plugin.");
                ValidateObservation(new(release.CandidateId, plugin.PluginId, release.Artifact, release.Description));
                ThrowValidation(SdkValidation.ValidateConfiguration(release.Description, release.Configuration));
            }
        }
        if (state.Grants.Any(grant => grant.Revision <= 0 || !state.Plugins.Any(plugin => plugin.PluginId == grant.PluginId))
            || state.Grants.GroupBy(grant => (grant.PluginId, grant.FilterKey, grant.Scope)).Any(group => group.Count() != 1))
            throw new ArgumentException("Invalid or duplicate grant state.");
        foreach (PluginGrantSnapshot grant in state.Grants)
        {
            RequireIdentity(grant.FilterKey); RequireIdentity(grant.Scope.SourceId);
            if (grant.Scope.BoardId is not null) RequireIdentity(grant.Scope.BoardId);
            if (grant.Scope.RuleId is not null) RequireIdentity(grant.Scope.RuleId);
            if ((grant.Access & ~(FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage)) != 0)
                throw new ArgumentException("Unknown grant rights.");
        }
    }

    private sealed record Envelope(int Version, string Sha256, byte[] Payload);
}
