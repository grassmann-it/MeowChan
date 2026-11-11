using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

// Host startup with a native import that stopped before its commit: the UI decision discards it and
// startup reaches Ready; keeping it (or no decision) preserves the strict recovery failure.
public sealed class InterruptedTransferStartupTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheStartupDecisionDiscardsOrKeepsTheInterruptedImport(bool discard)
    {
        using TempDirectory directory = new();
        new Config { SaveUrlsOnExit = false, AutomaticUpdateChecks = false }.SaveConfig(directory.Path);
        string parent = await SeedInterruptedImportAsync(directory.Path);
        DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        IReadOnlyList<InterruptedNativeMediaOperation>? offered = null;
        host.InterruptedTransferDecision = (operations, _) => { offered = operations; return Task.FromResult(discard); };
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            InterruptedNativeMediaOperation operation = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<InterruptedNativeMediaOperation>>(offered));
            Assert.Equal(new ResourceId("4chan", "b", "7001"), operation.Resource);
            Assert.Equal(5, operation.CandidateBytes);
            Assert.Equal(discard ? DesktopSessionState.Ready : DesktopSessionState.RecoveryFailed, host.State);
            Assert.Equal(!discard, Directory.Exists(parent));
            if (!discard) Assert.Equal("Native media transfer reconciliation is required.", host.StartupError);
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(Deadline); }
    }

    [Fact]
    public async Task WithoutADecisionStartupStillFailsAndNothingIsRemoved()
    {
        using TempDirectory directory = new();
        new Config { SaveUrlsOnExit = false, AutomaticUpdateChecks = false }.SaveConfig(directory.Path);
        string parent = await SeedInterruptedImportAsync(directory.Path);
        DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.RecoveryFailed, host.State);
            Assert.True(Directory.Exists(parent));
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(Deadline); }
    }

    // A Preparing row with its canonical operations parent, as left by a crash right after PreparingSaved.
    private static async Task<string> SeedInterruptedImportAsync(string profile)
    {
        Guid operation = Guid.NewGuid();
        string parent = Path.Combine(profile, "import-media-operations", operation.ToString("N"));
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            await catalog.WriteAsync((connection, transaction) =>
            {
                using var insert = ArchiveSql.Command(connection, transaction, """
                    INSERT INTO archive_media_import_operations (operation_id,request_sha256,package_fingerprint,catalog_instance,
                        catalog_generation,source,board,thread,operations_parent,imported_at,state,revision_keys_json,
                        candidate_directory,manifest_generation,bindings_sha256,post_count,attachment_count)
                    VALUES ($op,$hash,$hash,$op,0,'4chan','b','7001',$parent,$at,'Preparing','[]',NULL,NULL,NULL,1,1);
                    """);
                insert.Parameters.AddWithValue("$op", operation.ToString("D"));
                insert.Parameters.AddWithValue("$hash", new string('a', 64));
                insert.Parameters.AddWithValue("$parent", parent);
                insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                return insert.ExecuteNonQuery();
            });
        Directory.CreateDirectory(Path.Combine(parent, "partial"));
        File.WriteAllText(Path.Combine(parent, "partial", "bytes"), "12345");
        return parent;
    }
}
