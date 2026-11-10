using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.DesktopTests;

// Host wiring regression: synthetic SQL journals, not a recovery/resume implementation.
[Collection("Delivered plugin hosts")]
public sealed class NativeTransferStartupRecoveryHostTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private const string Operation = "01234567-89ab-cdef-0123-456789abcdef";
    private static readonly string Hash = new('a', 64);
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    [Theory]
    [InlineData(false, "Preparing", "none")]
    [InlineData(true, "Preparing", "none")]
    [InlineData(false, "Prepared", "none")]
    [InlineData(true, "Prepared", "none")]
    [InlineData(false, "Committed", "none")]
    [InlineData(true, "Committed", "none")]
    [InlineData(false, "Preparing", "exact")]
    [InlineData(true, "Preparing", "exact")]
    [InlineData(false, "Prepared", "exact")]
    [InlineData(true, "Prepared", "exact")]
    [InlineData(false, "Committed", "exact")]
    [InlineData(true, "Committed", "exact")]
    [InlineData(false, "Committed", "request")]
    [InlineData(true, "Committed", "request")]
    [InlineData(false, "Committed", "counts")]
    [InlineData(true, "Committed", "counts")]
    public async Task DurableTransferJournalGatesTwoOfflineHostRestarts(bool merge, string state, string receipt)
    {
        using TempDirectory directory = new();
        string database = await SeedProfile(directory.Path, merge, state, receipt);
        bool healthy = state == "Committed" && receipt == "exact";
        await AssertTwoRestarts(directory.Path, database, healthy);
    }

    [Fact]
    public async Task EmptyTransferJournalAllowsTwoOfflineHostRestarts()
    {
        using TempDirectory directory = new();
        string database = await SeedProfile(directory.Path, null, null, null);
        await AssertTwoRestarts(directory.Path, database, healthy: true);
    }

    private static async Task AssertTwoRestarts(string profile, string database, bool healthy)
    {
        string journal = ReadJournal(database);
        string candidateRoot = Path.Combine(profile, "import-media-operations");
        string[] evidence = CandidateInventory(candidateRoot);
        byte[] session = File.ReadAllBytes(Path.Combine(profile, "saved_urls.json"));
        for (int restart = 0; restart < 2; restart++)
        {
            ObservedStore? observed = null;
            ProfileLease? lease = null;
            await using (DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>(),
                (path, owner) => { lease = owner; return observed = new(new SessionStore(path, owner)); }))
            {
                Assert.Null(host.Plugins); // Explicit empty providers: never discover/start plugins.
                Assert.NotNull(observed); Assert.NotNull(lease);
                Assert.Equal(0, observed.Loads);
                bool readablePublished = false;
                host.CatalogReadableChanged += () => readablePublished = true;
                await host.StartAsync().WaitAsync(Deadline);
                // An unresolved transfer never makes the archive readable, not even transiently.
                Assert.Equal(healthy, readablePublished); Assert.Equal(healthy, host.CatalogReadable);
                Assert.Equal(healthy ? DesktopSessionState.Ready : DesktopSessionState.RecoveryFailed, host.State);
                Assert.Equal(healthy ? ProfileAdmissionPhase.Ready : ProfileAdmissionPhase.RecoveryFailed, lease.Admission.Phase);
                Assert.Equal(healthy ? 1 : 0, observed.Loads);
                Assert.Equal(healthy, Field<bool>(Field<Registry>(host, "_registry"), "_restored"));
                Assert.Equal(0, observed.Saves);
                Assert.Equal(0, host.AcceptedCommandCount);
                Assert.Empty(host.GetView().Threads); Assert.Empty(host.GetView().Boards);
                Assert.Empty(host.Projection.Providers);
                if (healthy)
                {
                    Assert.Null(host.StartupError);
                    using ProfileAdmissionToken ordinary = lease.Admission.EnterOrdinaryWriter();
                    Assert.Equal(ProfileAdmissionKind.Ordinary, ordinary.Kind);
                    Assert.Equal(ReaderGroupsLoadState.Ready,
                        (await host.LoadReaderGroupsAsync().WaitAsync(Deadline)).State);
                }
                else
                {
                    Assert.Equal("Native media transfer reconciliation is required.", host.StartupError);
                    Assert.Throws<InvalidOperationException>(() => { using var denied = lease.Admission.EnterOrdinaryWriter(); });
                    await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadReaderGroupsAsync().WaitAsync(Deadline));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => host.SaveReaderGroupsAsync(ReaderGroupsSnapshot.Empty, 0).WaitAsync(Deadline));
                    Assert.Equal(0, host.AcceptedCommandCount);
                    // A second Start on this failed instance must not retry restoration or repair.
                    await host.StartAsync().WaitAsync(Deadline);
                    Assert.Equal(DesktopSessionState.RecoveryFailed, host.State);
                    Assert.Equal(0, observed.Loads); Assert.Equal(0, observed.Saves);
                }
                Assert.Equal(journal, ReadJournal(database));
                Assert.Equal(evidence, CandidateInventory(candidateRoot));
                Assert.Equal(session, File.ReadAllBytes(Path.Combine(profile, "saved_urls.json")));
                await host.StopAsync().WaitAsync(Deadline);
                Assert.Equal(ProfileAdmissionPhase.Stopped, lease.Admission.Phase);
                Assert.Equal(healthy ? 1 : 0, observed.Loads);
                // Save requests at normal shutdown are observed but not persisted by this seam.
                await host.DisposeAsync().AsTask().WaitAsync(Deadline);
                Assert.True(host.IsProfileReleased);
            }
            // Include disposal in the no-repair boundary, then reacquire via a genuinely new host.
            Assert.Equal(journal, ReadJournal(database));
            Assert.Equal(evidence, CandidateInventory(candidateRoot));
            Assert.Equal(session, File.ReadAllBytes(Path.Combine(profile, "saved_urls.json")));
        }
    }

    private static async Task<string> SeedProfile(string profile, bool? merge, string? state, string? receipt)
    {
        new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = true, DownloadsPaused = true,
            DownloadPath = Path.Combine(profile, "absent-media") }.SaveConfig(profile);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        // A real readable session makes Loads=0 causal, not a missing-file shortcut.
        await new SessionStore(profile, lease).SaveAsync(new SessionSnapshot
        { SuppressedThreadUrls = ["https://offline.invalid/suppressed-only"] }).WaitAsync(Deadline);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease).WaitAsync(Deadline);
        if (merge == null) return catalog.DatabasePath;
        string parent = Path.Combine(profile, "import-media-operations", Operation.Replace("-", ""));
        string child = Path.Combine(parent, "synthetic-incomplete-child");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, ".meowchan-import-candidate.json"), "synthetic invalid journal: never repair");
        File.WriteAllBytes(Path.Combine(child, "retained-original.bin"), [0, 1, 2, 255]);
        await catalog.WriteAsync((connection, transaction) =>
        {
            string table = merge.Value ? "archive_media_merge" : "archive_media_import";
            string columns = merge.Value
                ? "roots_json,full_keys_json,safe_keys_json,catalog_bytes,full_directory,full_manifest_generation,full_bindings_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts"
                : "revision_keys_json,candidate_directory,manifest_generation,bindings_sha256,post_count,attachment_count";
            string values = merge.Value ? "'[]','[]','[]',X'7B7D',$dir,$generation,$generation,1,0,1,0,0"
                : "'[]',$dir,$generation,$generation,1,1";
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO {table}_operations(operation_id,request_sha256,package_fingerprint,catalog_instance,
                    catalog_generation,source,board,thread,operations_parent,imported_at,state,{columns})
                VALUES($op,$hash,$hash,$op,0,'fixture','b','1',$parent,'2026-11-06',$state,{values});
                """;
            command.Parameters.AddWithValue("$op", Operation); command.Parameters.AddWithValue("$hash", Hash);
            command.Parameters.AddWithValue("$parent", parent); command.Parameters.AddWithValue("$state", state!);
            command.Parameters.AddWithValue("$dir", state == "Preparing" ? DBNull.Value : child);
            command.Parameters.AddWithValue("$generation", state == "Preparing" ? DBNull.Value : Hash);
            Assert.Equal(1, command.ExecuteNonQuery());
            if (receipt != "none")
            {
                using var add = connection.CreateCommand(); add.Transaction = transaction;
                string receiptColumns = merge.Value
                    ? "operation_id,request_sha256,new_posts,updated_posts,new_attachments,new_bindings,conflicts"
                    : "operation_id,request_sha256,post_count,attachment_count";
                string receiptValues = merge.Value ? "$op,$hash,$posts,0,1,0,0" : "$op,$hash,$posts,1";
                add.CommandText = $"INSERT INTO {table}_receipts({receiptColumns}) VALUES({receiptValues})";
                add.Parameters.AddWithValue("$op", Operation);
                add.Parameters.AddWithValue("$hash", receipt == "request" ? new string('b', 64) : Hash);
                add.Parameters.AddWithValue("$posts", receipt == "counts" ? 99 : 1);
                Assert.Equal(1, add.ExecuteNonQuery());
            }
            return true;
        }).WaitAsync(Deadline);
        return catalog.DatabasePath;
    }

    private static string ReadJournal(string database)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        List<string> tables = [];
        foreach (string table in new[] { "archive_media_import_operations", "archive_media_import_receipts",
            "archive_media_merge_operations", "archive_media_merge_receipts" })
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"SELECT * FROM {table} ORDER BY operation_id";
            using var reader = command.ExecuteReader();
            List<string?[]> rows = [];
            while (reader.Read())
            {
                string?[] row = new string?[reader.FieldCount];
                for (int index = 0; index < row.Length; index++)
                    row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index) is byte[] bytes
                        ? Convert.ToHexString(bytes) : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture);
                rows.Add(row);
            }
            tables.Add(table + ":" + JsonSerializer.Serialize(rows));
        }
        foreach (string table in new[] { "archive_threads", "archive_posts", "archive_attachments", "archive_media_directories" })
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"SELECT count(*) FROM {table}";
            Assert.Equal(0L, (long)command.ExecuteScalar()!); // No implicit activation/repair of the synthetic candidates.
        }
        transaction.Commit(); return string.Join("\n", tables);
    }

    private static string[] CandidateInventory(string root) => !Directory.Exists(root) ? [] :
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) +
                (Directory.Exists(path) ? "/" : ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();

    // Real load delegation, counting seam only. Deliberately no session writer substitution claim:
    // normal shutdown save requests are counted/no-op so they cannot obscure startup evidence.
    private sealed class ObservedStore(SessionStore inner) : ISessionStore
    {
        internal int Loads, Saves;
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Loads); return inner.LoadAsync(cancellationToken); }
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Saves); return Task.CompletedTask; }
    }
}
