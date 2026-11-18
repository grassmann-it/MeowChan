using System.Reflection;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

public sealed class ArchiveReaderLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly ResourceId Id = new("lifetime", "board", "1");

    [Theory]
    [InlineData("success")]
    [InlineData("cancellation")]
    [InlineData("fault")]
    public async Task ConcurrentDisposalDrainsHeldReadAndClosesAdmissionWithoutReplacingItsOutcome(string outcome)
    {
        using TempDirectory directory = new();
        await CreateCatalog(directory.Path);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InvalidOperationException fault = new("synthetic read failure");
        // Exercise the actual admission/transaction/disposal primitive, holding a real read
        // connection after schema validation. Reflection avoids a shipping test-only hook.
        MethodInfo readMethod = typeof(ArchiveReader).GetMethod("ReadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(int));
        Func<SqliteConnection, SqliteTransaction, int> body = (connection, transaction) =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master";
            Assert.True(Convert.ToInt32(command.ExecuteScalar()) > 0);
            entered.SetResult();
            if (!release.Wait(Deadline)) throw new TimeoutException("The held read was not released.");
            if (outcome == "fault") throw fault;
            return 42;
        };
        Task<int> read = (Task<int>)readMethod.Invoke(reader, [body, cancellation.Token])!;
        Task[] disposals = new Task[8];
        try
        {
            await entered.Task.WaitAsync(Deadline);
            // Invoke on different threads; every returned barrier must cover the same held read.
            Parallel.For(0, disposals.Length, i => disposals[i] = reader.DisposeAsync().AsTask());
            Assert.All(disposals, disposal => Assert.False(disposal.IsCompleted));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadThreadAsync(Id));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ListThreadsAsync());
            if (outcome == "cancellation") cancellation.Cancel();
            release.Set();
            await Task.WhenAll(disposals).WaitAsync(Deadline);
            Assert.True(read.IsCompleted);
            if (outcome == "fault")
                Assert.Same(fault, await Assert.ThrowsAsync<InvalidOperationException>(() => read));
            else if (outcome == "cancellation")
                Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read)).CancellationToken);
            else
                Assert.Equal(42, await read);
            await reader.DisposeAsync().AsTask().WaitAsync(Deadline);
            using FileStream exclusive = new(Path.Combine(directory.Path, "archive.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            release.Set();
            await reader.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task PreCancelledReadDoesNotPoisonDisposalOrSubsequentReadAdmission()
    {
        using TempDirectory directory = new();
        await CreateCatalog(directory.Path);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadThreadAsync(Id, cancellation.Token));
        Assert.Null(await reader.ReadThreadAsync(Id));
        await reader.DisposeAsync().AsTask().WaitAsync(Deadline);
        await reader.DisposeAsync().AsTask().WaitAsync(Deadline);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadThreadAsync(Id));
    }

    private static async Task CreateCatalog(string profile)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
    }
}
