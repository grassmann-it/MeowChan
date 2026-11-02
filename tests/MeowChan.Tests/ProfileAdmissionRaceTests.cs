using System.Collections.Concurrent;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ProfileAdmissionRaceTests
{
    private const int Iterations = 32;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CancellationRacingLastWriterReleaseNeverRevokesOrLeaksAGrant()
    {
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            using ProfileAdmission admission = ReadyAdmission();
            using GrantedTokens tokens = new();
            using CancellationTokenSource cancellation = new();
            ProfileAdmissionToken writer = tokens.Own(admission.EnterOrdinaryWriter());
            Task<ProfileAdmissionToken> pending = tokens.OwnAsync(
                admission.EnterExclusiveAsync(cancellation.Token));
            Assert.False(pending.IsCompleted);

            await RaceAsync(cancellation.Cancel, writer.Dispose);

            ProfileAdmissionToken? granted = null;
            Exception? error = await Record.ExceptionAsync(async () =>
            {
                granted = await pending.WaitAsync(Deadline);
            });
            Assert.Equal(ProfileAdmissionPhase.Ready, admission.Phase);
            Assert.True(cancellation.IsCancellationRequested);
            if (error is not null)
            {
                OperationCanceledException cancelled = Assert.IsAssignableFrom<OperationCanceledException>(error);
                Assert.Equal(cancellation.Token, cancelled.CancellationToken);
                Assert.True(pending.IsCanceled);

                // A lost race must leave neither a ghost writer nor a reservation.
                ProfileAdmissionToken nextWriter = tokens.Own(admission.EnterOrdinaryWriter());
                nextWriter.Dispose();
                granted = await tokens.OwnAsync(admission.EnterExclusiveAsync()).WaitAsync(Deadline);
            }

            Assert.NotNull(granted);
            Assert.Equal(ProfileAdmissionKind.Exclusive, granted.Kind);
            AssertExclusiveReserved(admission, tokens);
            admission.BeginShutdown();
            Task drain = admission.DrainAsync();
            Assert.False(drain.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => admission.CompleteShutdown());
            granted.Dispose();
            await drain.WaitAsync(Deadline);
            admission.CompleteShutdown();
            Assert.Equal(ProfileAdmissionPhase.Stopped, admission.Phase);
        }
    }

    [Fact]
    public async Task CancellationMarkedBeforeAdmissionCallbackRunsRejectsLastWriterRace()
    {
        using ProfileAdmission admission = ReadyAdmission();
        using GrantedTokens tokens = new();
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim callbackEntered = new();
        using ManualResetEventSlim releaseCallback = new();
        ProfileAdmissionToken writer = tokens.Own(admission.EnterOrdinaryWriter());
        Task<ProfileAdmissionToken> pending = tokens.OwnAsync(
            admission.EnterExclusiveAsync(cancellation.Token));
        Assert.False(pending.IsCompleted);

        int callbackTimedOut = 0;
        using CancellationTokenRegistration blocker = cancellation.Token.Register(() =>
        {
            callbackEntered.Set();
            if (!releaseCallback.Wait(Deadline))
                Interlocked.Exchange(ref callbackTimedOut, 1);
        });
        Task cancelWorker = Task.Factory.StartNew(
            cancellation.Cancel,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            Assert.True(callbackEntered.Wait(Deadline));
            Assert.True(cancellation.IsCancellationRequested);

            // CTS cancellation is already visible, but the newer callback still
            // prevents the admission callback from running. Release must observe
            // cancellation and reject the waiter rather than grant the token.
            writer.Dispose();
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => pending.WaitAsync(Deadline));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.True(pending.IsCanceled);

            ProfileAdmissionToken laterWriter = tokens.Own(admission.EnterOrdinaryWriter());
            laterWriter.Dispose();
        }
        finally
        {
            releaseCallback.Set();
            await cancelWorker.WaitAsync(Deadline);
        }

        Assert.Equal(0, Volatile.Read(ref callbackTimedOut));
        admission.BeginShutdown();
        Task drain = admission.DrainAsync();
        Assert.True(drain.IsCompletedSuccessfully);
        await drain.WaitAsync(Deadline);
        admission.CompleteShutdown();
        Assert.Equal(ProfileAdmissionPhase.Stopped, admission.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownRacingQueuedGrantEitherRejectsOrDrainsTheAcceptedToken(bool close)
    {
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            using ProfileAdmission admission = ReadyAdmission();
            using GrantedTokens tokens = new();
            ProfileAdmissionToken writer = tokens.Own(admission.EnterOrdinaryWriter());
            Task<ProfileAdmissionToken> pending = tokens.OwnAsync(admission.EnterExclusiveAsync());
            Assert.False(pending.IsCompleted);

            await RaceAsync(writer.Dispose, () =>
            {
                if (close)
                    admission.Close();
                else
                    admission.BeginShutdown();
            });

            ProfileAdmissionToken? granted = null;
            Exception? error = await Record.ExceptionAsync(async () =>
            {
                granted = await pending.WaitAsync(Deadline);
            });
            if (error is not null)
            {
                Assert.IsType<InvalidOperationException>(error);
                Assert.True(pending.IsFaulted);
            }
            else
            {
                Assert.NotNull(granted);
                Assert.Equal(ProfileAdmissionKind.Exclusive, granted.Kind);
            }

            Assert.Equal(ProfileAdmissionPhase.Stopping, admission.Phase);
            AssertSealed(admission, tokens, close);
            Task drain = admission.DrainAsync();
            if (granted is null)
            {
                // The sole ordinary writer has been released and rejection owns no slot.
                Assert.True(drain.IsCompletedSuccessfully);
            }
            else
            {
                // Shutdown cannot revoke a grant even if its await resumed afterwards.
                Assert.False(drain.IsCompleted);
                Assert.Throws<InvalidOperationException>(() => admission.CompleteShutdown());
                granted.Dispose();
            }

            await drain.WaitAsync(Deadline);
            admission.CompleteShutdown();
            Assert.Equal(ProfileAdmissionPhase.Stopped, admission.Phase);
            AssertSealed(admission, tokens, close);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDoubleDisposeCannotDrainAnotherAcceptedWriter(bool startup)
    {
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            using ProfileAdmission admission = new();
            using GrantedTokens tokens = new();
            if (!startup)
                admission.CompleteStartup();
            ProfileAdmissionToken target = tokens.Own(startup
                ? admission.EnterStartupWriter()
                : admission.EnterOrdinaryWriter());
            ProfileAdmissionToken survivor = tokens.Own(startup
                ? admission.EnterStartupWriter()
                : admission.EnterOrdinaryWriter());
            admission.BeginShutdown();
            Task firstDrain = admission.DrainAsync();
            Task secondDrain = admission.DrainAsync();

            await RaceAsync(target.Dispose, target.Dispose);

            Assert.False(firstDrain.IsCompleted);
            Assert.False(secondDrain.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => admission.CompleteShutdown());
            survivor.Dispose();
            await Task.WhenAll(firstDrain, secondDrain).WaitAsync(Deadline);
            admission.CompleteShutdown();
            Assert.Equal(ProfileAdmissionPhase.Stopped, admission.Phase);
        }
    }

    [Fact]
    public async Task ConcurrentDoubleDisposeOfLastWriterCannotReleaseTheExclusiveSuccessor()
    {
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            using ProfileAdmission admission = ReadyAdmission();
            using GrantedTokens tokens = new();
            ProfileAdmissionToken writer = tokens.Own(admission.EnterOrdinaryWriter());
            Task<ProfileAdmissionToken> pending = tokens.OwnAsync(admission.EnterExclusiveAsync());

            await RaceAsync(writer.Dispose, writer.Dispose);

            ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline);
            Assert.Equal(ProfileAdmissionKind.Exclusive, exclusive.Kind);
            AssertExclusiveReserved(admission, tokens);

            // Also race disposal of an exclusive token, then retain a fresh exclusive
            // while stale disposals run: no duplicate release may affect its lifetime.
            await RaceAsync(exclusive.Dispose, exclusive.Dispose);
            ProfileAdmissionToken successor = await tokens.OwnAsync(
                admission.EnterExclusiveAsync()).WaitAsync(Deadline);
            Assert.NotSame(exclusive, successor);
            admission.BeginShutdown();
            Task drain = admission.DrainAsync();
            await RaceAsync(writer.Dispose, exclusive.Dispose);
            Assert.False(drain.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => admission.CompleteShutdown());
            successor.Dispose();
            await drain.WaitAsync(Deadline);
            admission.CompleteShutdown();
            Assert.Equal(ProfileAdmissionPhase.Stopped, admission.Phase);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentExclusiveRequestsHaveExactlyOneWinner(bool queued)
    {
        const int contenders = 4;
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            using ProfileAdmission admission = ReadyAdmission();
            using GrantedTokens tokens = new();
            ProfileAdmissionToken? writer = queued
                ? tokens.Own(admission.EnterOrdinaryWriter())
                : null;
            Task<ExclusiveAttempt>[] attempts = new Task<ExclusiveAttempt>[contenders];
            Action[] requests = Enumerable.Range(0, contenders)
                .Select(index => (Action)(() => attempts[index] = AttemptExclusiveAsync(admission, tokens)))
                .ToArray();

            // Each worker issues its request before the last writer is released.
            // Successful requests stay owned until every contender has settled.
            await RaceAsync(requests);
            if (queued)
            {
                AssertExclusiveReserved(admission, tokens);
                Assert.Contains(attempts, attempt => !attempt.IsCompleted);
                writer!.Dispose();
            }
            ExclusiveAttempt[] results = await Task.WhenAll(attempts).WaitAsync(Deadline);
            ExclusiveAttempt winner = Assert.Single(results, result => result.Token is not null);
            Assert.Null(winner.Error);
            ProfileAdmissionToken exclusive = winner.Token!;
            Assert.Equal(ProfileAdmissionKind.Exclusive, exclusive.Kind);
            Assert.Equal(contenders - 1, results.Count(result => result.Error is not null));
            foreach (ExclusiveAttempt loser in results.Where(result => result.Token is null))
                Assert.IsType<InvalidOperationException>(loser.Error);
            AssertExclusiveReserved(admission, tokens);

            exclusive.Dispose();
            ProfileAdmissionToken nextWriter = tokens.Own(admission.EnterOrdinaryWriter());
            nextWriter.Dispose();
            ProfileAdmissionToken nextExclusive = await tokens.OwnAsync(
                admission.EnterExclusiveAsync()).WaitAsync(Deadline);
            admission.BeginShutdown();
            Task drain = admission.DrainAsync();
            Assert.False(drain.IsCompleted);
            nextExclusive.Dispose();
            await drain.WaitAsync(Deadline);
            admission.CompleteShutdown();
            Assert.Equal(ProfileAdmissionPhase.Stopped, admission.Phase);
        }
    }

    private static async Task<ExclusiveAttempt> AttemptExclusiveAsync(
        ProfileAdmission admission, GrantedTokens tokens)
    {
        try
        {
            return new ExclusiveAttempt(await tokens.OwnAsync(admission.EnterExclusiveAsync()), null);
        }
        catch (InvalidOperationException error)
        {
            return new ExclusiveAttempt(null, error);
        }
    }

    private static async Task RaceAsync(params Action[] actions)
    {
        using Barrier start = new(actions.Length);
        // Dedicated workers avoid blocking the thread pool while waiting for peers.
        Task[] workers = actions.Select(action => Task.Factory.StartNew(() =>
        {
            if (!start.SignalAndWait(Deadline))
                throw new TimeoutException("Admission race participants did not reach the barrier.");
            action();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(workers).WaitAsync(Deadline);
    }

    private static ProfileAdmission ReadyAdmission()
    {
        ProfileAdmission admission = new();
        admission.CompleteStartup();
        return admission;
    }

    private static void AssertExclusiveReserved(ProfileAdmission admission, GrantedTokens tokens)
    {
        Assert.Throws<InvalidOperationException>(() => tokens.Own(admission.EnterOrdinaryWriter()));
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = tokens.OwnAsync(admission.EnterExclusiveAsync());
        });
    }

    private static void AssertSealed(ProfileAdmission admission, GrantedTokens tokens, bool closed)
    {
        Action[] entries =
        [
            () => tokens.Own(admission.EnterStartupWriter()),
            () => tokens.Own(admission.EnterOrdinaryWriter()),
            () => { _ = tokens.OwnAsync(admission.EnterExclusiveAsync()); }
        ];
        foreach (Action enter in entries)
        {
            if (closed)
                Assert.Throws<ObjectDisposedException>(enter);
            else
                Assert.Throws<InvalidOperationException>(enter);
        }
    }

    private sealed record ExclusiveAttempt(ProfileAdmissionToken? Token, Exception? Error);

    // WaitAsync bounds observation, not the underlying request. Retain ownership
    // even after a failed assertion/timeout so a late successful request is disposed.
    private sealed class GrantedTokens : IDisposable
    {
        private readonly ConcurrentBag<ProfileAdmissionToken> _tokens = new();
        private int _disposed;

        internal ProfileAdmissionToken Own(ProfileAdmissionToken token)
        {
            _tokens.Add(token);
            if (Volatile.Read(ref _disposed) != 0)
                token.Dispose();
            return token;
        }

        internal async Task<ProfileAdmissionToken> OwnAsync(ValueTask<ProfileAdmissionToken> request) =>
            Own(await request.ConfigureAwait(false));

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            while (_tokens.TryTake(out ProfileAdmissionToken? token))
                token.Dispose();
        }
    }
}
