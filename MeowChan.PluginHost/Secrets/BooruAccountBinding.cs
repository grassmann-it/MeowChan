using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;
using MeowChan.Secrets;

namespace MeowChan.PluginHost.Secrets;

internal enum BooruAccountState
{
    Anonymous,
    Ready,
    ConfirmationRequired,
    StoreLocked,
    StoreUnavailable,
    StoreDisabled,
    ProfileUnavailable
}

internal readonly record struct BooruAccountChange(BooruAccountState State, SecretStoreStatus? Cleanup = null);

internal sealed class BooruAccountChangedException() : InvalidOperationException("The Booru account operation is no longer current.");

/// <summary>One host-owned account selection for a leased profile. Instantiate once per profile session.</summary>
internal sealed class BooruAccountBinding(ProfileLease lease, IBooruSecretStore store, bool enabled = true)
{
    internal const int ScopeFingerprintBytes = 32;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly object _gate = new();
    private readonly object _mutations = new();
    private long _generation;
    private bool _enabled = enabled;
    private bool _active = true;
    private bool _changing;
    private bool _revocationPending;
    private bool IsLeaseHeld => lease.IsHeld;

    internal void SetEnabled(bool value)
    {
        lock (_gate)
        {
            if (_enabled == value) return;
            _enabled = value;
            _generation++;
        }
    }

    /// <summary>Fences pending operations after an owner or source lifecycle change.</summary>
    internal void InvalidateOperations()
    {
        lock (_gate) _generation++;
    }

    internal void Retire()
    {
        lock (_gate)
        {
            _active = false;
            _generation++;
        }
    }

    internal Task<BooruAccountState> GetStateAsync(BooruAccountScope scope, CancellationToken token = default) =>
        Task.Run(() =>
        {
            long generation;
            Guid? reference;
            lock (_gate)
            {
                if (!_active || !lease.IsHeld) return BooruAccountState.ProfileUnavailable;
                if (!_enabled) return BooruAccountState.StoreDisabled;
                if (_revocationPending) return BooruAccountState.ProfileUnavailable;
                if (!TryReadReference(out reference)) return BooruAccountState.ProfileUnavailable;
                generation = _generation;
            }

            using SecretStoreRead read = Read(scope, reference, token, out BooruAccountState state);
            lock (_gate)
            {
                if (generation != _generation || !lease.IsHeld) return BooruAccountState.ProfileUnavailable;
                return state;
            }
        }, token);

    /// <summary>Publishes a fresh selection only after the OS store has accepted the new key.</summary>
    internal Task<BooruAccountChange> ConfirmAsync(BooruAccountScope scope, string account, byte[] apiKey,
        CancellationToken token = default) =>
        Task.Run(() =>
        {
            byte[] secret = Encode(scope, account, apiKey);
            try { return ChangeAccount(revoke: false, secret, token); }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }, token);

    /// <summary>Detaches the selection even when a locked OS store prevents immediate cleanup.</summary>
    internal Task<BooruAccountChange> RevokeAsync(CancellationToken token = default) =>
        Task.Run(() => ChangeAccount(revoke: true, null, token), token);

    private BooruAccountChange ChangeAccount(bool revoke, byte[]? secret, CancellationToken token)
    {
        lock (_mutations)
        {
            token.ThrowIfCancellationRequested();
            long generation;
            Guid? previous;
            lock (_gate)
            {
                if (!_active || !lease.IsHeld || !TryReadReference(out previous))
                    return new(BooruAccountState.ProfileUnavailable);
                if (!revoke && !_enabled) return new(BooruAccountState.StoreDisabled);
                _changing = true;
                generation = ++_generation;
            }

            try
            {
                if (revoke) return Revoke(previous);
                return Confirm(previous, secret!, generation, token);
            }
            catch (Exception error) when (IsProfileFailure(error))
            {
                return new(BooruAccountState.ProfileUnavailable);
            }
            finally
            {
                lock (_gate)
                {
                    _changing = false;
                    _generation++;
                }
            }
        }
    }

    private BooruAccountChange Revoke(Guid? previous)
    {
        string? oldAddress = null;
        if (previous is Guid id)
        {
            try { oldAddress = BooruSecretReference.Address(lease, id); }
            catch (Exception error) when (IsProfileFailure(error)) { }
        }

        bool detached = false;
        try
        {
            BooruSecretReference.Change(lease, previous, null);
            detached = true;
        }
        catch (Exception error) when (IsProfileFailure(error)) { }

        SecretStoreStatus? cleanup = oldAddress is null ? null : store.Delete(oldAddress, CancellationToken.None);
        bool revoked = detached || cleanup is SecretStoreStatus.Available or SecretStoreStatus.Missing;
        lock (_gate) _revocationPending = !revoked;
        return new(revoked ? BooruAccountState.Anonymous : BooruAccountState.ProfileUnavailable, cleanup);
    }

    private BooruAccountChange Confirm(Guid? previous, byte[] secret, long generation, CancellationToken token)
    {
        ProfileInstanceIdentity.ReadOrCreate(lease);
        string? oldAddress = previous is Guid id ? BooruSecretReference.Address(lease, id) : null;
        Guid next = Guid.NewGuid();
        string target = BooruSecretReference.Address(lease, next);
        bool committed = false;
        try
        {
            SecretStoreStatus written = store.Write(target, secret, token);
            token.ThrowIfCancellationRequested();
            if (written != SecretStoreStatus.Available) return new(StateOf(written));

            lock (_gate)
            {
                if (!_active || !_enabled || generation != _generation)
                    return new(BooruAccountState.ProfileUnavailable);
                BooruSecretReference.Change(lease, previous, next);
                _revocationPending = false;
                committed = true;
            }
        }
        finally
        {
            if (!committed) store.Delete(target, CancellationToken.None);
        }

        SecretStoreStatus? cleanup = oldAddress is null ? null : store.Delete(oldAddress, CancellationToken.None);
        return new(BooruAccountState.Ready, cleanup);
    }

    /// <summary>Captures the account generation before dispatching the host's typed operation to a worker.</summary>
    internal Operation? CaptureOperation(BooruAccountScope scope, SourceDescription source, PluginOperationContext context)
    {
        if (context.BooruCall is not BooruCallBinding call || scope.SourceId != source.SourceId
            || BooruEndpointPolicy.SourceOf(call.Request) != scope.SourceId)
            return null;

        lock (_gate)
        {
            if (!_active || !lease.IsHeld || _changing || call.Lifetime.IsCancellationRequested) return null;
            bool referenceReadable = TryReadReference(out Guid? reference);
            return new Operation(this, scope, source, call, _generation, reference,
                referenceReadable && _enabled && !_revocationPending);
        }
    }

    private bool TryReadReference(out Guid? reference)
    {
        try
        {
            reference = BooruSecretReference.Read(lease);
            return true;
        }
        catch (Exception error) when (IsProfileFailure(error))
        {
            reference = null;
            return false;
        }
    }

    private SecretStoreRead Read(BooruAccountScope scope, Guid? reference, CancellationToken token,
        out BooruAccountState state)
    {
        if (reference is not Guid id)
        {
            state = BooruAccountState.Anonymous;
            return new(SecretStoreStatus.Missing);
        }

        string target;
        try { target = BooruSecretReference.Address(lease, id); }
        catch (Exception error) when (IsProfileFailure(error))
        {
            state = BooruAccountState.ProfileUnavailable;
            return new(SecretStoreStatus.Unavailable);
        }

        SecretStoreRead read = store.Read(target, token);
        if (read.Status != SecretStoreStatus.Available)
        {
            state = StateOf(read.Status);
            return read;
        }

        ReadOnlySpan<byte> bytes = read.Bytes;
        if (bytes.Length is <= ScopeFingerprintBytes or > SecretStoreRead.MaximumBytes
            || !bytes[..ScopeFingerprintBytes].SequenceEqual(scope.Fingerprint())
            || !ValidBasic(bytes[ScopeFingerprintBytes..]))
        {
            read.Dispose();
            state = BooruAccountState.ConfirmationRequired;
            return new(SecretStoreStatus.Missing);
        }
        state = BooruAccountState.Ready;
        return read;
    }

    private static BooruAccountState StateOf(SecretStoreStatus status) => status switch
    {
        SecretStoreStatus.Available => BooruAccountState.Ready,
        SecretStoreStatus.Missing => BooruAccountState.ConfirmationRequired,
        SecretStoreStatus.Locked => BooruAccountState.StoreLocked,
        SecretStoreStatus.Disabled => BooruAccountState.StoreDisabled,
        _ => BooruAccountState.StoreUnavailable
    };

    private static bool IsProfileFailure(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException;

    private static byte[] Encode(BooruAccountScope scope, string account, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Length > SecretStoreRead.MaximumBytes
            || account.Contains(':') || account.Any(char.IsControl) || !ValidKey(key))
            throw new ArgumentException("The Booru account or API key is invalid.");

        int accountBytes;
        try { accountBytes = StrictUtf8.GetByteCount(account); }
        catch (EncoderFallbackException) { throw new ArgumentException("The Booru account name is invalid."); }

        int size = ScopeFingerprintBytes + accountBytes + 1 + key.Length;
        if (size > SecretStoreRead.MaximumBytes) throw new ArgumentException("The Booru credential is too large.");
        byte[] output = new byte[size];
        scope.Fingerprint().CopyTo(output, 0);
        StrictUtf8.GetBytes(account.AsSpan(), output.AsSpan(ScopeFingerprintBytes, accountBytes));
        output[ScopeFingerprintBytes + accountBytes] = (byte)':';
        key.CopyTo(output, ScopeFingerprintBytes + accountBytes + 1);
        return output;
    }

    private static bool ValidBasic(ReadOnlySpan<byte> value)
    {
        int separator = value.IndexOf((byte)':');
        return separator > 0 && ValidKey(value[(separator + 1)..]);
    }

    private static bool ValidKey(ReadOnlySpan<byte> key) =>
        key.Length is > 0 and <= SecretStoreRead.MaximumBytes && key.IndexOfAnyExceptInRange((byte)'!', (byte)'~') < 0;

    internal sealed class Operation
    {
        private readonly BooruAccountBinding _owner;
        private readonly BooruAccountScope _scope;
        private readonly SourceDescription _source;
        private readonly BooruCallBinding _call;
        private readonly long _generation;
        private readonly Guid? _reference;
        private readonly bool _mayReadStore;

        internal Operation(BooruAccountBinding owner, BooruAccountScope scope, SourceDescription source,
            BooruCallBinding call, long generation, Guid? reference, bool mayReadStore)
        {
            _owner = owner;
            _scope = scope;
            _source = source;
            _call = call;
            _generation = generation;
            _reference = reference;
            _mayReadStore = mayReadStore;
        }

        /// <summary>Atomically admits a final in-process publication against revocation.</summary>
        internal bool TryPublish(Action publish)
        {
            lock (_owner._gate)
            {
                if (!IsCurrentUnderLock()) return false;
                publish();
                return true;
            }
        }

        internal bool IsCurrent()
        {
            lock (_owner._gate) return IsCurrentUnderLock();
        }

        private bool IsCurrentUnderLock()
        {
            if (!_owner._active || !_owner.IsLeaseHeld || _owner._changing || _owner._generation != _generation
                || _call.Lifetime.IsCancellationRequested) return false;
            return !_owner.TryReadReference(out Guid? current) ? _reference is null : current == _reference;
        }

        private void EnsureCurrent()
        {
            if (!IsCurrentUnderLock()) throw new BooruAccountChangedException();
        }

        /// <summary>The delegate starts a single host-owned HTTPS request with redirects, cookies and header logging disabled.</summary>
        internal async Task<BooruAccountAttempt> SendAttemptAsync(HostHttpRequest request, HttpHostPurpose purpose,
            HttpRequestMessage message, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
            CancellationToken token)
        {
            if (purpose != HttpHostPurpose.Metadata
                || BooruEndpointPolicy.Bind(request, _source, _call.Request) is not HostHttpRequest canonical
                || !_scope.MatchesOrigin(canonical.Url)
                || message.Method != HttpMethod.Get
                || message.RequestUri is not { IsAbsoluteUri: true } uri || uri.AbsoluteUri != canonical.Url.AbsoluteUri
                || (message.Headers.Host is string host
                    && !host.Equals(PluginRequestHeaders.CanonicalHost(canonical.Url), StringComparison.OrdinalIgnoreCase))
                || message.Headers.Contains("Authorization")
                || message.Headers.Contains("Proxy-Authorization") || message.Headers.Contains("Cookie")
                || message.Content is not null)
                throw new BooruAccountChangedException();

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _call.Lifetime);
            lock (_owner._gate) EnsureCurrent();
            SecretStoreRead? read = null;
            HttpResponseMessage? response = null;
            string? basic = null;
            try
            {
                if (_mayReadStore && _reference is not null)
                    read = await Task.Run(() => _owner.Read(_scope, _reference, linked.Token, out _), linked.Token).ConfigureAwait(false);

                Task<HttpResponseMessage> pending;
                lock (_owner._gate)
                {
                    EnsureCurrent();
                    linked.Token.ThrowIfCancellationRequested();
                    if (read is { Status: SecretStoreStatus.Available, Bytes: { } bytes })
                    {
                        basic = Convert.ToBase64String(bytes.AsSpan(ScopeFingerprintBytes));
                        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                    }
                    pending = send(message, linked.Token);
                }

                response = await pending.ConfigureAwait(false);
                lock (_owner._gate) EnsureCurrent();
                linked.Token.ThrowIfCancellationRequested();
                BooruAccountAttempt attempt = new(response, read, basic);
                response = null;
                read = null;
                return attempt;
            }
            catch (OperationCanceledException) when (basic is not null)
            {
                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(linked.Token);
            }
            catch (Exception error) when (basic is not null && error is not BooruAccountChangedException)
            {
                throw new HttpRequestException("Authenticated Booru metadata transport failed.");
            }
            finally
            {
                if (basic is not null) message.Headers.Authorization = null;
                try { response?.Dispose(); }
                finally { read?.Dispose(); }
            }
        }
    }
}
