using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Transfer
{

/// <summary>
/// The non-serializable live authority for one private native Apply preparation.
///
/// The request and its hashes are inert evidence. Mutation authority comes only
/// from the exact owner-issued import plan, the live owner/profile lease, and the
/// exclusive admission token retained by this object. A request or journal replay
/// can therefore reproduce the evidence but not this capability.
/// </summary>
[JsonConverter(typeof(NativePrivateApplyCapabilityJsonConverter))]
internal sealed class NativePrivateApplyCapability : IDisposable
{
    private static readonly ConditionalWeakTable<ProfileLease, LeaseIncarnation> LeaseIncarnations = new();
    private static readonly ConditionalWeakTable<ProfileAdmissionToken, CapabilityMarker> AdmissionClaims = new();
    private readonly object _sync = new();
    private readonly ArchiveCatalog _owner;
    private readonly NativeMediaImportPlan _plan;
    private readonly ProfileLease _lease;
    private readonly ProfileAdmissionToken _admission;
    private readonly NativePrivateApplyRequest _request;
    private bool _disposed;
    private bool _admissionReleased;

    private NativePrivateApplyCapability(ArchiveCatalog owner, NativeMediaImportPlan plan,
        ProfileLease lease, ProfileAdmissionToken admission, NativePrivateApplyRequest request)
    {
        _owner = owner;
        _plan = plan;
        _lease = lease;
        _admission = admission;
        _request = request;
    }

    internal Guid OperationId => _request.OperationId;
    internal string ContentPlanSha256 => _request.ContentPlanSha256;
    internal string RequestSha256 => _request.RequestSha256;
    internal NativePrivateMetadataSelection Selection => _request.Selection;
    internal Guid ProfileIncarnation => _request.Binding.ProfileIncarnation;
    internal string TargetFenceSha256 => _request.Binding.TargetFenceSha256;

    // The request remains internal and immutable; it is intentionally not exposed
    // through a serializable snapshot or journal projection.
    internal NativePrivateApplyRequest Request => _request;

    /// <summary>
    /// Creates a capability only from an owner-issued plan and a live exclusive
    /// profile admission. The target fence is derived from the owner plan and the
    /// complete selected store-fence set; callers cannot choose a second hash.
    /// </summary>
    internal static NativePrivateApplyCapability IssueFromOwner(ArchiveCatalog owner,
        ProfileLease lease, ProfileAdmissionToken admission, NativeMediaImportPlan plan,
        NativePrivateMetadataComparisonResult comparison,
        ImmutableArray<NativePrivateApplyStoreFence> storeFences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(comparison);
        cancellationToken.ThrowIfCancellationRequested();

        // These checks are deliberately before any request construction. A copied
        // plan/request/journal value must never get as far as the capability claim.
        owner.EnsureNativePrivateApplyPlan(plan, admission);
        if (admission.Kind != ProfileAdmissionKind.Exclusive)
            throw new InvalidOperationException("Private Apply requires exclusive profile admission.");
        lease.EnsureDirectoryIdentity();
        // This rejects a token from another profile lease and verifies that the
        // owner catalog is still live. The second check below verifies the exact
        // lease object retained by this capability.
        owner.EnsureTransferAdmission(admission);
        lease.RunPrivateStoreCommit(admission, static () => true);

        cancellationToken.ThrowIfCancellationRequested();
        NativePrivateConsentDecision consent = plan.PrivateConsent;
        if (!consent.HasExplicitPrivateConsent || plan.SelectedPrivateMetadata is null
            || !consent.IsIssuedBy(owner) || !consent.MatchesPackage(plan.Package))
            throw new InvalidDataException("A live owner-issued private consent is required.");
        if (comparison.Selection != consent.Selection)
            throw new InvalidDataException("The private comparison selection differs from owner consent.");
        long packageLength = plan.Stage.PackageLength;
        if (packageLength <= 0 || packageLength > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("The staged package length is outside the private Apply bound.");

        LeaseIncarnation incarnation = LeaseIncarnations.GetValue(lease, static _ => new LeaseIncarnation());
        NativeTransferEnvelope envelope = plan.Package.Envelope;
        TransferBlobDescriptor? privateDescriptor = envelope.PrivateMetadata;
        if (privateDescriptor is null)
            throw new InvalidDataException("The private component is missing.");
        string targetFence = ComputeTargetFence(plan, comparison.Selection, storeFences, cancellationToken);
        NativePrivateApplyRequestBinding binding = new(packageLength, envelope.Fingerprint,
            envelope.Catalog.Sha256, privateDescriptor.Length, privateDescriptor.Sha256,
            incarnation.Id, targetFence);
        NativePrivateApplyRequest request = NativePrivateMediaImportApplyAdmission.CreateRequest(
            plan, comparison, binding, storeFences, cancellationToken);

        // One exclusive token can own at most one private capability. Sharing the
        // token between two capability objects would make disposal/replay ambiguous.
        lock (AdmissionClaims)
        {
            if (AdmissionClaims.TryGetValue(admission, out _))
                throw new InvalidOperationException("The exclusive admission already owns a private Apply capability.");
            AdmissionClaims.Add(admission, new CapabilityMarker());
        }

        try
        {
            return new NativePrivateApplyCapability(owner, plan, lease, admission, request);
        }
        catch
        {
            lock (AdmissionClaims) AdmissionClaims.Remove(admission);
            throw;
        }
    }

    /// <summary>
    /// Runs a synchronous executor callback under the same live profile admission
    /// and OS lease. This is the only execution handoff; evidence alone has no
    /// equivalent method. The callback remains responsible for store-owned
    /// prepare/readback and the contract's ordered Journal/receipt protocol.
    /// </summary>
    internal T RunAdmitted<T>(Func<NativePrivateApplyRequest, T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return RunAdmitted((request, _, _) => callback(request));
    }

    // The executor needs to pass the already-owned exclusive token to the
    // store-owned prepare/commit/retirement primitives. Exposing the token only
    // inside this guarded callback keeps the capability as the sole live
    // admission boundary; callers cannot retain or manufacture it.
    internal T RunAdmitted<T>(Func<NativePrivateApplyRequest, ProfileLease, ProfileAdmissionToken, T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_owner.IsNativePrivateApplyPlanIssued(_plan))
                throw new InvalidOperationException("The private Apply plan is no longer owner-issued.");
            _owner.EnsureTransferAdmission(_admission);
            _lease.EnsureDirectoryIdentity();
            return _lease.RunPrivateStoreCommit(_admission, () => callback(_request, _lease, _admission));
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_admissionReleased)
            {
                _admissionReleased = true;
                AdmissionClaims.Remove(_admission);
                _admission.Dispose();
            }
        }
    }

    private static string ComputeTargetFence(NativeMediaImportPlan plan,
        NativePrivateMetadataSelection selection, ImmutableArray<NativePrivateApplyStoreFence> fences,
        CancellationToken cancellationToken)
    {
        if (plan.Instance is not { Length: > 0 } || plan.Generation < 0
            || plan.Resource.Thread is not { Length: > 0 }
            || fences.IsDefault || fences.Length > 3) throw new InvalidDataException("The target fence is invalid.");
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Expected-after fingerprints belong to RequestSha256. Keeping them out
        // of this pre-plan target snapshot is required for the store marker
        // dependency to remain acyclic: ContentPlan -> marker -> after-bytes ->
        // Request. The exact after-image is still bound by NativePrivateApplyRequest.
        AppendText("meowchan-native-private-target-fence-v2");
        AppendText(plan.OperationId.ToString("D")); AppendText(plan.Instance); AppendNumber(plan.Generation);
        AppendText(plan.Resource.Source); AppendText(plan.Resource.Board); AppendText(plan.Resource.Thread);
        AppendNumber(selection.ThreadFavorites ? 1 : 0); AppendNumber(selection.MediaFavorites ? 1 : 0);
        AppendNumber(selection.PostBookmarksAndNotes ? 1 : 0); AppendNumber(fences.Length);
        foreach (NativePrivateApplyStoreFence fence in fences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fence is null) throw new InvalidDataException("The target fence contains a null store fence.");
            AppendNumber((int)fence.Kind); AppendImage(fence.Before);
            AppendNumber(fence.BeforeBackup is null ? 0 : 1);
            if (fence.BeforeBackup is not null) AppendImage(fence.BeforeBackup);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void AppendNumber(long value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            hash.AppendData(bytes);
        }
        void AppendText(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            AppendNumber(bytes.Length); hash.AppendData(bytes);
        }
        void AppendImage(NativePrivateApplyStoreImage image)
        {
            if (image is null) throw new InvalidDataException("The target fence contains a null image.");
            AppendNumber(image.Exists ? 1 : 0); AppendNumber(image.Schema);
            AppendText(image.Instance?.ToString("D") ?? string.Empty); AppendNumber(image.Revision);
            AppendNumber(image.ByteLength); AppendText(image.Sha256 ?? string.Empty);
        }
    }

    private sealed class LeaseIncarnation
    {
        internal Guid Id { get; } = Guid.NewGuid();
    }

    private sealed class CapabilityMarker { }
}

/// <summary>Explicitly refuses wire serialization; no replay can manufacture a live capability.</summary>
internal sealed class NativePrivateApplyCapabilityJsonConverter : JsonConverter<NativePrivateApplyCapability>
{
    public override NativePrivateApplyCapability Read(ref Utf8JsonReader reader,
        Type typeToConvert, JsonSerializerOptions options)
        => throw new JsonException("Native private Apply capabilities are process-local and non-serializable.");

    public override void Write(Utf8JsonWriter writer, NativePrivateApplyCapability value,
        JsonSerializerOptions options)
        => throw new NotSupportedException("Native private Apply capabilities are process-local and non-serializable.");
}

}

namespace MeowChan.Archive
{

/// <summary>Owner-only bridge kept in a new Transfer file; no existing host/store file changes.</summary>
public sealed partial class ArchiveCatalog
{
    internal NativePrivateApplyCapability IssueNativePrivateApplyCapability(
        ProfileLease lease, ProfileAdmissionToken admission, NativeMediaImportPlan plan,
        NativePrivateMetadataComparisonResult comparison,
        ImmutableArray<NativePrivateApplyStoreFence> storeFences,
        CancellationToken cancellationToken = default)
        => NativePrivateApplyCapability.IssueFromOwner(this, lease, admission, plan,
            comparison, storeFences, cancellationToken);

    internal void EnsureNativePrivateApplyPlan(NativeMediaImportPlan plan, ProfileAdmissionToken admission)
    {
        EnsureTransferAdmission(admission);
        if (!IsNativePrivateApplyPlanIssued(plan))
            throw new InvalidOperationException("Private Apply requires the exact owner-issued import plan.");
    }

    internal bool IsNativePrivateApplyPlanIssued(NativeMediaImportPlan plan)
        => plan is not null && ReferenceEquals(plan.Owner, this)
            && _issuedNativeMediaPlans.TryGetValue(plan, out _);
}

}
