using System.Reflection;
using MeowChan.Desktop;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Contracts;

namespace MeowChan.DesktopSmoke;

internal static partial class PluginManagementChecks
{
    /// <summary>
    /// The management form fixture owns a separate manager, not a PluginSession. Exercise the
    /// real window session independently; never relabel that fixture's clients as session-owned.
    /// </summary>
    private static async Task CheckSessionProductionObservationAsync(MainWindow window)
    {
        PluginSession session = window.SessionHost.Plugins
            ?? throw new InvalidOperationException("Production observation requires the real desktop PluginSession.");
        var profile = await session.ProfileStore.ReadAsync();
        var activation = profile.Plugins.FirstOrDefault(plugin => plugin.Enabled &&
            plugin.Active is { Description.Sources.Count: > 0 })
            ?? throw new InvalidOperationException("No active source build is available for the session observation.");
        var release = activation.Active!;
        var management = new PluginManagementServices(session.ProfileStore, session.CompilationService,
            session.Inventory, session.PluginDirectory, session.DescribeTestAsync, session.StartTestWorkerAsync,
            PluginProbe.ReadAsync)
        {
            GetProductionWorkerSnapshots = session.GetProductionWorkerSnapshots
        };

        // This existing lazy-start path establishes the actual session's _clients ownership.
        // Reflection is confined to the harness: no new product accessor, injected dictionary
        // entry, source operation, HTTP probe, watch, rule or persisted profile change is needed.
        MethodInfo start = typeof(PluginSession).GetMethod("GetWorkerAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(PluginSession), "GetWorkerAsync");
        var starting = (Task<IPluginWorkerClient>)(start.Invoke(session,
            new object[] { activation.PluginId, CancellationToken.None })
            ?? throw new InvalidOperationException("The production session did not return its worker task."));
        IPluginWorkerClient production = await starting.WaitAsync(TimeSpan.FromSeconds(30));
        // The session owns production; the harness must not dispose it.
        PluginWorkerIdentity expected = production.Identity;
        Require(expected.ExecutionRevision == release.ExecutionRevision &&
            expected.ConfigurationRevision == release.Configuration.Revision && production.Snapshot.ProcessId is > 0,
            "The session production client does not match its active build and configuration.");

        var catalog = await management.ReadCatalogAsync(CancellationToken.None);
        PluginCatalogEntry entry = catalog.Entries.Single(item => item.PluginId == activation.PluginId &&
            item.CandidateId == release.CandidateId);
        void AssertProductionObservation()
        {
            var snapshots = management.GetProductionWorkerSnapshots!();
            PluginProductionWorkerSnapshot owned = snapshots.Single(item => item.PluginId == activation.PluginId);
            Require(owned.PluginId == activation.PluginId && owned.Worker.Identity == expected &&
                owned.Worker.Identity.ExecutionRevision == entry.Activation!.Active!.ExecutionRevision &&
                owned.Worker.Identity.ConfigurationRevision == entry.Activation.Active.Configuration.Revision,
                "The session getter lost the exact plugin/build/configuration/instance binding.");
            var presented = PluginEntryPresentation.Create(entry, snapshots);
            Require(presented.Worker?.Identity == expected,
                "The plugin presentation did not use the real session production observation.");
        }
        AssertProductionObservation();
        var testConfiguration = release.Configuration with { Revision = "pux-isolated-test-" + Guid.NewGuid().ToString("N") };
        await using (IPluginWorkerClient test = await session.StartTestWorkerAsync(release.Artifact, testConfiguration))
        {
            Require(test.Identity.ExecutionRevision == expected.ExecutionRevision &&
                test.Identity.ConfigurationRevision != expected.ConfigurationRevision &&
                test.Identity.InstanceId != expected.InstanceId,
                "The independent test did not create the same build with a distinct instance/configuration.");
            var combined = session.Runtime.GetSnapshots();
            Require(combined.Any(item => item.Identity == expected) &&
                combined.Any(item => item.Identity == test.Identity),
                "The real production and isolated test clients were not simultaneously alive.");
            AssertProductionObservation();
            Require(management.GetProductionWorkerSnapshots!().All(item => item.Worker.Identity != test.Identity),
                "An isolated test worker leaked into the real session production getter.");
        }
        AssertProductionObservation();
        Require(production.Identity == expected && production.Snapshot.State != "stopped",
            "Disposing the isolated test stopped or replaced the session production client.");
        Console.WriteLine("PLUGIN SESSION OBSERVATION PASS: real session-owned production plugin=" + activation.PluginId
            + " execution=" + expected.ExecutionRevision + " configuration=" + expected.ConfigurationRevision
            + " instance=" + expected.InstanceId + "; same-build distinct test worker excluded.");
    }
}
