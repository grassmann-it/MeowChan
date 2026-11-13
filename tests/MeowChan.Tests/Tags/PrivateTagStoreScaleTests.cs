using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using Xunit.Abstractions;

namespace MeowChan.Tests.Tags;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PrivateTagStoreScaleCollection
{
    public const string Name = "Private tag store scale evidence";
}

/// <summary>Synthetic private tag documents in the store's own v2 shape, sized like a large collection.</summary>
internal static class PrivateTagScaleDocument
{
    internal sealed record Shape(int Tags, int Threads, int Media, int Occurrences, int Local);

    private const string RevisionKey = "att-v2:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    internal static string TagId(int index) => Id(index, 1);

    private static string Id(int index, short kind) => new Guid(index + 1, kind, 0, new byte[8]).ToString("D");

    private static string Number(int index) => (100_000_000 + index).ToString(CultureInfo.InvariantCulture);

    internal static byte[] Create(Shape shape)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Version", 2);
            writer.WriteNumber("Revision", 1);
            writer.WriteStartArray("Tags");
            for (int index = 0; index < shape.Tags; index++)
            {
                writer.WriteStartObject();
                writer.WriteString("Id", TagId(index));
                writer.WriteString("Name", "collection tag " + index.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("Threads");
            for (int index = 0; index < shape.Threads; index++)
            {
                writer.WriteStartObject();
                writer.WriteString("Tag", TagId(index % shape.Tags));
                Thread(writer, index);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("Media");
            for (int index = 0; index < shape.Media; index++)
            {
                writer.WriteStartObject();
                writer.WriteString("Tag", TagId(index % shape.Tags));
                Thread(writer, index / 100);
                writer.WriteString("Post", Number(index));
                writer.WriteNumber("Position", 0);
                writer.WriteString("RevisionKey", RevisionKey);
                writer.WriteString("MediaId", "1731" + Number(index) + "000");
                writer.WriteString("Variant", "original");
                writer.WriteString("Role", "original");
                writer.WriteString("Sha256", Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("LocalOccurrences");
            for (int index = 0; index < shape.Occurrences; index++)
            {
                writer.WriteStartObject();
                writer.WriteString("Id", Id(index, 2));
                Thread(writer, index / 100);
                writer.WriteString("Directory", "/home/user/MeowChan/archive/fourchan/g/" + Number(index / 100) + "/media");
                writer.WriteString("RequestedFile", "1731" + Number(index) + "000.webm");
                writer.WriteStartArray("Revisions");
                writer.WriteStartObject();
                writer.WriteString("Id", Id(index, 3));
                writer.WriteString("AssetId", Sha256.Replace('c', 'd'));
                writer.WriteNumber("Length", 1_048_576 + index);
                writer.WriteString("Sha256", Sha256);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("LocalMedia");
            for (int index = 0; index < shape.Local; index++)
            {
                int occurrence = index % shape.Occurrences;
                writer.WriteStartObject();
                writer.WriteString("Tag", TagId(index / shape.Occurrences % shape.Tags));
                writer.WriteString("Occurrence", Id(occurrence, 2));
                writer.WriteString("Revision", Id(occurrence, 3));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void Thread(Utf8JsonWriter writer, int index)
    {
        writer.WriteString("Source", "fourchan");
        writer.WriteString("Board", "g");
        writer.WriteString("Thread", Number(index));
    }
}

/// <summary>Real store parse and write paths on synthetic large documents; timings are observations, never assertions.</summary>
[Collection(PrivateTagStoreScaleCollection.Name)]
public sealed class PrivateTagStoreScaleEvidenceTests(ITestOutputHelper output)
{
    private const string FileName = "private-tags.json";

    [Fact]
    [Trait("Category", "TagStoreScaleEvidence")]
    public void ParseAndWriteTimingsAndBytesPerItemAgainstTheByteCap()
    {
        PrivateTagScaleDocument.Shape empty = new(1, 0, 0, 0, 0);
        int baseline = PrivateTagScaleDocument.Create(empty).Length;
        const int sample = 1000;
        double PerItem(PrivateTagScaleDocument.Shape shape) =>
            (PrivateTagScaleDocument.Create(shape).Length - baseline) / (double)sample;
        double tag = (PrivateTagScaleDocument.Create(empty with { Tags = sample + 1 }).Length - baseline) / (double)sample;
        double thread = PerItem(empty with { Threads = sample });
        double media = PerItem(empty with { Media = sample });
        double occurrence = PerItem(empty with { Occurrences = sample });
        double local = (PrivateTagScaleDocument.Create(empty with { Occurrences = sample, Local = sample }).Length - baseline) / (double)sample - occurrence;
        output.WriteLine(FormattableString.Invariant(
            $"bytes/item: tag {tag:F0}, thread {thread:F0}, media {media:F0}, local occurrence (1 revision) {occurrence:F0}, local assignment {local:F0}"));
        output.WriteLine(FormattableString.Invariant(
            $"fit alone in {PrivateTagStore.MaximumBytes / 1048576} MiB: thread {PrivateTagStore.MaximumBytes / thread:F0}, media {PrivateTagStore.MaximumBytes / media:F0}, occurrence {PrivateTagStore.MaximumBytes / occurrence:F0}, local {PrivateTagStore.MaximumBytes / local:F0}"));
        double full = PrivateTagStore.MaximumTags * tag + PrivateTagStore.MaximumThreadAssignments * thread
            + PrivateTagStore.MaximumMediaAssignments * media + PrivateTagStore.MaximumLocalOccurrences * occurrence
            + PrivateTagStore.MaximumLocalAssignments * local + baseline;
        output.WriteLine(FormattableString.Invariant($"all count limits at maximum: {full / 1048576:F1} MiB"));

        Measure("moderate", new(1000, 5_000, 10_000, 5_000, 5_000));
        Measure("dense", new(1000, 30_000, 30_000, 10_000, 20_000));
        // Raised settings: almost the tag ceiling and every assignment count limit at or near its maximum.
        Measure("ceiling", new(PrivateTagLimits.MaximumTagLimit - 10, 50_000, 50_000, 20_000, 20_000));
        MeasureCatalog();
    }

    // The catalog alone at its definition limit: building it and one membership check per assignment.
    private void MeasureCatalog()
    {
        PrivateTagDefinition[] items = Enumerable.Range(0, PrivateTagCatalog.MaximumDefinitions)
            .Select(index => new PrivateTagDefinition(new(Guid.Parse(PrivateTagScaleDocument.TagId(index))),
                PrivateTagCatalogComplexityTests.Name("collection tag " + index.ToString(CultureInfo.InvariantCulture))))
            .ToArray();
        // One define per write in the product; a loop of single defines copies the vocabulary each time.
        PrivateTagDefinition[] defaults = items[..PrivateTagLimits.DefaultTagLimit];
        double sequential = Median(() =>
        {
            PrivateTagCatalog catalog = new();
            foreach (PrivateTagDefinition item in defaults)
                Assert.True(catalog.TryDefine(item.Id, item.Name, catalog.Revision, out catalog, out _));
        });
        Assert.True(PrivateTagCatalog.TryCreate(items[..^1], out PrivateTagCatalog? almost, out _));
        PrivateTagDefinition final = items[^1];
        double single = Median(() => Assert.True(almost.TryDefine(final.Id, final.Name, almost.Revision, out _, out _)));
        double bulk = Median(() => Assert.True(PrivateTagCatalog.TryCreate(items, out _, out _)));
        Assert.True(PrivateTagCatalog.TryCreate(items, out PrivateTagCatalog? built, out _));
        const int lookups = 150_000;
        double linear = Median(() =>
        {
            for (int index = 0; index < lookups; index++)
            {
                TagId id = items[index % items.Length].Id;
                Assert.True(built.Definitions.Any(item => item.Id == id));
            }
        });
        double indexed = Median(() =>
        {
            for (int index = 0; index < lookups; index++) Assert.True(built.Contains(items[index % items.Length].Id));
        });
        output.WriteLine(FormattableString.Invariant(
            $"catalog: {defaults.Length} sequential TryDefine {sequential:F1} ms; at {items.Length} tags: TryCreate {bulk:F1} ms, one TryDefine {single:F2} ms; {lookups} lookups linear (former Contains) {linear:F0} ms, indexed {indexed:F1} ms"));
    }

    private void Measure(string label, PrivateTagScaleDocument.Shape shape)
    {
        byte[] document = PrivateTagScaleDocument.Create(shape);
        Assert.True(document.Length <= PrivateTagLimits.MaximumDocumentLimitBytes);
        Assert.Equal(2, PrivateTagStore.ValidateDocumentBytes(document));
        double parse = Median(() => PrivateTagStore.ValidateDocumentBytes(document));

        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, FileName);
        File.WriteAllBytes(path, document);
        File.WriteAllBytes(path + ".bak", document);
        PrivateTagStore store = new(temp.Path, lease, new PrivateTagLimits
        {
            TagLimit = PrivateTagLimits.MaximumTagLimit, DocumentLimitBytes = PrivateTagLimits.MaximumDocumentLimitBytes
        });
        double read = Median(() => store.ReadSnapshot());
        List<double> writes = [];
        for (int index = 0; index < 4; index++)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            Assert.True(store.Define("bench " + index.ToString(CultureInfo.InvariantCulture)).Changed);
            writes.Add(elapsed.Elapsed.TotalMilliseconds);
        }
        output.WriteLine(FormattableString.Invariant(
            $"{label} {shape}: {document.Length / 1048576.0:F1} MiB; parse {parse:F0} ms, read snapshot {read:F0} ms, writes {string.Join(" / ", writes.Select(value => value.ToString("F0", CultureInfo.InvariantCulture)))} ms"));
    }

    private static double Median(Action action)
    {
        action();
        double[] samples = new double[5];
        for (int index = 0; index < samples.Length; index++)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            action();
            samples[index] = elapsed.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return samples[samples.Length / 2];
    }
}

public sealed class PrivateTagCatalogComplexityTests
{
    internal static PrivateTagName Name(string text)
    {
        Assert.True(PrivateTagName.TryCreate(text, out PrivateTagName? name, out _));
        return name!;
    }

    private static (bool Ok, PrivateTagCatalog Catalog, PrivateTagCatalogError Error) Sequential(IEnumerable<PrivateTagDefinition> items)
    {
        PrivateTagCatalog catalog = new();
        foreach (PrivateTagDefinition item in items)
        {
            if (!catalog.TryDefine(item.Id, item.Name, catalog.Revision, out PrivateTagCatalog next, out PrivateTagCatalogError error))
                return (false, catalog, error);
            catalog = next;
        }
        return (true, catalog, PrivateTagCatalogError.None);
    }

    [Fact]
    public void BulkCreateEqualsDefiningEachItemInOrder()
    {
        Random random = new(20261113);
        TagId[] ids = [.. Enumerable.Range(1, 12).Select(index => new TagId(new Guid(index, 7, 0, new byte[8]))), new TagId(Guid.Empty)];
        string[] names = ["cat", "CAT", "Cat", "dog", "café", "CAFE\u0301", "bird", "fish", "owl", "fox", "elk", "yak", "ant"];
        for (int round = 0; round < 2000; round++)
        {
            PrivateTagDefinition[] items = Enumerable.Range(0, random.Next(0, 16)).Select(_ => new PrivateTagDefinition(
                ids[random.Next(ids.Length)], random.Next(40) == 0 ? null! : Name(names[random.Next(names.Length)]))).ToArray();
            (bool ok, PrivateTagCatalog expected, PrivateTagCatalogError error) = Sequential(items);

            Assert.Equal(ok, PrivateTagCatalog.TryCreate(items, out PrivateTagCatalog? actual, out PrivateTagCatalogError actualError));
            Assert.Equal(error, actualError);
            if (!ok) { Assert.Null(actual); continue; }
            Assert.Equal(expected.Revision, actual!.Revision);
            Assert.Equal(expected.Definitions.Select(item => (item.Id, item.Name.Display)), actual.Definitions.Select(item => (item.Id, item.Name.Display)));
            Assert.True(actual.DescendsFrom(actual));
            foreach (PrivateTagDefinition item in expected.Definitions)
            {
                Assert.True(actual.Contains(item.Id));
                Assert.Equal(item.Id, actual.Find(Name(item.Name.Display.ToUpperInvariant()))?.Id);
            }
            Assert.False(actual.Contains(new(Guid.NewGuid())));
            Assert.Null(actual.Find(Name("never defined")));
            Assert.Null(actual.Find(null));
        }
    }

    [Fact]
    public void BulkCreateKeepsTheDefinitionLimitAndLaterChangesUseTheIndex()
    {
        PrivateTagDefinition[] items = Enumerable.Range(0, PrivateTagCatalog.MaximumDefinitions + 1)
            .Select(index => new PrivateTagDefinition(new(new Guid(index + 1, 0, 0, new byte[8])), Name("tag " + index.ToString(CultureInfo.InvariantCulture))))
            .ToArray();
        Assert.False(PrivateTagCatalog.TryCreate(items, out _, out PrivateTagCatalogError error));
        Assert.Equal(PrivateTagCatalogError.CapacityExceeded, error);

        Assert.True(PrivateTagCatalog.TryCreate(items[..^1], out PrivateTagCatalog? full, out _));
        Assert.Equal(PrivateTagCatalog.MaximumDefinitions, full!.Revision);
        Assert.False(full.TryDefine(items[^1].Id, items[^1].Name, full.Revision, out _, out error));
        Assert.Equal(PrivateTagCatalogError.CapacityExceeded, error);
        Assert.True(full.TryDefine(items[5].Id, Name("tag 5"), full.Revision, out PrivateTagCatalog same, out _));
        Assert.Same(full, same);
        Assert.False(full.TryRename(items[5].Id, Name("TAG 6"), full.Revision, out _, out error));
        Assert.Equal(PrivateTagCatalogError.NameCollision, error);
        Assert.True(full.TryRename(items[5].Id, Name("TAG 5"), full.Revision, out PrivateTagCatalog renamed, out _));
        Assert.True(renamed.DescendsFrom(full));
        Assert.Equal("TAG 5", renamed.Find(Name("tag 5"))!.Name.Display);
        Assert.Equal("tag 5", full.Find(Name("tag 5"))!.Name.Display);
        Assert.True(renamed.TryRename(items[6].Id, Name("tag 5 old"), renamed.Revision, out PrivateTagCatalog second, out _));
        Assert.Null(second.Find(Name("tag 6")));
        Assert.Equal(items[6].Id, second.Find(Name("TAG 5 OLD"))!.Id);
    }
}

[Collection(PrivateTagStoreScaleCollection.Name)]
public sealed class PrivateTagStoreComplexityTests
{
    private const string FileName = "private-tags.json";
    private static ResourceId Thread(int number) => new("source", "board", number.ToString(CultureInfo.InvariantCulture));

    [Fact]
    public void EachWriteParsesThePrimaryOnceAndAnAlreadyValidatedBackupNotAgain()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        List<string> parsed = [];
        PrivateTagStore store = new(temp.Path, lease) { DocumentParsedForTests = parsed.Add };
        TagId cat = store.Define("cat").Tag;
        Assert.Empty(parsed); // nothing on disk yet
        TagId dog = store.Define("dog").Tag;
        Assert.Equal([FileName], parsed); // no backup exists before the second write
        for (int index = 0; index < 4; index++)
        {
            parsed.Clear();
            Assert.True(store.SetThread(index % 2 == 0 ? cat : dog, Thread(index), true).Changed);
            Assert.Equal([FileName], parsed);
        }
        parsed.Clear();
        Assert.False(store.Define("CAT").Changed);
        store.ReadSnapshot();
        Assert.Equal([FileName, FileName], parsed);

        // A new instance (restart) does not know the backup yet: it parses it in full once.
        List<string> restarted = [];
        PrivateTagStore fresh = new(temp.Path, lease) { DocumentParsedForTests = restarted.Add };
        fresh.Delete(dog);
        Assert.Equal([FileName, FileName + ".bak"], restarted);
        restarted.Clear();
        fresh.Rename(cat, "Cat");
        Assert.Equal([FileName], restarted);
        Assert.Equal(["Cat"], fresh.ReadSnapshot().Tags.Select(tag => tag.Name.Display));
    }

    [Fact]
    public void AWarmBackupCheckStillRefusesToRotateANewerOrDamagedBackup()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        List<string> parsed = [];
        PrivateTagStore store = new(temp.Path, lease) { DocumentParsedForTests = parsed.Add };
        TagId cat = store.Define("cat").Tag;
        store.SetThread(cat, Thread(1), true);
        store.SetThread(cat, Thread(2), true);
        string path = Path.Combine(temp.Path, FileName);
        byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        byte[] future = "{\"Version\":3,\"Revision\":9,\"Tags\":[],\"Threads\":[],\"Media\":[]}"u8.ToArray();
        byte[] damaged = (byte[])backup.Clone();
        damaged[^1] = (byte)'x';
        // Still a valid document of the same length, but not the bytes this store validated.
        string backupText = Encoding.UTF8.GetString(backup);
        Assert.Single(backupText.Split("\"Thread\":\"1\"")[1..]);
        byte[] sameLength = Encoding.UTF8.GetBytes(backupText.Replace("\"Thread\":\"1\"", "\"Thread\":\"7\"", StringComparison.Ordinal));
        Assert.Equal(backup.Length, sameLength.Length);

        foreach (byte[] replaced in new[] { future, damaged })
        {
            File.WriteAllBytes(path + ".bak", replaced);
            parsed.Clear();
            Assert.Throws<InvalidDataException>(() => store.SetThread(cat, Thread(3), true));
            Assert.Equal([FileName, FileName + ".bak"], parsed);
            Assert.Equal(primary, File.ReadAllBytes(path));
            Assert.Equal(replaced, File.ReadAllBytes(path + ".bak"));
        }

        File.WriteAllBytes(path + ".bak", sameLength);
        parsed.Clear();
        Assert.True(store.SetThread(cat, Thread(3), true).Changed);
        Assert.Equal([FileName, FileName + ".bak"], parsed);
        Assert.Equal(primary, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void AFullVocabularyWithManyAssignmentsParsesAndWritesInLinearTime()
    {
        PrivateTagScaleDocument.Shape shape = new(PrivateTagStore.MaximumTags, 30_000, 30_000, 10_000, 20_000);
        byte[] document = PrivateTagScaleDocument.Create(shape);
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, FileName);
        File.WriteAllBytes(path, document);
        File.WriteAllBytes(path + ".bak", document);
        List<string> parsed = [];
        PrivateTagStore store = new(temp.Path, lease) { DocumentParsedForTests = parsed.Add };

        // Generous bound for a loaded machine; the former quadratic catalog and linear membership checks are gone.
        Stopwatch elapsed = Stopwatch.StartNew();
        PrivateTagStoreSnapshot snapshot = store.ReadSnapshot();
        TagId removed = snapshot.Tags[1].Id;
        PrivateTagCommit first = store.Delete(removed);
        PrivateTagCommit second = store.Rename(snapshot.Tags[0].Id, "Collection Tag 0");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(60), elapsed.Elapsed.ToString());

        Assert.Equal([FileName, FileName, FileName + ".bak", FileName], parsed);
        Assert.Equal(shape.Tags, snapshot.Tags.Count);
        Assert.Equal(shape.Threads, snapshot.ThreadAssignments.Count);
        Assert.Equal(shape.Media, snapshot.MediaAssignments.Count);
        Assert.Equal(shape.Local, snapshot.LocalAssignments.Count);
        Assert.Equal(snapshot.Tags.Where(tag => tag.Id != removed).Select(tag => tag.Id), first.Snapshot.Tags.Select(tag => tag.Id));
        Assert.DoesNotContain(first.Snapshot.ThreadAssignments, item => (TagId)item.TagId == removed);
        Assert.Equal("Collection Tag 0", second.Snapshot.Tags[0].Name.Display);
        Assert.Equal(PrivateTagStoreError.NameCollision,
            Assert.Throws<PrivateTagStoreException>(() => store.Rename(snapshot.Tags[0].Id, "COLLECTION TAG 2")).Error);
        Assert.Equal(second.Snapshot.Fingerprint, new PrivateTagStore(temp.Path, lease).ReadSnapshot().Fingerprint);
    }
}
