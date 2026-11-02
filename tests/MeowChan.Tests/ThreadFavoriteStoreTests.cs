using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ThreadFavoriteStoreTests
{
    private static ResourceId Thread => new("source", "board", "42");

    [Fact]
    public void SchemaOneRevisionAndBomRemainReadableWithoutChangingEvidence()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        string backup = path + ".bak";
        byte[] primary = Encoding.UTF8.GetBytes("\uFEFF" + JsonSerializer.Serialize(
            new { Version = 1, Revision = 17L, Threads = new[] { Thread } }));
        byte[] backupBytes = Encoding.UTF8.GetBytes("backup evidence");
        File.WriteAllBytes(path, primary);
        File.WriteAllBytes(backup, backupBytes);

        ThreadFavoriteSnapshot snapshot = new ThreadFavoriteStore(temp.Path, lease).ReadSnapshot();

        Assert.Equal(Thread, Assert.Single(snapshot.Threads));
        Assert.Equal(17, snapshot.Revision);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(primary)), snapshot.Fingerprint);
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(backupBytes, File.ReadAllBytes(backup));
    }

    [Theory]
    [InlineData("{\"Version\":1,\"Revision\":0,\"Threads\":[],\"Unknown\":true}")]
    [InlineData("{\"Version\":1,\"Version\":1,\"Revision\":0,\"Threads\":[]}")]
    [InlineData("{\"Version\":1,\"Threads\":[]}")]
    [InlineData("{\"Version\":1,\"Revision\":0,\"Threads\":null}")]
    [InlineData("{\"Version\":1,\"Revision\":0,\"Threads\":[{\"Source\":\"source\",\"Board\":\"board\"}]}")]
    [InlineData("{\"Version\":1,\"Revision\":0,\"Threads\":[{\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\",\"Unknown\":1}]}")]
    [InlineData("{\"Version\":1,\"Revision\":0,\"Threads\":[{\"Source\":\"source\",\"Source\":\"other\",\"Board\":\"board\",\"Thread\":\"42\"}]}")]
    [InlineData("{\"Version\":1,\"Revision\":0,\"Threads\":[{\"Source\":null,\"Board\":\"board\",\"Thread\":\"42\"}]}")]
    [InlineData("{\"Version\":1,\"Revision\":0,\"Threads\":[{\"Source\":\"\",\"Board\":\"board\",\"Thread\":\"42\"}]}")]
    [InlineData("{\"Version\":1,\"Revision\":0,\"Threads\":[{\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\"},{\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\"}]}")]
    public void UnknownDuplicateMissingNullAndInvalidFieldsAreRejectedWithoutWriting(string raw)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        string backup = path + ".bak";
        byte[] primary = Encoding.UTF8.GetBytes(raw);
        byte[] backupBytes = Encoding.UTF8.GetBytes("preserved backup");
        File.WriteAllBytes(path, primary);
        File.WriteAllBytes(backup, backupBytes);
        ThreadFavoriteStore store = new(temp.Path, lease);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());

        Assert.DoesNotContain("source", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("board", error.Message, StringComparison.Ordinal);
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(backupBytes, File.ReadAllBytes(backup));
        Assert.Throws<InvalidDataException>(() => store.Set(Thread, true));
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(backupBytes, File.ReadAllBytes(backup));
    }

    [Fact]
    public void ExactByteBoundaryIsReadableAndOneByteOverIsRejected()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        byte[] emptySource = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Revision = 0L,
            Threads = new[] { new ResourceId("", "board", "42") }
        });
        int sourceLength = ThreadFavoriteStore.MaximumBytes - emptySource.Length;
        Assert.True(sourceLength > 0);
        byte[] exact = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Revision = 0L,
            Threads = new[] { new ResourceId(new string('a', sourceLength), "board", "42") }
        });
        Assert.Equal(ThreadFavoriteStore.MaximumBytes, exact.Length);
        File.WriteAllBytes(path, exact);

        ThreadFavoriteSnapshot snapshot = new ThreadFavoriteStore(temp.Path, lease).ReadSnapshot();
        Assert.Equal(0, snapshot.Revision);
        Assert.Equal(sourceLength, Assert.Single(snapshot.Threads).Source.Length);

        File.WriteAllBytes(path, [.. exact, (byte)' ']);
        Assert.Throws<InvalidDataException>(() => new ThreadFavoriteStore(temp.Path, lease).ReadSnapshot());
    }

    [Fact]
    public void ReadSnapshotIsImmutableAndMissingFilesStayMissing()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        string backup = path + ".bak";
        ThreadFavoriteStore store = new(temp.Path, lease);

        ThreadFavoriteSnapshot empty = store.ReadSnapshot();

        Assert.Empty(empty.Threads);
        Assert.Equal(0, empty.Revision);
        Assert.Throws<NotSupportedException>(() => ((IList<ResourceId>)empty.Threads).Add(Thread));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(backup));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path, "thread-favorites*"));

        File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, Revision = 1L, Threads = new[] { Thread } }));
        File.WriteAllText(backup, "backup evidence");
        byte[] primary = File.ReadAllBytes(path);
        byte[] backupBytes = File.ReadAllBytes(backup);
        ThreadFavoriteSnapshot saved = store.ReadSnapshot();
        Assert.Throws<NotSupportedException>(() => ((IList<ResourceId>)saved.Threads).Clear());
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(backupBytes, File.ReadAllBytes(backup));
    }

    [Fact]
    public void DirectoryAtPrimaryPathFailsClosedInsteadOfBecomingEmpty()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        Directory.CreateDirectory(path);

        Assert.Throws<InvalidDataException>(() => new ThreadFavoriteStore(temp.Path, lease).ReadSnapshot());
    }

    public static IEnumerable<object[]> InvalidDocuments()
    {
        foreach (string raw in new[]
        {
            "null", "[]", "{}", "{", "true",
            "{\"Version\":1,\"Revision\":0,\"Threads\":[]} {}",
            "{\"Version\":1,\"Revision\":0,\"Threads\":[],}",
            "{\"Version\":1,/*comment*/\"Revision\":0,\"Threads\":[]}"
        }) yield return new object[] { raw };

        const string valid = "{\"Version\":1,\"Revision\":0,\"Threads\":[]}";
        foreach (string name in new[] { "Version", "Revision", "Threads" })
        {
            string value = name == "Version" ? "1" : name == "Revision" ? "0" : "[]";
            string field = "\"" + name + "\":" + value;
            yield return new object[] { valid.Replace(field, field + "," + field) };
            yield return new object[] { valid.Replace("\"" + name + "\"", "\"" + name.ToLowerInvariant() + "\"") };
            yield return new object[] { valid.Replace(field, "\"" + name + "\":null") };
            yield return new object[] { valid.Replace(field + ",", "").Replace("," + field, "") };
        }
        foreach (string version in new[] { "0", "2", "1.0", "\"1\"", "true", "2147483648" })
            yield return new object[] { valid.Replace("\"Version\":1", "\"Version\":" + version) };
        foreach (string revision in new[] { "-1", "0.5", "\"0\"", "false", "9223372036854775808" })
            yield return new object[] { valid.Replace("\"Revision\":0", "\"Revision\":" + revision) };
        foreach (string threads in new[] { "{}", "[null]", "[[]]", "[true]", "[42]", "[{}]" })
            yield return new object[] { valid.Replace("[]", threads) };

        const string identity = "{\"Source\":\"source\",\"Board\":\"board\",\"Thread\":\"42\"}";
        foreach (string name in new[] { "Source", "Board", "Thread" })
        {
            string value = name == "Source" ? "source" : name == "Board" ? "board" : "42";
            string field = "\"" + name + "\":\"" + value + "\"";
            foreach (string replacement in new[] { "null", "true", "42", "{}", "[]", "\"\"", "\" \\t\"", "\"\\uD800\"", "\"\\uDC00\"" })
                yield return new object[] { valid.Replace("[]", "[" + identity.Replace(field, "\"" + name + "\":" + replacement) + "]") };
            yield return new object[] { valid.Replace("[]", "[" + identity.Replace(field, field + "," + field) + "]") };
            yield return new object[] { valid.Replace("[]", "[" + identity.Replace(field + ",", "").Replace("," + field, "") + "]") };
        }
        // Escaped names are decoded before duplicate-field checks.
        yield return new object[] { valid.Replace("\"Revision\":0", "\"Revision\":0,\"\\u0052evision\":1") };
        yield return new object[] { valid.Replace("[]", "[" + identity.Replace("\"Source\":\"source\"", "\"Source\":\"source\",\"\\u0053ource\":\"source\"") + "]") };
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void StrictSchemaRejectsInvalidTypesNamesUnicodeAndJson(string raw)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        File.WriteAllText(path, raw, new UTF8Encoding(false, true));
        byte[] before = File.ReadAllBytes(path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());
        Assert.Throws<InvalidDataException>(() => store.Set(Thread, false));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void InvalidUtf8AndPrivateParserValuesNeverEnterDiagnostics()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        ThreadFavoriteStore store = new(temp.Path, lease);
        foreach (byte[] bytes in new[]
        {
            new byte[] { 0xFF }, new byte[] { 0xC0, 0xAF }, new byte[] { 0xED, 0xA0, 0x80 },
            Encoding.UTF8.GetBytes("{\"Version\":\"private-version-marker\",\"Revision\":0,\"Threads\":[]}"),
            Encoding.UTF8.GetBytes("{\"Version\":1,\"Revision\":0,\"Threads\":[{\"Source\":\"private-source-marker\",\"Board\":\"b\",\"Thread\":private-token-marker}]}")
        })
        {
            File.WriteAllBytes(path, bytes);
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("private-", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(temp.Path, error.ToString(), StringComparison.Ordinal);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bak"));
        }
    }

    [Fact]
    public void SchemaOneWritesIncrementOnlyRealChangesAndPreserveExactIdentities()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        ResourceId[] ids = [Thread, Thread with { Source = "Source" }, new(" source ", "猫", "😀")];
        byte[] before = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Revision = 17L, Threads = ids });
        File.WriteAllBytes(path, before);
        ThreadFavoriteStore store = new(temp.Path, lease);
        ThreadFavoriteSnapshot snapshot = store.ReadSnapshot();
        Assert.Equal(ids, snapshot.Threads);
        Assert.False(store.Set(Thread, true).Changed);
        Assert.False(store.Set(Thread with { Thread = "missing" }, false).Changed);
        Assert.Throws<OperationCanceledException>(() => store.Set(Thread, false, new CancellationToken(true)));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));

        ThreadFavoriteCommit removed = store.Set(Thread, false);
        Assert.True(removed.Changed);
        Assert.Equal(18, removed.Snapshot.Revision);
        Assert.Equal(ids[1..], removed.Snapshot.Threads);
        Assert.Equal(ids, snapshot.Threads);
        Assert.Equal(before, File.ReadAllBytes(path + ".bak"));
        using JsonDocument written = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(1, written.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(18, written.RootElement.GetProperty("Revision").GetInt64());
        Assert.Equal(new[] { "Version", "Revision", "Threads" }, written.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void MaximumRevisionRemainsReadableAndNoopDoesNotOverflowOrWrite()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        byte[] before = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Revision = long.MaxValue, Threads = new[] { Thread } });
        File.WriteAllBytes(path, before);
        ThreadFavoriteStore store = new(temp.Path, lease);
        Assert.Equal(long.MaxValue, store.ReadSnapshot().Revision);
        Assert.False(store.Set(Thread, true).Changed);
        Assert.Throws<OverflowException>(() => store.Set(Thread, false));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void EntryLimitIsInclusiveAndOversizedInputFailsAsOneDocument()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        ResourceId[] ids = Enumerable.Range(0, ThreadFavoriteStore.MaximumEntries + 1)
            .Select(index => new ResourceId("s", "b", index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        byte[] exact = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Revision = 0L, Threads = ids[..^1] });
        Assert.True(exact.Length < ThreadFavoriteStore.MaximumBytes);
        File.WriteAllBytes(path, exact);
        ThreadFavoriteStore store = new(temp.Path, lease);
        Assert.Equal(ThreadFavoriteStore.MaximumEntries, store.ReadSnapshot().Threads.Count);
        Assert.Throws<InvalidDataException>(() => store.Set(ids[^1], true));
        Assert.Equal(exact, File.ReadAllBytes(path));
        byte[] over = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Revision = 0L, Threads = ids });
        File.WriteAllBytes(path, over);
        Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());
        Assert.Equal(over, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingPrimaryWithValidOrInvalidBackupRequiresExplicitRecovery(bool malformed)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        byte[] backup = Encoding.UTF8.GetBytes(malformed ? "{" : "{\"Version\":1,\"Revision\":0,\"Threads\":[]}");
        File.WriteAllBytes(path + ".bak", backup);
        ThreadFavoriteStore store = new(temp.Path, lease);
        Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());
        Assert.Throws<InvalidDataException>(() => store.Set(Thread, false));
        Assert.False(File.Exists(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void MalformedBackupDoesNotBlockReadOrNoopButCannotBeRotatedAway()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        byte[] primary = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Revision = 1L, Threads = new[] { Thread } });
        byte[] backup = Encoding.UTF8.GetBytes("{\"Version\":1,\"Revision\":0,\"Threads\":[],\"extra\":true}");
        File.WriteAllBytes(path, primary);
        File.WriteAllBytes(path + ".bak", backup);
        ThreadFavoriteStore store = new(temp.Path, lease);
        Assert.Equal(Thread, Assert.Single(store.ReadSnapshot().Threads));
        Assert.False(store.Set(Thread, true).Changed);
        Assert.Throws<InvalidDataException>(() => store.Set(Thread, false));
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryAtEitherCandidateNeverEstablishesAbsence(bool backup)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        string directory = path + (backup ? ".bak" : "");
        Directory.CreateDirectory(directory);
        ThreadFavoriteStore store = new(temp.Path, lease);
        Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());
        Assert.Throws<InvalidDataException>(() => store.Set(Thread, true));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LinkedAndDanglingCandidatesFailClosed(bool backup, bool dangling)
    {
        // Windows link creation requires developer mode or elevated privileges.
        if (OperatingSystem.IsWindows()) return;
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        string target = Path.Combine(temp.Path, "synthetic-target.json");
        byte[] bytes = Encoding.UTF8.GetBytes("{\"Version\":1,\"Revision\":0,\"Threads\":[]}");
        if (!dangling) File.WriteAllBytes(target, bytes);
        string link = path + (backup ? ".bak" : "");
        File.CreateSymbolicLink(link, target);
        ThreadFavoriteStore store = new(temp.Path, lease);
        Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());
        Assert.Throws<InvalidDataException>(() => store.Set(Thread, true));
        Assert.Equal(target, new FileInfo(link).LinkTarget);
        if (!dangling) Assert.Equal(bytes, File.ReadAllBytes(target));
        else Assert.False(File.Exists(target));
    }

}
