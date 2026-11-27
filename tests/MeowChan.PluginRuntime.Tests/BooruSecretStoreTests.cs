using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Secrets;
using MeowChan.PluginSdk;
using MeowChan.Secrets;

namespace MeowChan.PluginRuntime.Tests;

public sealed class BooruSecretStoreTests : IDisposable
{
    private const string Marker = "MEOWCHAN-BOORU-TEST-KEY";
    private const string Replacement = "MEOWCHAN-BOORU-REPLACEMENT-KEY";
    private const string Account = "local-tester";
    private const string NativeRun = "MEOWCHAN_TEST_NATIVE_BOORU_STORE_ID";

    private static readonly SourceDescription Source = new("danbooru", "Danbooru", new("https://metadata.test/"),
        SourceCapabilities.PostSearch, [], [new("metadata.test", HttpHostPurpose.Metadata)], new("1", []));
    private static readonly BooruSearchRequest Search = new(Source.SourceId, "cat", 1, 20);
    private static readonly HostHttpRequest Request = new(Source.SourceId, HostHttpMethod.Get,
        new Uri("https://metadata.test/posts.json?tags=cat&page=1&limit=20"));

    private readonly string _root = Directory.CreateTempSubdirectory("meowchan-secret-store-").FullName;
    private readonly FakeStore _store = new();

    [Fact]
    public async Task RestartReplacementAndRevocationUseTheCurrentOsEntry()
    {
        using (ProfileLease lease = ProfileLease.Acquire(_root))
        {
            BooruAccountBinding binding = new(lease, _store);
            Assert.Equal(BooruAccountState.Ready, (await ConfirmAsync(binding)).State);
            Guid reference = Assert.IsType<Guid>(BooruSecretReference.Read(lease));
            Assert.Equal(reference.ToString("N"), File.ReadAllText(Path.Combine(_root, BooruSecretReference.FileName)));
        }

        using ProfileLease reopened = ProfileLease.Acquire(_root);
        BooruAccountBinding current = new(reopened, _store);
        Assert.Equal(BooruAccountState.Ready, await current.GetStateAsync(Scope()));
        await AssertAttemptAsync(Capture(current), Marker);

        BooruAccountBinding.Operation waiting = Capture(current);
        Assert.Equal(new(BooruAccountState.Ready, SecretStoreStatus.Available),
            await ConfirmAsync(current, Replacement));
        Assert.Single(_store.Entries);
        Assert.False(waiting.TryPublish(() => throw new InvalidOperationException()));
        await AssertAttemptAsync(Capture(current), Replacement);

        Assert.Equal(new(BooruAccountState.Anonymous, SecretStoreStatus.Available), await current.RevokeAsync());
        Assert.Empty(_store.Entries);
        Assert.Null(BooruSecretReference.Read(reopened));
        await AssertAttemptAsync(Capture(new(reopened, _store)), expectedKey: null);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("revision")]
    [InlineData("instance")]
    [InlineData("origin")]
    [InlineData("source")]
    public async Task ConfirmedScopeCannotAuthorizeAnotherBinding(string mismatch)
    {
        using ProfileLease lease = ProfileLease.Acquire(_root);
        BooruAccountBinding binding = new(lease, _store);
        Assert.Equal(BooruAccountState.Ready, (await ConfirmAsync(binding)).State);

        BooruAccountScope scope = mismatch switch
        {
            "owner" => Scope(plugin: "other-plugin"),
            "revision" => Scope(revision: "second"),
            "instance" => Scope(instance: "other"),
            "origin" => Scope(origin: "https://another.test/"),
            _ => Scope(source: "another")
        };
        Assert.Equal(BooruAccountState.ConfirmationRequired, await binding.GetStateAsync(scope));
        if (mismatch == "source")
        {
            Assert.Null(binding.CaptureOperation(scope, Source, Context(Search)));
        }
        else if (mismatch == "origin")
        {
            using HttpRequestMessage message = new(HttpMethod.Get, Request.Url);
            await Assert.ThrowsAsync<BooruAccountChangedException>(() =>
                Capture(binding, scope).SendAttemptAsync(Request, HttpHostPurpose.Metadata, message, RejectSend, default));
        }
        else
        {
            await AssertAttemptAsync(Capture(binding, scope), expectedKey: null);
        }
    }

    [Theory]
    [InlineData("search")]
    [InlineData("post")]
    [InlineData("preview")]
    [InlineData("original")]
    [InlineData("redirect")]
    [InlineData("host")]
    public async Task SecretIsReadOnlyForTheExactApprovedMetadataRequest(string mismatch)
    {
        using ProfileLease lease = ProfileLease.Acquire(_root);
        BooruAccountBinding binding = new(lease, _store);
        Assert.Equal(BooruAccountState.Ready, (await ConfirmAsync(binding)).State);

        HostHttpRequest request = mismatch switch
        {
            "search" => Request with { Url = new("https://metadata.test/posts.json?tags=dog&page=1&limit=20") },
            "post" => Request with { Url = new("https://metadata.test/posts/42.json") },
            _ => Request
        };
        HttpHostPurpose purpose = mismatch switch
        {
            "preview" => HttpHostPurpose.PreviewMedia,
            "original" => HttpHostPurpose.OriginalMedia,
            _ => HttpHostPurpose.Metadata
        };
        Uri destination = mismatch == "redirect" ? new("https://cdn.test/posts.json") : request.Url;
        using HttpRequestMessage message = new(HttpMethod.Get, destination);
        if (mismatch == "host") message.Headers.Host = "cdn.test";

        await Assert.ThrowsAsync<BooruAccountChangedException>(() =>
            Capture(binding).SendAttemptAsync(request, purpose, message, RejectSend, default));
        Assert.Equal(0, _store.ReadCount);
        Assert.Null(message.Headers.Authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableAndLockedStoresKeepMetadataAnonymous(bool locked)
    {
        using ProfileLease lease = ProfileLease.Acquire(_root);
        BooruAccountBinding binding = new(lease, _store);
        Assert.Equal(BooruAccountState.Ready, (await ConfirmAsync(binding)).State);
        _store.Status = locked ? SecretStoreStatus.Locked : SecretStoreStatus.Unavailable;

        Assert.Equal(locked ? BooruAccountState.StoreLocked : BooruAccountState.StoreUnavailable,
            await binding.GetStateAsync(Scope()));
        await AssertAttemptAsync(Capture(binding), expectedKey: null);
    }

    [Fact]
    public async Task OptOutInvalidatesQueuedAttemptsAndSkipsStoreReads()
    {
        using ProfileLease lease = ProfileLease.Acquire(_root);
        BooruAccountBinding binding = new(lease, _store);
        Assert.Equal(BooruAccountState.Ready, (await ConfirmAsync(binding)).State);
        BooruAccountBinding.Operation queued = Capture(binding);

        binding.SetEnabled(false);
        Assert.Equal(BooruAccountState.StoreDisabled, await binding.GetStateAsync(Scope()));
        using HttpRequestMessage request = new(HttpMethod.Get, Request.Url);
        await Assert.ThrowsAsync<BooruAccountChangedException>(() =>
            queued.SendAttemptAsync(Request, HttpHostPurpose.Metadata, request, RejectSend, default));
        await AssertAttemptAsync(Capture(binding), expectedKey: null);
        Assert.Equal(0, _store.ReadCount);
    }

    [Fact]
    public async Task LocalRevocationSurvivesRestartWhenStoreDeletionIsLocked()
    {
        using (ProfileLease lease = ProfileLease.Acquire(_root))
        {
            BooruAccountBinding binding = new(lease, _store);
            Assert.Equal(BooruAccountState.Ready, (await ConfirmAsync(binding)).State);
            _store.Status = SecretStoreStatus.Locked;
            Assert.Equal(new(BooruAccountState.Anonymous, SecretStoreStatus.Locked), await binding.RevokeAsync());
        }

        _store.Status = SecretStoreStatus.Available;
        using ProfileLease reopened = ProfileLease.Acquire(_root);
        Assert.Single(_store.Entries);
        await AssertAttemptAsync(Capture(new(reopened, _store)), expectedKey: null);
        Assert.Equal(0, _store.ReadCount);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("replace")]
    [InlineData("profile")]
    [InlineData("plugin")]
    public async Task ChangingBindingRefusesRetriesAndLateResults(string change)
    {
        using ProfileLease lease = ProfileLease.Acquire(_root);
        BooruAccountBinding binding = new(lease, _store);
        Assert.Equal(BooruAccountState.Ready, (await ConfirmAsync(binding)).State);
        BooruAccountBinding.Operation operation = Capture(binding);
        using var message = new HttpRequestMessage(HttpMethod.Get, Request.Url);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<BooruAccountAttempt> pending = operation.SendAttemptAsync(Request, HttpHostPurpose.Metadata, message,
            async (request, _) =>
            {
                entered.TrySetResult();
                await finish.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            }, default);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            switch (change)
            {
                case "revoke": await binding.RevokeAsync(); break;
                case "replace": await ConfirmAsync(binding, Replacement); break;
                case "profile": binding.Retire(); break;
                case "plugin": binding.InvalidateOperations(); break;
            }
        }
        finally { finish.TrySetResult(); }

        await Assert.ThrowsAsync<BooruAccountChangedException>(() => pending);
        Assert.Null(message.Headers.Authorization);
        Assert.False(operation.TryPublish(() => throw new InvalidOperationException()));
        using HttpRequestMessage retry = new(HttpMethod.Get, Request.Url);
        await Assert.ThrowsAsync<BooruAccountChangedException>(() => operation.SendAttemptAsync(
            Request, HttpHostPurpose.Metadata, retry, RejectSend, default));
        Assert.Equal(1, _store.ReadCount);
    }

    [Fact]
    public async Task MarkedKeyIsScopedToTheSendAndEchoesAndExceptionsCanBeWithheld()
    {
        using ProfileLease lease = ProfileLease.Acquire(_root);
        BooruAccountBinding binding = new(lease, _store);
        Assert.Equal(BooruAccountState.Ready, (await ConfirmAsync(binding)).State);
        PluginOperationContext context = Context(Search);
        BooruAccountBinding.Operation operation = Assert.IsType<BooruAccountBinding.Operation>(
            binding.CaptureOperation(Scope(), Source, context));
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Account + ":" + Marker));

        using HttpRequestMessage message = new(HttpMethod.Get, Request.Url);
        using BooruAccountAttempt attempt = await operation.SendAttemptAsync(Request, HttpHostPurpose.Metadata, message,
            (request, _) =>
            {
                Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
                Assert.Equal(encoded, request.Headers.Authorization?.Parameter);
                Assert.DoesNotContain(Marker, request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(Marker)
                });
            }, default);
        Assert.True(attempt.ContainsCredential(await attempt.Response.Content.ReadAsByteArrayAsync()));
        Assert.True(attempt.ContainsCredential("Basic " + encoded));
        Assert.False(attempt.ContainsCredential("normal metadata"));
        Assert.Null(message.Headers.Authorization);
        Assert.DoesNotContain(Marker, JsonSerializer.Serialize(Request), StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, JsonSerializer.Serialize(context), StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, JsonSerializer.Serialize(Search), StringComparison.Ordinal);

        using HttpRequestMessage failed = new(HttpMethod.Get, Request.Url);
        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => operation.SendAttemptAsync(
            Request, HttpHostPurpose.Metadata, failed, (_, _) => throw new HttpRequestException(Marker), default));
        Assert.DoesNotContain(Marker, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Null(failed.Headers.Authorization);
    }

    // Run each phase in a fresh test process, in this order, with the same GUID and isolated OS vault.
    [NativeStoreFact]
    public void Native01Write()
    {
        IBooruSecretStore store = NativeBooruSecretStore.Create();
        Assert.Equal(SecretStoreStatus.Missing, ReadNative(store, expected: null));
        Assert.Equal(SecretStoreStatus.Available, store.Write(NativeTarget(), Encoding.UTF8.GetBytes(Marker), default));
    }

    [NativeStoreFact]
    public void Native02ReopenAndReplace()
    {
        IBooruSecretStore store = NativeBooruSecretStore.Create();
        Assert.Equal(SecretStoreStatus.Available, ReadNative(store, Marker));
        Assert.Equal(SecretStoreStatus.Available, store.Write(NativeTarget(), Encoding.UTF8.GetBytes(Replacement), default));
    }

    [NativeStoreFact]
    public void Native03ReopenAndDelete()
    {
        IBooruSecretStore store = NativeBooruSecretStore.Create();
        Assert.Equal(SecretStoreStatus.Available, ReadNative(store, Replacement));
        Assert.Equal(SecretStoreStatus.Available, store.Delete(NativeTarget(), default));
    }

    [NativeStoreFact]
    public void Native04ConfirmDeletion() =>
        Assert.Equal(SecretStoreStatus.Missing, ReadNative(NativeBooruSecretStore.Create(), expected: null));

    private static string NativeTarget() =>
        "MeowChan.Booru.ApiKey.Test." + Guid.ParseExact(Environment.GetEnvironmentVariable(NativeRun)!, "N").ToString("N");

    private static SecretStoreStatus ReadNative(IBooruSecretStore store, string? expected)
    {
        using SecretStoreRead read = store.Read(NativeTarget(), default);
        if (expected is null) Assert.Null(read.Bytes);
        else Assert.Equal(Encoding.UTF8.GetBytes(expected), read.Bytes);
        return read.Status;
    }

    public sealed class NativeStoreFactAttribute : FactAttribute
    {
        public NativeStoreFactAttribute()
        {
            if (!Guid.TryParseExact(Environment.GetEnvironmentVariable(NativeRun), "N", out Guid id) || id == Guid.Empty)
                Skip = "Native evidence requires a separate test process per phase and an isolated OS vault.";
        }
    }

    private static Task<BooruAccountChange> ConfirmAsync(BooruAccountBinding binding, string key = Marker) =>
        binding.ConfirmAsync(Scope(), Account, Encoding.UTF8.GetBytes(key));

    private static BooruAccountScope Scope(string plugin = "danbooru-plugin", string revision = "first",
        string source = "danbooru", string instance = "default", string origin = "https://metadata.test/") =>
        BooruAccountScope.Create(plugin, revision, source, instance, new Uri(origin));

    private static PluginOperationContext Context(BooruSearchRequest request) =>
        new PluginOperationContext("resource", 1).BindBooruCall(request, default);

    private static BooruAccountBinding.Operation Capture(BooruAccountBinding binding, BooruAccountScope? scope = null) =>
        Assert.IsType<BooruAccountBinding.Operation>(binding.CaptureOperation(scope ?? Scope(), Source, Context(Search)));

    private static Task<HttpResponseMessage> RejectSend(HttpRequestMessage request, CancellationToken token) =>
        throw new InvalidOperationException("The request crossed the transport boundary.");

    private static async Task AssertAttemptAsync(BooruAccountBinding.Operation operation, string? expectedKey)
    {
        using HttpRequestMessage message = new(HttpMethod.Get, Request.Url);
        using BooruAccountAttempt attempt = await operation.SendAttemptAsync(Request, HttpHostPurpose.Metadata, message,
            (request, _) =>
            {
                if (expectedKey is null) Assert.Null(request.Headers.Authorization);
                else Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(Account + ":" + expectedKey)),
                    request.Headers.Authorization?.Parameter);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
            }, default);

        Assert.Equal(expectedKey is not null, attempt.IsAuthenticated);
        Assert.Null(message.Headers.Authorization);
        Assert.True(operation.TryPublish(() => { }));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FakeStore : IBooruSecretStore
    {
        private int _readCount;
        internal int ReadCount => _readCount;
        internal ConcurrentDictionary<string, byte[]> Entries { get; } = new();
        internal SecretStoreStatus Status { get; set; } = SecretStoreStatus.Available;

        public SecretStoreRead Read(string target, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            if (Status != SecretStoreStatus.Available) return new(Status);
            return Entries.TryGetValue(target, out byte[]? bytes)
                ? new(SecretStoreStatus.Available, (byte[])bytes.Clone())
                : new(SecretStoreStatus.Missing);
        }

        public SecretStoreStatus Write(string target, byte[] secret, CancellationToken cancellationToken)
        {
            if (Status == SecretStoreStatus.Available) Entries[target] = (byte[])secret.Clone();
            return Status;
        }

        public SecretStoreStatus Delete(string target, CancellationToken cancellationToken) =>
            Status != SecretStoreStatus.Available ? Status
                : Entries.TryRemove(target, out _) ? SecretStoreStatus.Available : SecretStoreStatus.Missing;
    }
}
