using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tests.GoalFixtures;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTransferArchiveInvalidRootTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 11, 0, 0, TimeSpan.Zero);
    private static readonly Guid PackageId = Guid.Parse("fa977e62-06ce-41aa-9fbc-049b855a10d7");
    private const string SecretMarker = "SECRET-ARCHIVE-ROOT";

    [Theory]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("relative")]
    [InlineData("nul")]
    public async Task MalformedPersistedRootFreezesAsSanitizedInvalidArchiveWithoutRepair(string kind)
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        string malformedRoot = InvalidRoot(fixture, kind);
        await SetStoredRootAsync(fixture, malformedRoot);
        string databaseBefore = await DatabaseHashAsync(fixture);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);

        TransferArchiveFreezeResult result = await NativeTransferArchiveExporter.FreezeAsync(
            reader, fixture.ThreadId, At, PackageId, includeOriginals: true);

        Assert.False(result.IsFrozen);
        Assert.Null(result.Snapshot);
        Assert.Equal(TransferArchiveError.InvalidArchive, result.Error);
        AssertSanitized(result.ToString(), fixture);
        Assert.Equal(malformedRoot, await ReadStoredRootAsync(fixture));
        await AssertUnrepairedAsync(fixture, databaseBefore);
    }

    [Fact]
    public async Task StoredHtmlGrowingBeyondReadBudgetAfterFreezeReturnsInvalidMediaBeforeWriting()
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(
            reader, fixture.ThreadId, At, PackageId, includeOriginals: true);
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        NativeTransferArchiveSnapshot snapshot = frozen.Snapshot!;
        Assert.NotEmpty(snapshot.Payloads);

        // SQLite creates the oversized ASCII text; do not materialize a >32 MiB .NET HTML string.
        const int halfBytes = 16 * 1024 * 1024 + 1;
        long expectedBytes = SecretMarker.Length + 2L * halfBytes;
        using (ProfileLease lease = ProfileLease.Acquire(fixture.ProfilePath))
        await using (SqliteConnection connection = Connection(fixture, SqliteOpenMode.ReadWrite))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE archive_posts SET html=$marker || hex(zeroblob($halfBytes)) "
                + "WHERE source=$source AND board=$board AND thread=$thread AND post=$post";
            AddThreadParameters(command, fixture);
            command.Parameters.AddWithValue("$post", NativeTransferArchiveFixture.StrictPostId);
            command.Parameters.AddWithValue("$marker", SecretMarker);
            command.Parameters.AddWithValue("$halfBytes", halfBytes);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.True(expectedBytes > 32L * 1024 * 1024);
        Assert.Equal(expectedBytes, await ReadStoredHtmlBytesAsync(fixture));
        string databaseBefore = await DatabaseHashAsync(fixture);
        using MemoryStream stage = new();

        TransferArchiveWriteResult result = await NativeTransferArchiveExporter.WriteAsync(snapshot, stage);

        AssertFailedBeforeWriting(result, stage, fixture);
        Assert.Equal(TransferArchiveError.InvalidMedia, result.Error);
        Assert.Equal(expectedBytes, await ReadStoredHtmlBytesAsync(fixture));
        await AssertUnrepairedAsync(fixture, databaseBefore);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("relative")]
    [InlineData("nul")]
    public async Task PersistedRootBecomingInvalidAfterFreezeReturnsStaleSnapshotWithoutRepair(string kind)
    {
        await using NativeTransferArchiveFixture fixture = await NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(
            reader, fixture.ThreadId, At, PackageId, includeOriginals: true);
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        NativeTransferArchiveSnapshot snapshot = frozen.Snapshot!;
        Assert.NotEmpty(snapshot.Payloads);

        string malformedRoot = InvalidRoot(fixture, kind);
        await SetStoredRootAsync(fixture, malformedRoot);
        string databaseBefore = await DatabaseHashAsync(fixture);
        using MemoryStream stage = new();

        TransferArchiveWriteResult result = await NativeTransferArchiveExporter.WriteAsync(snapshot, stage);

        AssertFailedBeforeWriting(result, stage, fixture);
        Assert.Equal(malformedRoot, await ReadStoredRootAsync(fixture));
        await AssertUnrepairedAsync(fixture, databaseBefore);
        // Comparable raw directory drift is detected before the changed root is opened or validated.
        Assert.Equal(TransferArchiveError.StaleSnapshot, result.Error);
    }

    private static string InvalidRoot(NativeTransferArchiveFixture fixture, string kind) => kind switch
    {
        "empty" => string.Empty,
        "whitespace" => " \t\r\n ",
        "relative" => SecretMarker + "-relative-root",
        // Retain an absolute prefix so this reaches GetFullPath's argument-validation boundary.
        "nul" => fixture.MediaDirectory + Path.DirectorySeparatorChar + SecretMarker + "\0-root",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static async Task SetStoredRootAsync(NativeTransferArchiveFixture fixture, string malformedRoot)
    {
        using ProfileLease lease = ProfileLease.Acquire(fixture.ProfilePath);
        await using SqliteConnection connection = Connection(fixture, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE archive_media_directories SET directory=$root "
            + "WHERE source=$source AND board=$board AND thread=$thread AND directory=$originalRoot";
        AddThreadParameters(command, fixture);
        command.Parameters.AddWithValue("$root", malformedRoot);
        command.Parameters.AddWithValue("$originalRoot", fixture.MediaDirectory);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<string> ReadStoredRootAsync(NativeTransferArchiveFixture fixture)
    {
        await using SqliteConnection connection = Connection(fixture, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT directory FROM archive_media_directories "
            + "WHERE source=$source AND board=$board AND thread=$thread";
        AddThreadParameters(command, fixture);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<long> ReadStoredHtmlBytesAsync(NativeTransferArchiveFixture fixture)
    {
        await using SqliteConnection connection = Connection(fixture, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT length(CAST(html AS BLOB)) FROM archive_posts "
            + "WHERE source=$source AND board=$board AND thread=$thread AND post=$post";
        AddThreadParameters(command, fixture);
        command.Parameters.AddWithValue("$post", NativeTransferArchiveFixture.StrictPostId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static SqliteConnection Connection(NativeTransferArchiveFixture fixture, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.ProfilePath, "archive.db"), Mode = mode, Pooling = false
        }.ToString());

    private static void AddThreadParameters(SqliteCommand command, NativeTransferArchiveFixture fixture)
    {
        command.Parameters.AddWithValue("$source", fixture.ThreadId.Source);
        command.Parameters.AddWithValue("$board", fixture.ThreadId.Board);
        command.Parameters.AddWithValue("$thread", fixture.ThreadId.Thread);
    }

    private static async Task<string> DatabaseHashAsync(NativeTransferArchiveFixture fixture)
    {
        await using FileStream database = File.OpenRead(Path.Combine(fixture.ProfilePath, "archive.db"));
        return Convert.ToHexString(await SHA256.HashDataAsync(database));
    }

    private static async Task AssertUnrepairedAsync(NativeTransferArchiveFixture fixture, string databaseBefore)
    {
        Assert.Equal(databaseBefore, await DatabaseHashAsync(fixture));
        Assert.Equal(fixture.ManifestGeneration, (await MediaAssetReader.OpenAsync(fixture.MediaDirectory)).Generation);
        Assert.Equal(fixture.StrictBytes.ToArray(), await File.ReadAllBytesAsync(fixture.StrictCommit.Path));
        Assert.Equal(fixture.LegacyBytes.ToArray(), await File.ReadAllBytesAsync(fixture.LegacyCommit.Path));
        Assert.Equal(0, fixture.UnexpectedProviderCalls);
    }

    private static void AssertFailedBeforeWriting(TransferArchiveWriteResult result, MemoryStream stage,
        NativeTransferArchiveFixture fixture)
    {
        Assert.False(result.IsComplete);
        Assert.Null(result.Package);
        Assert.Null(result.BytesWritten);
        Assert.Equal(0, stage.Length);
        Assert.Equal(0, stage.Position);
        Assert.True(stage.CanRead);
        Assert.True(stage.CanWrite);
        AssertSanitized(result.ToString(), fixture);
    }

    private static void AssertSanitized(string result, NativeTransferArchiveFixture fixture)
    {
        Assert.DoesNotContain(fixture.ProfilePath, result, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.MediaDirectory, result, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretMarker, result, StringComparison.Ordinal);
        Assert.DoesNotContain("ArgumentException", result, StringComparison.Ordinal);
    }
}
