using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests.GoalFixtures;

internal sealed record Gp02SyntheticMediaFile(string Path, ReadOnlyMemory<byte> Bytes);

internal sealed record Gp02SyntheticSource(
    string ProfileDirectory,
    string MediaDirectory,
    CapturedThreadSnapshot Snapshot,
    IReadOnlyList<string> ExpectedPostIds,
    IReadOnlyList<Gp02SyntheticMediaFile> MediaFiles,
    IReadOnlyList<string> MissingOriginalPaths);

/// <summary>
/// Builds two independent, completely synthetic archive profiles for the bounded GP-02
/// overlap baseline. The fixture never reads a user profile, provider, or personal media.
/// </summary>
internal sealed class Gp02SyntheticArchiveBaseline : IAsyncDisposable
{
    public static readonly ResourceId ThreadId = new("gp02-synthetic", "synthetic", "7001");
    private static readonly DateTimeOffset CapturedA = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CapturedB = new(2026, 11, 1, 9, 30, 0, TimeSpan.Zero);

    private Gp02SyntheticArchiveBaseline()
    {
    }

    public TempDirectory ProfileADirectory { get; } = new();
    public TempDirectory ProfileBDirectory { get; } = new();
    public Gp02SyntheticSource SourceA { get; private set; } = null!;
    public Gp02SyntheticSource SourceB { get; private set; } = null!;

    public static async Task<Gp02SyntheticArchiveBaseline> CreateAsync()
    {
        Gp02SyntheticArchiveBaseline fixture = new();
        fixture.SourceA = CreateSource(
            fixture.ProfileADirectory, "A", ThreadSourceState.Archived, CapturedA,
            Enumerable.Range(1, 20).ToArray());
        fixture.SourceB = CreateSource(
            fixture.ProfileBDirectory, "B", ThreadSourceState.NotFound, CapturedB,
            Enumerable.Range(15, 16).Append(35).ToArray());

        try
        {
            await CommitAsync(fixture.SourceA);
            await CommitAsync(fixture.SourceB);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private static Gp02SyntheticSource CreateSource(
        TempDirectory profile, string label, ThreadSourceState sourceState,
        DateTimeOffset capturedAt, IReadOnlyCollection<int> postNumbers)
    {
        string mediaDirectory = Path.Combine(profile.Path, "media");
        string originalDirectory = Path.Combine(mediaDirectory, "original");
        Dictionary<int, CapturedAttachment> attachments = [];
        List<Gp02SyntheticMediaFile> mediaFiles = [];
        List<string> missingOriginals = [];

        foreach (int number in PresentAttachmentNumbers(label))
        {
            (CapturedAttachment attachment, Gp02SyntheticMediaFile file) =
                PresentAttachment(originalDirectory, number);
            attachments.Add(number, attachment);
            mediaFiles.Add(file);
        }

        foreach (int number in MissingAttachmentNumbers(label))
        {
            string path = OriginalPath(originalDirectory, number);
            attachments.Add(number, MetadataOnlyAttachment(originalDirectory, number));
            missingOriginals.Add(path);
        }

        CapturedPost[] posts = [.. postNumbers.Select(number => CreatePost(
            label, capturedAt, number, attachments.GetValueOrDefault(number)))];
        CapturedThreadSnapshot snapshot = new(
            ThreadId,
            "https://archive.gp02.invalid/synthetic/thread/7001",
            $"GP-02 synthetic source {label}",
            sourceState,
            capturedAt,
            mediaDirectory,
            posts);

        return new Gp02SyntheticSource(
            profile.Path,
            mediaDirectory,
            snapshot,
            [.. postNumbers.Select(PostId)],
            mediaFiles,
            missingOriginals);
    }

    private static CapturedPost CreatePost(
        string label, DateTimeOffset capturedAt, int number, CapturedAttachment? attachment)
    {
        string text = PostId(number);
        bool partial = (label == "A" && number == 2) || (label == "B" && number == 25);
        if (partial)
        {
            return new CapturedPost(
                text,
                number,
                PostFieldPresence.Html | PostFieldPresence.Subject,
                Subject: $"Partial synthetic {label} subject {text}",
                Html: $"<p>Partial GP-02 synthetic {label} post {text}</p>");
        }

        return new CapturedPost(
            text,
            number,
            PostFieldPresence.All,
            PostedAt: capturedAt.AddSeconds(number),
            Subject: $"Synthetic {label} subject {text}",
            Author: $"gp02-{label.ToLowerInvariant()}",
            Html: $"<p>GP-02 synthetic {label} post {text}</p>",
            Attachments: attachment == null ? [] : [attachment]);
    }

    private static IEnumerable<int> PresentAttachmentNumbers(string label) =>
        label == "A" ? [1, 17] : [17, 35];

    private static IEnumerable<int> MissingAttachmentNumbers(string label) =>
        label == "A" ? [3] : [28];

    private static (CapturedAttachment Attachment, Gp02SyntheticMediaFile File) PresentAttachment(
        string originalDirectory, int number)
    {
        byte[] bytes = SyntheticBytes(number);
        return (Attachment(originalDirectory, number, bytes),
            new Gp02SyntheticMediaFile(OriginalPath(originalDirectory, number), bytes));
    }

    private static CapturedAttachment MetadataOnlyAttachment(string originalDirectory, int number) =>
        Attachment(originalDirectory, number, SyntheticBytes(number));

    private static CapturedAttachment Attachment(string originalDirectory, int number, byte[] bytes)
    {
        string mediaId = MediaId(number);
        return new CapturedAttachment(
            0,
            mediaId,
            Url: $"https://media.gp02.invalid/original/{mediaId}.bin",
            OriginalFileName: $"{mediaId}.bin",
            PlannedPath: OriginalPath(originalDirectory, number),
            ExpectedSize: bytes.Length,
            ExpectedMd5: Convert.ToBase64String(MD5.HashData(bytes)))
        {
            ExpectedSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        };
    }

    private static byte[] SyntheticBytes(int number) =>
        Encoding.UTF8.GetBytes($"GP-02 synthetic deterministic post {number:000}\n");

    private static string OriginalPath(string originalDirectory, int number) =>
        Path.Combine(originalDirectory, $"{MediaId(number)}.bin");

    private static string MediaId(int number) => $"gp02-post-{number:000}-original";

    private static string PostId(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static async Task CommitAsync(Gp02SyntheticSource source)
    {
        using ProfileLease lease = ProfileLease.Acquire(source.ProfileDirectory);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(source.ProfileDirectory, lease);
        await catalog.CaptureAsync(source.Snapshot);

        foreach (Gp02SyntheticMediaFile file in source.MediaFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
            await File.WriteAllBytesAsync(file.Path, file.Bytes);
        }
    }

    public ValueTask DisposeAsync()
    {
        ProfileADirectory.Dispose();
        ProfileBDirectory.Dispose();
        return ValueTask.CompletedTask;
    }
}
