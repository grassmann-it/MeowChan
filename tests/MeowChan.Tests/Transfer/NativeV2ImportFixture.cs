using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Real writer -> validator -> retained stage -> owner-issued plan. No SQL seeding,
// provider, decoder, network, personal profile, or reflection-based admission.
internal sealed class NativeV2ImportFixture : IAsyncDisposable
{
    internal static readonly ResourceId Id = new("4chan", "b", "7001");
    internal const string Url = "https://boards.4chan.org/b/thread/7001";
    internal static readonly DateTimeOffset At = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    internal static readonly byte[] Original = Encoding.UTF8.GetBytes("synthetic W32b3 original; not a decoder fixture");
    internal static readonly byte[] Preview = Encoding.UTF8.GetBytes("synthetic W32b3 preview; not a decoder fixture");
    internal Workspace Files { get; }
    internal ProfileLease Lease { get; }
    internal ArchiveCatalog Catalog { get; }
    internal NativeTransferImportStage Stage { get; }
    internal NativeMediaImportPlan Plan { get; }
    internal MediaStore Store { get; } = new();
    private bool _ownerClosed;

    private NativeV2ImportFixture(Workspace files, ProfileLease lease, ArchiveCatalog catalog,
        NativeTransferImportStage stage, NativeMediaImportPlan plan)
    { Files = files; Lease = lease; Catalog = catalog; Stage = stage; Plan = plan; }

    internal static async Task<NativeV2ImportFixture> CreateAsync(NativeTransferCatalog? content = null,
        byte[][]? payloads = null)
    {
        Workspace files = new();
        ProfileLease? lease = null;
        ArchiveCatalog? catalog = null;
        NativeTransferImportStage? stage = null;
        try
        {
            lease = ProfileLease.Acquire(files.Profile);
            catalog = await ArchiveCatalog.OpenAsync(files.Profile, lease);
            stage = await StageAsync(files, content ?? Content(), payloads ?? [Original, Preview]);
            NativeMediaImportPreparation prepared = await catalog.PrepareNativeMediaImportAsync(stage);
            Assert.Equal(NativeMediaImportError.None, prepared.Error);
            NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(prepared.Plan);
            Assert.Equal(2, plan.PlanVersion);
            Assert.NotNull(plan.Representation);
            return new(files, lease, catalog, stage, plan);
        }
        catch
        {
            if (stage != null) await stage.DisposeAsync();
            if (catalog != null) await catalog.DisposeAsync();
            lease?.Dispose(); files.Dispose(); throw;
        }
    }

    // Storage-owner tests only: mirrors existing NativeMediaImportTests setup, not
    // a public Registry startup proof. Restart tests must run the real startup guard.
    internal ValueTask<ProfileAdmissionToken> ExclusiveAsync()
    {
        if (Lease.Admission.Phase == ProfileAdmissionPhase.Starting) Lease.Admission.CompleteStartup();
        return Lease.Admission.EnterExclusiveAsync();
    }

    internal Task<ProfileExclusiveOutcome<NativeMediaImportCommitResult>> CommitAsync(ProfileAdmissionToken admission,
        CancellationToken cancellationToken = default, Action<NativeMediaImportPhase>? checkpoint = null,
        Action? beforeAdmission = null)
        => Catalog.CommitNativeMediaImportAdmittedAsync(admission, Plan, Store, cancellationToken, checkpoint, beforeAdmission);

    internal async Task CloseOwnerAsync()
    {
        if (_ownerClosed) return;
        _ownerClosed = true;
        await Stage.DisposeAsync();
        await Catalog.DisposeAsync();
        Lease.Dispose();
    }

    public async ValueTask DisposeAsync()
    { await CloseOwnerAsync(); Files.Dispose(); }

    internal static NativeTransferCatalog Content(bool originalContained = true, bool derived = true,
        bool retainedOnly = false, bool presence = true)
    {
        TransferAttachment attachment = Attachment(retainedOnly ? "999" : "15",
            originalContained ? TransferContentStatus.Contained : TransferContentStatus.Omitted, derived);
        ImmutableArray<TransferPost> posts = retainedOnly ? [] : [Post("15", attachment)];
        if (!retainedOnly && presence)
            posts = posts.Add(new(new CapturedPost("16", 1, PostFieldPresence.Attachments, Attachments: null), [])
                { AttachmentsWereNull = true }).Add(Post("17"));
        TransferThread thread = new(Id, Url, "synthetic W32b3 thread", ThreadSourceState.NotFound,
            At, At, 1, true, posts);
        if (retainedOnly) thread = thread with { RetainedBindings = [new("999", attachment)] };
        return new(At, [thread]);
    }

    internal static TransferAttachment Attachment(string post = "15",
        TransferContentStatus status = TransferContentStatus.Contained, bool derived = true)
    {
        MediaProviderMetadata provider = new("media-" + post, "original",
            $"https://i.4cdn.org/b/{post}.webm", "synthetic.webm", Original.Length, Md5(Original), Hash(Original));
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url,
            provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        { ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, post, 0, provider) };
        // Nullable incoming SHA1 deliberately stays unknown despite measured payload SHA1.
        TransferOriginalByteVersion version = new(Original.LongLength, Hash(Original), Md5(Original), null);
        return new(metadata, TransferMediaRole.Original, status,
            status == TransferContentStatus.Contained ? Hash(Original) : null, null)
        {
            OriginalByteVersion = version,
            DerivedRepresentations = derived ? [new(TransferDerivedRepresentationKind.Preview,
                metadata.AttachmentRevisionKey!, version, TransferContentStatus.Contained, Hash(Preview))] : []
        };
    }

    internal static TransferPost Post(string id, params TransferAttachment[] attachments)
        => new(new CapturedPost(id, 1, PostFieldPresence.All, At, "", "", "synthetic post", false,
            attachments.Select(a => a.Metadata).ToArray()), attachments.ToImmutableArray());

    internal static async Task<NativeTransferImportStage> StageAsync(Workspace files,
        NativeTransferCatalog content, params byte[][] payloads)
    {
        using MemoryStream package = new();
        TransferPayloadSource[] sources = payloads.DistinctBy(Hash).Select(bytes => new TransferPayloadSource(
            new(bytes.Length, Hash(bytes)), _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)))).ToArray();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(package, content, sources, Guid.NewGuid());
        Assert.True(written.IsComplete, written.ToString());
        Assert.True(written.Package!.IsChanV4);
        package.Position = 0;
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(package, files.StageRoot);
        Assert.Equal(NativeTransferStageError.None, staged.Error);
        NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
        Assert.True(stage.Package.IsChanV4);
        Assert.Null(stage.Package.PrivateMetadata);
        return stage;
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
    internal static Dictionary<string, string> PayloadFiles(string root) => Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).ToDictionary(p => Path.GetRelativePath(root, p), p => Hash(File.ReadAllBytes(p)), StringComparer.Ordinal);

    internal sealed class Workspace : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("meowchan-v2-import-tests-").FullName;
        internal string Profile { get; }
        internal string StageRoot { get; }
        internal Workspace()
        { Profile = Directory.CreateDirectory(Path.Combine(Root, "profile")).FullName;
            StageRoot = Directory.CreateDirectory(Path.Combine(Root, "stage")).FullName; }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
