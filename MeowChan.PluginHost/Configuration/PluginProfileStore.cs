using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration;

/// <summary>
/// Profile-owned atomic store. A separate stable lock file serializes readers and writers across processes;
/// the revision is rechecked under that lock. Corrupt state is never replaced with defaults.
/// </summary>
public sealed class PluginProfileStore : IPluginProfileStore
{
    private const int FormatVersion = 1;
    private const int DeliveredFormatVersion = 2;
    private const int MaximumStoreBytes = 24 * 1024 * 1024;
    private readonly string directory;
    private readonly string path;
    private readonly string lockPath;
    private readonly Func<string, bool> initiallyEnabled;
    private readonly Func<IReadOnlyList<string>>? repairRestrictedSources;

    public PluginProfileStore(string profileDirectory) : this(profileDirectory, static _ => true) { }

    /// <summary>
    /// Uses the host's default only when a plugin identity is first recorded. Saved enable choices
    /// remain authoritative; resolving a provisional owner cannot bypass a disabled default.
    /// </summary>
    public PluginProfileStore(string profileDirectory, Func<string, bool> initiallyEnabled, Func<IReadOnlyList<string>>? repairRestrictedSources = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        ArgumentNullException.ThrowIfNull(initiallyEnabled);
        this.initiallyEnabled = initiallyEnabled;
        this.repairRestrictedSources = repairRestrictedSources;
        directory = Path.Combine(Path.GetFullPath(profileDirectory), "plugin-state");
        path = Path.Combine(directory, "state.json");
        lockPath = Path.Combine(directory, "state.lock");
    }

    public string StorePath => path;
    public event Action<PluginLifecycleChange>? LifecycleChanged;
    public event Action<PluginGrantChange>? GrantChanged;

    public async Task<PluginProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        // ReplaceFile can briefly hide the destination name on Windows. Read under the
        // same stable lease so a replacement cannot be mistaken for a new empty profile.
        await using FileStream lease = await AcquireLockAsync(cancellationToken, FileAccess.Read).ConfigureAwait(false);
        return await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<PluginProfileSnapshot> ObserveCandidateAsync(PluginCandidateObservation candidate, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        candidate = Clone(candidate);
        ValidateObservation(candidate);
        return MutateAsync(expectedRevision, state =>
        {
            state = ReleaseProvisionalOwners(state, [candidate]);
            PluginActivationSnapshot plugin = FindOrCreate(state, candidate.PluginId);
            List<PluginCandidateSnapshot> candidates = plugin.Candidates.Where(item => item.CandidateId != candidate.CandidateId).ToList();
            if (state.Plugins.Any(item => item.PluginId != candidate.PluginId && item.Candidates.Any(existing => existing.CandidateId == candidate.CandidateId)))
                throw new ArgumentException("A candidate identity cannot move to another plugin implicitly.");
            candidates.Add(ToSnapshot(candidate));
            return Replace(state, plugin with { Candidates = candidates });
        }, cancellationToken);
    }

    public Task<PluginProfileSnapshot> ApplyStartupAsync(IReadOnlyList<PluginCandidateObservation> candidates, long expectedRevision,
        CancellationToken cancellationToken = default) => ApplyStartupCoreAsync(candidates, null, null, expectedRevision, cancellationToken);

    public Task<PluginProfileSnapshot> ApplyBundledStartupAsync(IReadOnlyList<PluginCandidateObservation> candidates,
        IReadOnlyList<DeliveredCandidateProvenance> delivered, IReadOnlyList<string> restrictedSourceIds,
        long expectedRevision, CancellationToken cancellationToken = default) =>
        ApplyStartupCoreAsync(candidates, delivered, restrictedSourceIds, expectedRevision, cancellationToken);

    private Task<PluginProfileSnapshot> ApplyStartupCoreAsync(IReadOnlyList<PluginCandidateObservation> candidates,
        IReadOnlyList<DeliveredCandidateProvenance>? delivered, IReadOnlyList<string>? restrictedSourceIds,
        long expectedRevision, CancellationToken cancellationToken)
    {
        PluginCandidateObservation[] observations = Clone(candidates.ToArray());
        DeliveredCandidateProvenance[]? proofs = delivered is null ? null : Clone(delivered.ToArray());
        string[]? restricted = restrictedSourceIds?.ToArray();
        foreach (PluginCandidateObservation candidate in observations) ValidateObservation(candidate);
        if (observations.Select(item => item.CandidateId).Distinct(StringComparer.Ordinal).Count() != observations.Length)
            throw new ArgumentException("Startup inventory contains duplicate candidate identities.");
        return MutateDocumentAsync(expectedRevision, document =>
        {
            if (proofs is not null)
            {
                if (proofs.Select(p => p.CandidateId).Distinct(StringComparer.Ordinal).Count() != proofs.Length ||
                    proofs.Any(proof => !observations.Any(c => c.Problem is null && BundledCandidateMigration.Matches(proof, ToSnapshot(c)))))
                    throw new ArgumentException("Delivered evidence does not match the successful startup inventory.");
                List<DeliveredCandidateProvenance> evidence = document.Provenance.ToList();
                foreach (DeliveredCandidateProvenance proof in proofs)
                {
                    DeliveredCandidateProvenance? known = evidence.FirstOrDefault(p => p.CandidateId == proof.CandidateId && p.ExecutionRevision == proof.ExecutionRevision);
                    if (known is not null && known != proof) throw new InvalidDataException("Delivered provenance changed for a retained identity.");
                    if (known is null) evidence.Add(proof);
                }
                document = document with { Provenance = evidence, RestrictedSourceIds = restricted ?? [] };
                BundledCandidateMigration.Validate(document);
            }
            PluginProfileSnapshot state = document.Profile;
            state = ReleaseProvisionalOwners(state, observations);
            // A source file changing its declared plugin identity is an unavailable update of
            // that existing candidate, not permission to transfer its configuration or rights.
            Dictionary<string, string> candidateOwners = state.Plugins.SelectMany(plugin => plugin.Candidates
                .Select(candidate => (candidate.CandidateId, plugin.PluginId)))
                .ToDictionary(item => item.CandidateId, item => item.PluginId, StringComparer.Ordinal);
            PluginCandidateObservation[] startup = observations.Select(candidate =>
                candidateOwners.TryGetValue(candidate.CandidateId, out string? owner) && owner != candidate.PluginId
                    ? new PluginCandidateObservation(candidate.CandidateId, owner, null, null,
                        $"Candidate plugin identity changed from '{owner}' to '{candidate.PluginId}'. Install it as a separate candidate to select the new identity explicitly.", candidate.CandidatePath)
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
                    BundledRepairBinding? binding = document.Bindings.FirstOrDefault(b => b.PluginId == pluginId && b.SelectedCandidateId == selected);
                    string effectiveCandidate = binding?.TargetCandidateId ?? selected;
                    PluginCandidateSnapshot? candidate = inventory.FirstOrDefault(item => item.CandidateId == effectiveCandidate);
                    PreReplacementBasis previous = BundledCandidateMigration.TrustedPreReplacementBasis(document, old);
                    bool buildChanged = candidate?.Artifact is not null && previous.HasHistory &&
                        (previous.Missing || previous.ExecutionRevision != candidate.Artifact.ExecutionRevision);
                    bool deliveredCandidate = candidate is not null && document.Provenance.Any(p => BundledCandidateMigration.Matches(p, candidate));
                    bool deliveredIdentity = candidate is not null && (binding is not null || previous.Provenance is not null ||
                        document.Provenance.Any(p => p.CandidateId == candidate.CandidateId));
                    // An explicitly selected same-code local Pending may relocate established code,
                    // but may not attest a first activation, claim delivered provenance or mask material changes.
                    bool selectedLocalAlias = candidate is not null && old.SelectedCandidateId == candidate.CandidateId &&
                        old.Pending is { } pendingAlias && pendingAlias.CandidateId == candidate.CandidateId &&
                        BundledCandidateMigration.SameExecutableAndDescription(pendingAlias, candidate) &&
                        BundledCandidateMigration.IdenticalEstablishedLocalAlias(document, old, candidate, IsProfileLocal(candidate)) is not null;
                    bool bindingMatches = binding is not null && !previous.Missing && previous.Provenance?.SlotId == binding.SlotId &&
                        candidate?.Artifact?.ExecutionRevision == binding.TargetExecutionRevision &&
                        document.Provenance.Any(p => p.SlotId == binding.SlotId && BundledCandidateMigration.Matches(p, candidate));
                    if (candidate is null || !candidate.IsPresent || candidate.Problem is not null || candidate.Artifact is null || candidate.Description is null)
                        next = next with { Problem = candidate?.Problem ?? "Selected candidate is unavailable." };
                    else if (binding is not null && !bindingMatches)
                        next = next with { Problem = "Confirmed bundled target changed or disappeared; review repair again." };
                    else if (deliveredIdentity && !deliveredCandidate && !selectedLocalAlias)
                        next = next with { Problem = "Bundled target has no matching delivered provenance; review is required." };
                    else if (deliveredIdentity && previous.Missing)
                        next = next with { Problem = "Bundled replacement has no unambiguous trusted pre-replacement basis; review is required." };
                    else if (deliveredCandidate && buildChanged && !bindingMatches)
                        next = next with { Problem = "Bundled executable changed; explicit repair review is required." };
                    else if (deliveredCandidate && BundledCandidateMigration.HasInheritedRights(document, old, candidate, buildChanged))
                        next = next with { Problem = "Bundled replacement is blocked: existing grants cannot be inherited." };
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
            return Task.FromResult(document with { Profile = state with { Plugins = plugins } });
        }, cancellationToken);
    }

    public async Task<BundledCandidateRepairPreview> PreviewBundledRepairsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory)) return new(0, []);
        await using FileStream lease = await AcquireLockAsync(cancellationToken, FileAccess.Read).ConfigureAwait(false);
        return BundledCandidateMigration.Preview(WithCurrentRepairRestrictions(await ReadDocumentCoreAsync(cancellationToken).ConfigureAwait(false)));
    }

    public Task<PluginProfileSnapshot> ConfirmBundledRepairAsync(BundledCandidateRepair repair, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repair);
        return MutateDocumentAsync(expectedRevision, async document =>
        {
            document = WithCurrentRepairRestrictions(document);
            BundledCandidateRepair? current = BundledCandidateMigration.Preview(document).Rows
                .SingleOrDefault(row => row.PluginId == repair.PluginId && row.OldCandidateId == repair.OldCandidateId);
            if (current is null || current != repair || !current.CanConfirm)
                throw new InvalidOperationException("Bundled repair is blocked or the preview changed. Review it again.");
            PluginActivationSnapshot plugin = Find(document.Profile, current.PluginId);
            PluginCandidateSnapshot candidate = AvailableCandidate(plugin, current.NewCandidateId!);
            DeliveredCandidateProvenance proof = document.Provenance.Single(p => p.SlotId == current.SlotId &&
                BundledCandidateMigration.Matches(p, candidate));
            // Revalidate both delivered bytes and immutable cache before committing the exact reviewed build.
            await PluginInstallationInventory.VerifyRepairTargetAsync(proof, candidate.Artifact!, candidate.Description!, cancellationToken).ConfigureAwait(false);
            document = WithCurrentRepairRestrictions(document);
            if (BundledCandidateMigration.HasInheritedRights(document, plugin, candidate, current.BuildChanged))
                throw new InvalidOperationException("Bundled replacement would inherit permissions; repair is blocked.");
            PreReplacementBasis previous = BundledCandidateMigration.TrustedPreReplacementBasis(document, plugin);
            PluginReleaseSnapshot basis = previous.Release ?? throw new InvalidOperationException("No trusted pre-replacement configuration is retained.");
            PluginReleaseSnapshot pending = new(candidate.CandidateId, candidate.Artifact!, candidate.Description!, basis.Configuration);
            BundledRepairBinding binding = new(plugin.PluginId, plugin.SelectedCandidateId!, basis.ExecutionRevision,
                candidate.CandidateId, candidate.Artifact!.ExecutionRevision, proof.SlotId)
                { OriginalRelease = Clone(basis) };
            // Keep the user's original selection as the anchor. Only this explicit, versioned binding
            // selects a different execution at next startup. No owner/grant/pin/config rewrite.
            return document with
            {
                Profile = Replace(document.Profile, plugin with { Pending = pending,
                    Problem = "Confirmed bundled repair saved for next start; original selection and rollback retained." }),
                Bindings = document.Bindings.Where(b => b.PluginId != plugin.PluginId).Append(binding).ToArray()
            };
        }, cancellationToken);
    }

    private BundledProfileDocument WithCurrentRepairRestrictions(BundledProfileDocument document) => repairRestrictedSources is null
        ? document : document with { RestrictedSourceIds = document.RestrictedSourceIds.Concat(repairRestrictedSources())
            .Distinct(StringComparer.Ordinal).ToArray() };

    private bool IsProfileLocal(PluginCandidateSnapshot candidate)
    {
        if (candidate.CandidatePath is not { } path || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            string local = Path.Combine(Path.GetDirectoryName(directory)!, "plugins");
            return string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), local,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private void RequireOrdinaryCandidateSelection(BundledProfileDocument document,
        PluginActivationSnapshot plugin, PluginCandidateSnapshot candidate)
    {
        PreReplacementBasis basis = BundledCandidateMigration.TrustedPreReplacementBasis(document, plugin);
        bool selectedDelivered = document.Provenance.Any(p => p.CandidateId == plugin.SelectedCandidateId);
        if (basis.Provenance is null && !selectedDelivered && !document.Bindings.Any(b => b.PluginId == plugin.PluginId) &&
            !document.Provenance.Any(p => p.CandidateId == candidate.CandidateId)) return;
        if (BundledCandidateMigration.IdenticalEstablishedLocalAlias(document, plugin, candidate, IsProfileLocal(candidate)) is not null) return;
        if (!basis.HasHistory) return;
        if (basis.Missing || basis.CandidateId != candidate.CandidateId || basis.ExecutionRevision != candidate.Artifact!.ExecutionRevision)
            throw new InvalidOperationException("Use bundled repair preview and confirmation; ordinary candidate selection cannot bypass provenance or grants.");
    }

    public Task<PluginProfileSnapshot> SelectCandidateAsync(string pluginId, string candidateId, long expectedRevision,
        CancellationToken cancellationToken = default) => MutateDocumentAsync(expectedRevision, document =>
    {
        PluginProfileSnapshot state = document.Profile;
        PluginActivationSnapshot plugin = Find(state, pluginId);
        PluginCandidateSnapshot candidate = AvailableCandidate(plugin, candidateId);
        RequireOrdinaryCandidateSelection(document, plugin, candidate);
        PluginReleaseSnapshot? sameCodeAlias = BundledCandidateMigration.IdenticalEstablishedLocalAlias(document, plugin, candidate, IsProfileLocal(candidate));
        ConfigurationMigrationResult migration = PluginSettingsMigration.Migrate(MatchingBasis(plugin, candidateId) ?? sameCodeAlias, candidate.Description!);
        // Selecting a candidate with incompatible settings records the choice, retaining the old values for explicit correction.
        PluginReleaseSnapshot? pending = migration.Configuration is null ? null : new(candidateId, candidate.Artifact!, candidate.Description!, migration.Configuration);
        Dictionary<string, string> selectedSources = new(state.SelectedSourceOwners, StringComparer.Ordinal);
        foreach (SourceDescription source in candidate.Description!.Sources) selectedSources[source.SourceId] = pluginId;
        PluginProfileSnapshot next = Replace(state, plugin with { SelectedCandidateId = candidateId, Pinned = null, Pending = pending,
            Problem = migration.Configuration is null ? "Selected candidate needs settings correction: " + string.Join("; ", migration.Issues.Select(issue => issue.Message)) : null })
            with { SelectedSourceOwners = selectedSources };
        return Task.FromResult(document with { Profile = next, Bindings = document.Bindings.Where(b => b.PluginId != pluginId).ToArray() });
    }, cancellationToken);

    public Task<PluginProfileSnapshot> SaveConfigurationAsync(string pluginId, string candidateId, PluginConfiguration configuration,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        configuration = Clone(configuration);
        return MutateDocumentAsync(expectedRevision, document =>
        {
            PluginProfileSnapshot state = document.Profile;
            PluginActivationSnapshot plugin = Find(state, pluginId);
            PluginReleaseSnapshot? pinned = plugin.Pinned?.CandidateId == candidateId ? plugin.Pinned : null;
            PluginCandidateSnapshot? candidate = pinned is null ? AvailableCandidate(plugin, candidateId) : null;
            if (candidate is not null) RequireOrdinaryCandidateSelection(document, plugin, candidate);
            PluginDescription description = pinned?.Description ?? candidate!.Description!;
            ThrowValidation(SdkValidation.ValidateConfiguration(description, configuration));
            Dictionary<string, SettingsSnapshot> sources = description.Sources.ToDictionary(source => source.SourceId,
                source => PluginSettingsMigration.WithDefaults(source.Settings, configuration.SourceSettings[source.SourceId]), StringComparer.Ordinal);
            PluginConfiguration saved = new(Guid.NewGuid().ToString("N"),
                PluginSettingsMigration.WithDefaults(description.Settings, configuration.PluginSettings), sources);
            PluginReleaseSnapshot pending = new(candidateId, pinned?.Artifact ?? candidate!.Artifact!, description, saved);
            bool bound = document.Bindings.Any(b => b.PluginId == pluginId && b.SelectedCandidateId == plugin.SelectedCandidateId &&
                b.TargetCandidateId == candidateId && b.TargetExecutionRevision == pending.ExecutionRevision);
            return Task.FromResult(document with { Profile = Replace(state, plugin with
                { SelectedCandidateId = bound ? plugin.SelectedCandidateId : candidateId, Pending = pending, Problem = null }) });
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

    private Task<PluginProfileSnapshot> MutateAsync(long expectedRevision, Func<PluginProfileSnapshot, PluginProfileSnapshot> update,
        CancellationToken cancellationToken) => MutateDocumentAsync(expectedRevision,
            document => Task.FromResult(document with { Profile = update(document.Profile) }), cancellationToken);

    private async Task<PluginProfileSnapshot> MutateDocumentAsync(long expectedRevision,
        Func<BundledProfileDocument, Task<BundledProfileDocument>> update, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        await using FileStream lease = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        BundledProfileDocument document = await ReadDocumentCoreAsync(cancellationToken).ConfigureAwait(false);
        PluginProfileSnapshot state = document.Profile;
        if (state.Revision != expectedRevision) throw new PluginProfileConflictException(expectedRevision, state.Revision);
        BundledProfileDocument updated = await update(document).ConfigureAwait(false);
        PluginProfileSnapshot next = updated.Profile with { Revision = checked(state.Revision + 1) };
        updated = updated with { Profile = next };
        ValidateState(next);
        BundledCandidateMigration.Validate(updated);
        bool deliveredFormat = updated.Provenance.Count != 0 || updated.Bindings.Count != 0 || updated.RestrictedSourceIds.Count != 0;
        byte[] payload = deliveredFormat ? SdkJson.Serialize(updated) : SdkJson.Serialize(next);
        if (payload.Length > SdkLimits.MaximumMetadataBytes * 2) throw new IOException("Plugin profile payload exceeds its bounded store size.");
        Envelope envelope = new(deliveredFormat ? DeliveredFormatVersion : FormatVersion, Convert.ToHexString(SHA256.HashData(payload)), payload);
        byte[] bytes = SdkJson.Serialize(envelope);
        if (bytes.Length > MaximumStoreBytes) throw new IOException("Plugin profile state exceeds its bounded store size.");
        string temporary = Path.Combine(directory, ".state-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool preserveTemporary = false;
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Windows MoveFileEx can deny overwrite while a share-delete reader holds
            // the old revision open. ReplaceFile supports that reader and keeps its
            // complete snapshot alive while publishing the new revision atomically.
            // The stable writer lease prevents competing initial creates/replacements.
            if (File.Exists(path))
            {
                try { File.Replace(temporary, path, destinationBackupFileName: null); }
                catch (IOException exception) when (OperatingSystem.IsWindows()
                    && (exception.HResult == unchecked((int)0x80070498)
                        || exception.HResult == unchecked((int)0x80070499)))
                {
                    // ReplaceFile errors 1176/1177 can already have moved the old
                    // destination. Retain the complete new file for recovery and
                    // report the original error; never fall back to another write.
                    preserveTemporary = true;
                    throw;
                }
            }
            else File.Move(temporary, path);
        }
        finally { if (!preserveTemporary && File.Exists(temporary)) File.Delete(temporary); }
        return Clone(next);
    }

    private async Task<PluginProfileSnapshot> ReadCoreAsync(CancellationToken cancellationToken) =>
        (await ReadDocumentCoreAsync(cancellationToken).ConfigureAwait(false)).Profile;

    private async Task<BundledProfileDocument> ReadDocumentCoreAsync(CancellationToken cancellationToken)
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
            catch (FileNotFoundException) { return new(Empty(), [], [], []); }
            catch (DirectoryNotFoundException) { return new(Empty(), [], [], []); }
            Envelope envelope = JsonSerializer.Deserialize<Envelope>(bytes, SdkJson.CreateOptions()) ?? throw new JsonException("Null envelope.");
            if (envelope.Version is not (FormatVersion or DeliveredFormatVersion) || envelope.Payload is null ||
                !string.Equals(envelope.Sha256, Convert.ToHexString(SHA256.HashData(envelope.Payload)), StringComparison.Ordinal))
                throw new JsonException("Store version or integrity check failed.");
            BundledProfileDocument document = envelope.Version == FormatVersion
                ? new(SdkJson.Deserialize<PluginProfileSnapshot>(envelope.Payload), [], [], [])
                : SdkJson.Deserialize<BundledProfileDocument>(envelope.Payload);
            ValidateState(document.Profile);
            BundledCandidateMigration.Validate(document);
            return document;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or OverflowException or NullReferenceException or EndOfStreamException)
        { throw new PluginProfileCorruptException(path, exception); }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken, FileAccess access = FileAccess.ReadWrite)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new(lockPath, FileMode.OpenOrCreate, access, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(20, cancellationToken).ConfigureAwait(false); }
        }
    }

    private static PluginProfileSnapshot Empty() => new(0, Array.Empty<PluginActivationSnapshot>(), Array.Empty<PluginGrantSnapshot>());
    private static T Clone<T>(T value) => SdkJson.Deserialize<T>(SdkJson.Serialize(value));
    private static PluginActivationSnapshot Find(PluginProfileSnapshot state, string pluginId) => state.Plugins.SingleOrDefault(plugin => plugin.PluginId == pluginId)
        ?? throw new KeyNotFoundException("Unknown plugin: " + pluginId);
    private PluginActivationSnapshot FindOrCreate(PluginProfileSnapshot state, string pluginId) => state.Plugins.SingleOrDefault(plugin => plugin.PluginId == pluginId)
        ?? new(pluginId, initiallyEnabled(pluginId), 0, null, null, null, null, null, Array.Empty<PluginCandidateSnapshot>(), null);
    private static PluginProfileSnapshot Replace(PluginProfileSnapshot state, PluginActivationSnapshot plugin) => state with
    { Plugins = state.Plugins.Where(item => item.PluginId != plugin.PluginId).Append(plugin).ToArray() };
    private static PluginCandidateSnapshot ToSnapshot(PluginCandidateObservation candidate) => new(candidate.CandidateId, candidate.PluginId,
        candidate.Artifact, candidate.Description, true, candidate.Problem, candidate.CandidatePath);
    private PluginProfileSnapshot ReleaseProvisionalOwners(PluginProfileSnapshot state, IReadOnlyList<PluginCandidateObservation> observations)
    {
        List<PluginActivationSnapshot> plugins = new();
        Dictionary<string, PluginActivationSnapshot> discovered = new(StringComparer.Ordinal);
        foreach (PluginActivationSnapshot plugin in state.Plugins)
        {
            bool provisional = plugin.Active is null && plugin.Pending is null && plugin.LastGood is null && plugin.Pinned is null
                && plugin.Candidates.All(candidate => candidate.Description is null)
                && !state.Grants.Any(grant => grant.PluginId == plugin.PluginId)
                && !state.SelectedSourceOwners.Values.Contains(plugin.PluginId);
            if (!provisional) { plugins.Add(plugin); continue; }
            foreach (PluginCandidateObservation observation in observations.Where(observation => observation.Description is not null
                && observation.PluginId != plugin.PluginId && plugin.Candidates.Any(candidate => candidate.CandidateId == observation.CandidateId)))
                if (!state.Plugins.Any(existing => existing.PluginId == observation.PluginId) && !discovered.ContainsKey(observation.PluginId))
                {
                    // A loader placeholder's enabled state is not consent for the newly described identity.
                    // Retain an explicit disable while applying that identity's first-recording default.
                    PluginActivationSnapshot resolved = FindOrCreate(state, observation.PluginId);
                    discovered.Add(observation.PluginId, resolved with
                    { Enabled = plugin.Enabled && resolved.Enabled, LifecycleRevision = plugin.LifecycleRevision });
                }
            PluginCandidateSnapshot[] retained = plugin.Candidates.Where(candidate => !observations.Any(observation =>
                observation.CandidateId == candidate.CandidateId && observation.PluginId != plugin.PluginId && observation.Description is not null)).ToArray();
            if (retained.Length != 0) plugins.Add(plugin with { Candidates = retained });
        }
        plugins.AddRange(discovered.Values);
        return state with { Plugins = plugins };
    }
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
