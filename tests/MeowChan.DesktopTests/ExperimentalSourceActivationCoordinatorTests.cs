using MeowChan.Desktop;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

public sealed class ExperimentalSourceActivationCoordinatorTests
{
    [Fact]
    public async Task StrictExperimentalActivationSavesEnableWithoutGrantOrEffectiveException()
    {
        using Fixture f = await Fixture.CreateAsync(); f.RequestPermission = false;
        var target = (await f.ObserveAsync()).Target!;
        var result = await f.ExecuteAsync(target);
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, result.Status);
        Assert.True(result.ActualState!.IsEnabled); Assert.True(result.ActualState.IsActive);
        Assert.False(result.ActualState.IsGranted); Assert.False(result.ActualState.IsEffective);
        Assert.Equal(target.PolicyRevision + 1, result.ActualState.PolicyRevision);
        Assert.Equal(1, f.EnableCalls); Assert.Equal(0, f.GrantCalls);
        Assert.Empty(Config.LoadConfig(f.Profile.Path).SourceMediaIntegrityPermissions);
    }

    [Fact]
    public async Task StrictActivationFailureCanRetryEnableWithoutEverRequestingPermission()
    {
        using Fixture f = await Fixture.CreateAsync(); f.RequestPermission = false; f.FailEnable = true;
        var failed = await f.ExecuteAsync((await f.ObserveAsync()).Target!);
        Assert.Equal(ExperimentalSourceActivationStatus.ActivationFailed, failed.Status);
        Assert.False(failed.RetryTarget!.RequestsMismatchPermission);
        f.FailEnable = false;
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, (await f.ExecuteAsync(failed.RetryTarget)).Status);
        Assert.Equal(0, f.GrantCalls); Assert.Empty(Config.LoadConfig(f.Profile.Path).SourceMediaIntegrityPermissions);
    }

    [Fact]
    public async Task StrictActivationCallbackFailureRetainsEnableAndRetryNeverAddsAGrant()
    {
        using Fixture f = await Fixture.CreateAsync(); f.RequestPermission = false; f.ThrowAfterLifecycle = true;
        var failed = await f.ExecuteAsync((await f.ObserveAsync()).Target!);
        Assert.Equal(ExperimentalSourceActivationStatus.ActivationFailed, failed.Status);
        Assert.True(failed.ActualState!.IsEnabled); Assert.False(failed.RetryTarget!.RequestsMismatchPermission);
        f.ThrowAfterLifecycle = false;
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, (await f.ExecuteAsync(failed.RetryTarget)).Status);
        Assert.Equal(1, f.EnableCalls); Assert.Equal(0, f.GrantCalls);
    }

    [Fact]
    public async Task ChangingTheFrozenPermissionIntentCannotGrantOrEnable()
    {
        using Fixture f = await Fixture.CreateAsync(); f.RequestPermission = false;
        var target = (await f.ObserveAsync()).Target! with { RequestsMismatchPermission = true };
        Assert.Equal(ExperimentalSourceActivationStatus.StaleTarget, (await f.ExecuteAsync(target)).Status);
        Assert.Equal(0, f.EnableCalls); Assert.Equal(0, f.GrantCalls);
    }

    [Fact]
    public async Task EnableUsesNewPolicyRevisionAndPersistsOnlyTheConfirmedSource()
    {
        using Fixture f = await Fixture.CreateAsync();
        var target = (await f.ObserveAsync()).Target!;
        var result = await f.ExecuteAsync(target).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, result.Status);
        Assert.True(result.ActualState!.IsEnabled);
        Assert.True(result.ActualState.IsActive);
        Assert.True(result.ActualState.IsEffective);
        Assert.Equal(target.PolicyRevision + 1, f.GrantRevision);
        Assert.Equal(target.PolicyRevision + 2, result.ActualState.PolicyRevision);
        var saved = Config.LoadConfig(f.Profile.Path);
        var permission = Assert.Single(saved.SourceMediaIntegrityPermissions).Value;
        Assert.Equal(Fixture.Owner, permission.PluginOwnerId);
        Assert.Equal(Fixture.Source, permission.SourceId);
        Assert.True(permission.AllowProviderSizeMd5Mismatch);
        Assert.Equal(1, f.EnableCalls);
        Assert.Equal(1, f.GrantCalls);
    }

    [Fact]
    public async Task GrantFailureLeavesDurableEnableAndExplicitRetryRunsOnlyMissingStep()
    {
        using Fixture f = await Fixture.CreateAsync();
        f.FailGrant = true;
        var result = await f.ExecuteAsync((await f.ObserveAsync()).Target!);
        Assert.Equal(ExperimentalSourceActivationStatus.GrantFailed, result.Status);
        Assert.True(result.ActualState!.IsEnabled);
        Assert.True(result.ActualState.IsActive);
        Assert.False(result.ActualState.IsGranted);
        Assert.False(result.ActualState.IsEffective);
        Assert.True((await new PluginProfileStore(f.Profile.Path).ReadAsync()).Plugins[0].Enabled);
        Assert.Empty(Config.LoadConfig(f.Profile.Path).SourceMediaIntegrityPermissions);
        Assert.Equal(1, f.GrantCalls); // No automatic retry.
        f.FailGrant = false;
        result = await f.ExecuteAsync(Assert.IsType<ExperimentalSourceActivationTarget>(result.RetryTarget));
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, result.Status);
        Assert.Equal(1, f.EnableCalls);
        Assert.Equal(2, f.GrantCalls);
    }

    [Fact]
    public async Task CallbackExceptionAfterDurableEnableReportsActualStateAndCanExplicitlyRetryGrant()
    {
        using Fixture f = await Fixture.CreateAsync();
        f.ThrowAfterLifecycle = true;
        var result = await f.ExecuteAsync((await f.ObserveAsync()).Target!);
        Assert.Equal(ExperimentalSourceActivationStatus.ActivationFailed, result.Status);
        Assert.True(result.ActualState!.IsEnabled);
        Assert.True(result.ActualState.IsActive);
        Assert.False(result.ActualState.IsGranted);
        Assert.True((await new PluginProfileStore(f.Profile.Path).ReadAsync()).Plugins[0].Enabled);
        Assert.Equal(0, f.GrantCalls);
        f.ThrowAfterLifecycle = false;
        result = await f.ExecuteAsync(result.RetryTarget!);
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, result.Status);
        Assert.Equal(1, f.EnableCalls);
    }

    [Fact]
    public async Task FailedEnableDoesNotGrantAndCanBeExplicitlyRepeated()
    {
        using Fixture f = await Fixture.CreateAsync();
        f.FailEnable = true;
        var result = await f.ExecuteAsync((await f.ObserveAsync()).Target!);
        Assert.Equal(ExperimentalSourceActivationStatus.ActivationFailed, result.Status);
        Assert.False(result.ActualState!.IsEnabled);
        Assert.False(result.ActualState.IsActive);
        Assert.Equal(0, f.GrantCalls);
        f.FailEnable = false;
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, (await f.ExecuteAsync(result.RetryTarget!)).Status);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("profile")]
    [InlineData("owner")]
    [InlineData("source")]
    [InlineData("candidate")]
    [InlineData("build")]
    [InlineData("profileRevision")]
    [InlineData("lifecycleRevision")]
    [InlineData("policyRevision")]
    public async Task StaleOrDifferentAuthorityNeverMutates(string change)
    {
        using Fixture f = await Fixture.CreateAsync();
        var target = (await f.ObserveAsync()).Target!;
        target = change switch
        {
            "session" => target with { SessionId = Guid.NewGuid() },
            "profile" => target with { ProfileId = "another-profile" },
            "owner" => target with { PluginOwnerId = "another-owner" },
            "source" => target with { SourceId = "another-source" },
            "candidate" => target with { CandidateId = "other.cs" },
            "build" => target with { ExecutionRevision = "other-build" },
            "profileRevision" => target with { ProfileRevision = target.ProfileRevision + 1 },
            "lifecycleRevision" => target with { LifecycleRevision = target.LifecycleRevision + 1 },
            _ => target with { PolicyRevision = target.PolicyRevision + 1 }
        };
        Assert.Equal(ExperimentalSourceActivationStatus.StaleTarget, (await f.ExecuteAsync(target)).Status);
        Assert.Equal(0, f.EnableCalls);
        Assert.Equal(0, f.GrantCalls);
    }

    [Fact]
    public async Task ExistingSavedGrantNeedsNoSecondGrantWriteAfterEnable()
    {
        using Fixture f = await Fixture.CreateAsync(savedGrant: true);
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, (await f.ExecuteAsync((await f.ObserveAsync()).Target!)).Status);
        Assert.Equal(0, f.GrantCalls);
        Assert.True(f.Policy.GetSnapshot().AllowProviderSizeMd5Mismatch);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("revoke")]
    [InlineData("owner")]
    [InlineData("shutdown")]
    public async Task InterveningChangeAfterEnableIsNotAcceptedAsTheNewGrantRevision(string change)
    {
        using Fixture f = await Fixture.CreateAsync(savedGrant: change == "revoke");
        f.AfterEnable = async () =>
        {
            if (change == "disable")
            {
                var saved = await f.Store.ReadAsync();
                await f.Store.SetEnabledAsync(Fixture.Owner, false, saved.Revision);
            }
            else if (change == "revoke") await f.Policy.SetPermissionAsync(false, f.Policy.GetSnapshot().Revision, _ => { });
            else if (change == "owner") f.OverrideOwner = "another-owner";
            else { f.Ready = false; f.Policy.Retire(); }
        };
        var result = await f.ExecuteAsync((await f.ObserveAsync()).Target!);
        Assert.Equal(ExperimentalSourceActivationStatus.StaleTarget, result.Status);
        Assert.Null(result.RetryTarget);
        Assert.Equal(0, f.GrantCalls);
        Assert.False(f.Policy.GetSnapshot().AllowProviderSizeMd5Mismatch);
    }

    [Fact]
    public async Task StoreConflictAfterValidationIsStaleAndDoesNotOfferSilentRetargeting()
    {
        using Fixture f = await Fixture.CreateAsync();
        f.BeforeEnable = async () =>
        {
            var saved = await f.Store.ReadAsync();
            await f.Store.SetEnabledAsync(Fixture.Owner, false, saved.Revision);
        };
        var result = await f.ExecuteAsync((await f.ObserveAsync()).Target!);
        Assert.Equal(ExperimentalSourceActivationStatus.StaleTarget, result.Status);
        Assert.Null(result.RetryTarget);
        Assert.False(result.ActualState!.IsEnabled);
        Assert.Equal(0, f.GrantCalls);
    }

    [Fact]
    public async Task DisableImmediatelyBeforeGrantSaveCannotAcquireNewConsent()
    {
        using Fixture f = await Fixture.CreateAsync();
        f.BeforeGrant = async () =>
        {
            var saved = await f.Store.ReadAsync();
            await f.Store.SetEnabledAsync(Fixture.Owner, false, saved.Revision);
        };
        var result = await f.ExecuteAsync((await f.ObserveAsync()).Target!);
        Assert.Equal(ExperimentalSourceActivationStatus.StaleTarget, result.Status);
        Assert.False(result.ActualState!.IsEnabled);
        Assert.False(result.ActualState.IsGranted);
        Assert.Empty(Config.LoadConfig(f.Profile.Path).SourceMediaIntegrityPermissions);
    }

    [Fact]
    public async Task EarlierLeaseDoesNotDeadlockEnableAndGrantAndLaterRevocationRetainsItsGuard()
    {
        using Fixture f = await Fixture.CreateAsync(savedGrant: true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        f.Policy.SetActive(true);
        var lease = await f.Policy.AcquireAcceptanceLeaseAsync(f.Policy.GetSnapshot().Revision, timeout.Token);
        f.Policy.SetActive(false); // An earlier linearized publication may still own its lease.
        var target = (await f.ObserveAsync()).Target!;
        var result = await f.ExecuteAsync(target).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, result.Status);
        Assert.Equal(0, f.GrantCalls); // Existing consent needs no lease-gated second write.
        Task revoke = f.Policy.SetPermissionAsync(false, result.ActualState!.PolicyRevision, _ => { });
        Assert.False(revoke.IsCompleted);
        await lease.DisposeAsync();
        await revoke.WaitAsync(timeout.Token);
        Assert.False(f.Policy.GetSnapshot().AllowProviderSizeMd5Mismatch);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Policy.AcquireAcceptanceLeaseAsync(result.ActualState.PolicyRevision, default).AsTask());
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Owner = "meowchan.source.kohlchan", Source = "kohlchan";
        internal TempDirectory Profile { get; } = new();
        internal PluginProfileStore Store { get; }
        internal SourceMediaIntegrityPolicyService Policy { get; }
        private readonly Guid session = Guid.NewGuid();
        private readonly Config config = new();
        internal bool FailEnable, FailGrant, ThrowAfterLifecycle, Ready = true, RequestPermission = true;
        internal string OverrideOwner = Owner;
        internal int EnableCalls, GrantCalls;
        internal long GrantRevision;
        internal Func<Task>? AfterEnable, BeforeEnable, BeforeGrant;

        private Fixture(bool savedGrant)
        {
            Store = new(Profile.Path);
            Policy = new(Profile.Path, Owner, Source, savedGrant ? new(Owner, Source, true, 4) : null);
            if (savedGrant) config.SourceMediaIntegrityPermissions[SourceMediaIntegrityPermission.CreateKey(Owner, Source)] = new(Owner, Source, true, 4);
            config.SaveConfig(Profile.Path);
        }
        internal static async Task<Fixture> CreateAsync(bool savedGrant = false)
        {
            Fixture f = new(savedGrant);
            SettingsSchema schema = new("1", []);
            PluginDescription description = new(Owner, "Fixture", "1.0", SdkContract.Version, "Fixture", [],
                [new("dimensions", "Dimensions", "Fixture", FilterDataAccess.CandidateMetadata, schema, ["image/png"])], schema, []);
            PluginArtifact artifact = new("code-a", "execution-a", "/fixture/a.dll", new string('a', 64), "sdk", "compiler", []);
            var saved = await f.Store.ApplyStartupAsync([new("fixture.cs", Owner, artifact, description)], 0);
            await f.Store.SetEnabledAsync(Owner, false, saved.Revision);
            f.Store.LifecycleChanged += change =>
            {
                f.Policy.SetActive(change.Enabled);
                if (f.ThrowAfterLifecycle) throw new IOException("Lifecycle callback failed after save.");
            };
            return f;
        }
        internal async Task<ExperimentalSourceActivationObservation> ObserveAsync()
        {
            var saved = await Store.ReadAsync();
            var plugin = Assert.Single(saved.Plugins);
            var view = Policy.GetView();
            var target = new ExperimentalSourceActivationTarget(session, Profile.Path, OverrideOwner, Source, "Kohlchan",
                "https://kohlchan.net/demo/res/123.html", plugin.Active!.CandidateId, plugin.Active.ExecutionRevision,
                plugin.Active.CompilationFingerprint, plugin.Active.Artifact.AssemblySha256,
                saved.Revision, plugin.LifecycleRevision, view.Snapshot.Revision, plugin.Enabled, view.IsGranted, RequestPermission);
            return new(target, new(plugin.Enabled, view.IsActive, view.IsGranted, view.Snapshot.AllowProviderSizeMd5Mismatch,
                saved.Revision, plugin.LifecycleRevision, view.Snapshot.Revision), Ready);
        }
        internal Task<ExperimentalSourceActivationResult> ExecuteAsync(ExperimentalSourceActivationTarget target) =>
            ExperimentalSourceActivationCoordinator.ExecuteAsync(target, ObserveAsync, async current =>
            {
                EnableCalls++;
                if (FailEnable) throw new IOException("Enable save failed.");
                if (BeforeEnable != null) await BeforeEnable();
                await Store.SetEnabledAsync(current.PluginOwnerId, true, current.ProfileRevision);
                if (AfterEnable != null) await AfterEnable();
            }, async current =>
            {
                GrantCalls++; GrantRevision = current.PolicyRevision;
                if (BeforeGrant != null) await BeforeGrant();
                await Policy.SetPermissionAsync(true, current.PolicyRevision, permission =>
                {
                    if (FailGrant) throw new IOException("Grant save failed.");
                    Config candidate = config.Clone();
                    candidate.SourceMediaIntegrityPermissions[SourceMediaIntegrityPermission.CreateKey(Owner, Source)] = permission;
                    candidate.SaveConfig(Profile.Path); config.CopyFrom(candidate);
                });
            });
        public void Dispose() => Profile.Dispose();
    }
}
