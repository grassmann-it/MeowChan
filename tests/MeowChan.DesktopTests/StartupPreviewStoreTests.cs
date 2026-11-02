using System.Collections;
using System.Reflection;
using System.Text;
using MeowChan.Desktop;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class StartupPreviewStoreTests
{
    private static readonly DateTimeOffset Captured = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private const string RowJson = """
        {"isBoard":false,"source":"synthetic","board":"test","title":"Example","knownMedia":3,"completedMedia":2}
        """;
    private static string Document(string row = RowJson) =>
        "{\"schemaVersion\":1,\"capturedAtUtc\":\"2026-11-01T12:00:00+00:00\",\"threadCount\":1,\"boardCount\":0,\"rows\":[" + row + "]}";
    private static StartupPreviewRow Row(bool board = false) => new(board, "synthetic", "test", "Example", 3, 2);
    private static StartupPreviewSnapshot Snapshot(params StartupPreviewRow[] rows) =>
        new(Captured, rows.Count(row => !row.IsBoard), rows.Count(row => row.IsBoard), rows);

    [Fact]
    public void MissingRoundTripRestartAndProfileIsolationUseActualLeases()
    {
        using Workspace first = new();
        using Workspace second = new();
        Assert.Null(first.Store.Load());
        Assert.Null(second.Store.Load());
        var snapshot = new StartupPreviewSnapshot(Captured, 8, 4, new[] { Row(), Row(true) with { Title = "" } });
        first.Store.Save(snapshot);
        StartupPreviewSnapshot loaded = first.Store.Load()!;
        Assert.Equal(Captured, loaded.CapturedAtUtc);
        Assert.Equal(8, loaded.ThreadCount);
        Assert.Equal(4, loaded.BoardCount);
        Assert.Equal(snapshot.Rows.ToArray(), loaded.Rows.ToArray());
        Assert.Null(second.Store.Load());
        first.Lease.Dispose();
        using ProfileLease restarted = ProfileLease.Acquire(first.Path);
        Assert.Equal(snapshot.Rows.ToArray(), new StartupPreviewStore(first.Path, restarted).Load()!.Rows.ToArray());
    }

    [Fact]
    public void RejectsMalformedMissingNullUnknownAndDuplicateFieldsAtBothLevels()
    {
        using Workspace workspace = new();
        string valid = Document();
        string[] invalid =
        [
            "", "null", "[]", "{}", valid + " {}", valid[..^1],
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":0"),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1.5"),
            valid.Replace("\"schemaVersion\":1,", ""),
            valid.Replace("\"rows\":[" + RowJson + "]", "\"rows\":null"),
            valid.Replace("[" + RowJson + "]", "[null]"),
            valid.Replace("\"source\":\"synthetic\"", "\"source\":null"),
            valid.Replace("\"source\":\"synthetic\",", ""),
            valid.Replace("\"isBoard\":false", "\"isBoard\":0"),
            valid.Replace("\"title\":\"Example\"", "\"title\":{}"),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            valid.Replace("\"title\":\"Example\"", "\"title\":\"Example\",\"title\":\"Example\""),
            valid.Replace("\"title\":\"Example\"", "\"title\":\"Example\",\"t\\u0069tle\":\"Example\""),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"SchemaVersion\":1"),
            valid.Replace("\"title\":\"Example\"", "\"title\":\"Example\",\"watchId\":\"forged\""),
            valid.Replace("\"rows\":[", "\"attachments\":[],\"rows\":["),
            valid.Replace("\"title\":\"Example\"", "\"title\":\"Example\",\"extra\":[[[[[[[[[[]]]]]]]]]]"),
            valid.Replace("\"threadCount\":1", "\"threadCount\":2147483648"),
            valid.Replace("\"knownMedia\":3", "\"knownMedia\":\"3\""),
            valid.Replace("\"source\":\"synthetic\"", "\"source\":\"\""),
            valid.Replace("\"board\":\"test\"", "\"board\":\"\""),
            valid.Replace("\"title\":\"Example\"", "\"title\":\"bad\\u0000text\""),
            valid.Replace("\"title\":\"Example\"", "\"title\":\"" + new string('x', StartupPreviewStore.MaximumTextLength + 1) + "\"")
        ];
        foreach (string json in invalid)
        {
            File.WriteAllText(workspace.CachePath, json);
            Assert.Throws<InvalidDataException>(() => workspace.Store.Load());
            Assert.Equal(json, File.ReadAllText(workspace.CachePath));
        }
        // Check every required root/row field rejects explicit null, not just representative ones.
        foreach (string name in new[] { "schemaVersion", "capturedAtUtc", "threadCount", "boardCount", "rows" })
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(valid)!.AsObject();
            node[name] = null;
            File.WriteAllText(workspace.CachePath, node.ToJsonString());
            Assert.Throws<InvalidDataException>(() => workspace.Store.Load());
        }
        foreach (string name in new[] { "isBoard", "source", "board", "title", "knownMedia", "completedMedia" })
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(valid)!.AsObject();
            node["rows"]![0]![name] = null;
            File.WriteAllText(workspace.CachePath, node.ToJsonString());
            Assert.Throws<InvalidDataException>(() => workspace.Store.Load());
        }
    }

    [Fact]
    public void InvalidCountersTotalsTimestampsAndTextAreRejectedOnLoadAndSave()
    {
        using Workspace workspace = new();
        StartupPreviewSnapshot good = Snapshot(Row(), Row(true));
        StartupPreviewSnapshot[] bad =
        [
            good with { CapturedAtUtc = default },
            good with { CapturedAtUtc = Captured.ToOffset(TimeSpan.FromHours(1)) },
            good with { ThreadCount = -1 }, good with { BoardCount = -1 },
            good with { ThreadCount = 0 }, good with { BoardCount = 0 },
            good with { Rows = null! }, good with { Rows = new StartupPreviewRow[] { null! } },
            Snapshot(Row() with { KnownMedia = -1 }), Snapshot(Row() with { CompletedMedia = -1 }),
            Snapshot(Row() with { CompletedMedia = 4 }), Snapshot(Row() with { Source = null! }),
            Snapshot(Row() with { Board = null! }), Snapshot(Row() with { Title = null! }),
            Snapshot(Row() with { Source = "" }), Snapshot(Row() with { Board = "" }),
            Snapshot(Row() with { Title = "bad\ntext" }), Snapshot(Row() with { Source = "bad\u007f" }),
            Snapshot(Row() with { Board = "bad\u0085" }),
            Snapshot(Row() with { Title = new string('x', StartupPreviewStore.MaximumTextLength + 1) })
        ];
        workspace.Store.Save(good);
        byte[] original = File.ReadAllBytes(workspace.CachePath);
        foreach (StartupPreviewSnapshot snapshot in bad)
        {
            Assert.Throws<InvalidDataException>(() => workspace.Store.Save(snapshot));
            Assert.Equal(original, File.ReadAllBytes(workspace.CachePath));
        }
        foreach (string json in new[]
        {
            Document().Replace("\"threadCount\":1", "\"threadCount\":0"),
            Document().Replace("\"boardCount\":0", "\"boardCount\":-1"),
            Document().Replace("\"knownMedia\":3", "\"knownMedia\":-1"),
            Document().Replace("\"completedMedia\":2", "\"completedMedia\":-1"),
            Document().Replace("\"completedMedia\":2", "\"completedMedia\":4"),
            Document().Replace("2026-11-01T12:00:00+00:00", "0001-01-01T00:00:00+00:00"),
            Document().Replace("2026-11-01T12:00:00+00:00", "2026-11-01T12:00:00+01:00"),
            Document().Replace("2026-11-01T12:00:00+00:00", "not a timestamp")
        })
        {
            File.WriteAllText(workspace.CachePath, json);
            Assert.Throws<InvalidDataException>(() => workspace.Store.Load());
        }
    }

    [Fact]
    public void ExactRowAndTextLimitsWorkAndEmptySnapshotIsValid()
    {
        using Workspace workspace = new();
        StartupPreviewRow[] rows = Enumerable.Repeat(Row() with { Title = new string('x', 512) }, 2000).ToArray();
        // A short title keeps the maximum-row case within the independent byte cap.
        workspace.Store.Save(Snapshot(rows.Select(row => row with { Title = "" }).ToArray()));
        Assert.Equal(StartupPreviewStore.MaximumRows, workspace.Store.Load()!.Rows.Count);
        workspace.Store.Save(Snapshot(Row() with { Source = new string('s', 512), Board = new string('b', 512), Title = rows[0].Title }));
        Assert.Equal(512, workspace.Store.Load()!.Rows[0].Title.Length);
        Assert.Throws<InvalidDataException>(() => workspace.Store.Save(Snapshot(rows.Append(Row()).ToArray())));
        File.WriteAllText(workspace.CachePath, Document(string.Join(',', Enumerable.Repeat(RowJson, 2001))));
        Assert.Throws<InvalidDataException>(() => workspace.Store.Load());
        workspace.Store.Save(Snapshot());
        Assert.Empty(workspace.Store.Load()!.Rows);
    }

    [Fact]
    public void EncodedByteLimitIsIndependentOfRowAndCharacterLimitsAndPreservesOldFile()
    {
        using Workspace workspace = new();
        workspace.Store.Save(Snapshot(Row()));
        byte[] before = File.ReadAllBytes(workspace.CachePath);
        // Each allowed character is escaped by the default writer into six UTF-8 bytes.
        StartupPreviewRow large = Row() with { Title = new string('\u263a', 512) };
        Assert.Throws<InvalidDataException>(() => workspace.Store.Save(Snapshot(Enumerable.Repeat(large, 2000).ToArray())));
        Assert.Equal(before, File.ReadAllBytes(workspace.CachePath));
        Assert.Empty(Directory.GetFiles(workspace.Path, ".startup-preview-*.tmp"));
        byte[] prefix = Encoding.UTF8.GetBytes(Document());
        byte[] exact = Enumerable.Repeat((byte)' ', StartupPreviewStore.MaximumBytes).ToArray();
        prefix.CopyTo(exact, 0);
        File.WriteAllBytes(workspace.CachePath, exact);
        Assert.Single(workspace.Store.Load()!.Rows);
        using (FileStream append = new(workspace.CachePath, FileMode.Append)) append.WriteByte((byte)' ');
        Assert.Throws<InvalidDataException>(() => workspace.Store.Load());
    }

    [Fact]
    public void ReaderNeverConsultsLengthAndConsumesAtMostCapPlusOneEvenWhenStreamGrows()
    {
        using GrowingStream stream = new(StartupPreviewStore.MaximumBytes + 100);
        MethodInfo read = typeof(StartupPreviewStore).GetMethod("ReadBounded", BindingFlags.Static | BindingFlags.NonPublic)!;
        TargetInvocationException error = Assert.Throws<TargetInvocationException>(() => read.Invoke(null, new object[] { stream, CancellationToken.None }));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal(StartupPreviewStore.MaximumBytes + 1, stream.Consumed);
        Assert.Equal(StartupPreviewStore.MaximumBytes + 1, stream.LargestRequested);
    }

    [Fact]
    public void SaveCopiesBeforeEncodingAndLoadReturnsIndependentReadOnlyRows()
    {
        using Workspace workspace = new();
        MutatingRows rows = new();
        workspace.Store.Save(new(Captured, 2, 0, rows));
        // Reading the second input row mutates the first caller-owned slot after it was copied.
        Assert.Equal("changed", rows[0].Title);
        StartupPreviewSnapshot first = workspace.Store.Load()!;
        Assert.Equal("Example", first.Rows[0].Title);
        Assert.NotSame(rows[0], first.Rows[0]);
        IList<StartupPreviewRow> list = Assert.IsAssignableFrom<IList<StartupPreviewRow>>(first.Rows);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list[0] = Row() with { Title = "tampered" });
        StartupPreviewSnapshot second = workspace.Store.Load()!;
        Assert.NotSame(first.Rows, second.Rows);
        Assert.Equal("Example", second.Rows[0].Title);
    }

    [Fact]
    public void CancellationLeavesMissingOrExistingCacheAndUnrelatedFilesUntouched()
    {
        using Workspace workspace = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => workspace.Store.Load(cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => workspace.Store.Save(Snapshot(Row()), cancellation.Token));
        Assert.False(File.Exists(workspace.CachePath));
        workspace.Store.Save(Snapshot(Row()));
        byte[] before = File.ReadAllBytes(workspace.CachePath);
        string unrelated = System.IO.Path.Combine(workspace.Path, ".startup-preview-someone-elses.tmp");
        File.WriteAllText(unrelated, "not owned");
        Assert.Throws<OperationCanceledException>(() => workspace.Store.Save(Snapshot(Row(true)), cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(workspace.CachePath));
        Assert.Equal("not owned", File.ReadAllText(unrelated));
        Assert.Single(Directory.GetFiles(workspace.Path, ".startup-preview-*.tmp"));
    }

    [Fact]
    public void InvalidAndFutureCacheCanBeReplacedWithoutTouchingAuthoritativeFiles()
    {
        using Workspace workspace = new();
        string sentinel = System.IO.Path.Combine(workspace.Path, "session.json");
        File.WriteAllText(sentinel, "synthetic authority sentinel");
        foreach (string bad in new[] { "malformed", Document().Replace("\"schemaVersion\":1", "\"schemaVersion\":900") })
        {
            File.WriteAllText(workspace.CachePath, bad);
            Assert.Throws<InvalidDataException>(() => workspace.Store.Load());
            workspace.Store.Save(Snapshot(Row()));
            Assert.Single(workspace.Store.Load()!.Rows);
            Assert.Equal("synthetic authority sentinel", File.ReadAllText(sentinel));
        }
        Assert.Empty(Directory.GetFiles(workspace.Path, ".startup-preview-*.tmp"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(workspace.CachePath));
    }

    [Fact]
    public void LeaseMustMatchAndRemainAlive()
    {
        using Workspace workspace = new();
        using Workspace other = new();
        Assert.Throws<ArgumentException>(() => new StartupPreviewStore(other.Path, workspace.Lease));
        workspace.Lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => workspace.Store.Load());
        Assert.Throws<ObjectDisposedException>(() => workspace.Store.Save(Snapshot(Row())));
        Assert.False(File.Exists(workspace.CachePath));
    }

    [Fact]
    public void FileDirectoryAndDanglingLinksAreRejectedWithoutChangingTargets()
    {
        // Windows link creation requires developer mode or privileges; Unix is the deterministic fixture.
        if (OperatingSystem.IsWindows()) return;
        using Workspace workspace = new();
        string target = System.IO.Path.Combine(workspace.Path, "synthetic-target");
        File.WriteAllText(target, Document());
        File.CreateSymbolicLink(workspace.CachePath, target);
        Assert.Throws<InvalidDataException>(() => workspace.Store.Load());
        Assert.Throws<InvalidDataException>(() => workspace.Store.Save(Snapshot(Row())));
        Assert.Equal(Document(), File.ReadAllText(target));
        File.Delete(workspace.CachePath);
        File.CreateSymbolicLink(workspace.CachePath, target + ".missing");
        Assert.Throws<InvalidDataException>(() => workspace.Store.Load());
        Assert.Throws<InvalidDataException>(() => workspace.Store.Save(Snapshot(Row())));
        Assert.False(File.Exists(target + ".missing"));
        File.Delete(workspace.CachePath);
        Directory.CreateSymbolicLink(workspace.CachePath, workspace.Path);
        Assert.Throws<InvalidDataException>(() => workspace.Store.Load());
        Assert.Throws<InvalidDataException>(() => workspace.Store.Save(Snapshot(Row())));
        Directory.Delete(workspace.CachePath);
        Directory.CreateDirectory(workspace.CachePath);
        Assert.Throws<InvalidDataException>(() => workspace.Store.Load());
        Assert.Throws<InvalidDataException>(() => workspace.Store.Save(Snapshot(Row())));
    }

    [Fact]
    public void LinkedProfileAndHigherAncestorAreRejectedEvenWhenCacheIsMissing()
    {
        if (OperatingSystem.IsWindows()) return;
        using Workspace workspace = new();
        string alias = workspace.Path + "-alias";
        Directory.CreateSymbolicLink(alias, workspace.Path);
        try
        {
            // Match the lease lexically: store rejection is not merely a mismatched-lease check.
            workspace.Lease.Dispose();
            using ProfileLease aliasLease = ProfileLease.Acquire(alias);
            StartupPreviewStore store = new(alias, aliasLease);
            Assert.Throws<InvalidDataException>(() => store.Load());
            Assert.Throws<InvalidDataException>(() => store.Save(Snapshot(Row())));
            string child = System.IO.Path.Combine(alias, "child");
            using ProfileLease childLease = ProfileLease.Acquire(child);
            StartupPreviewStore childStore = new(child, childLease);
            Assert.Throws<InvalidDataException>(() => childStore.Load());
            Assert.Throws<InvalidDataException>(() => childStore.Save(Snapshot(Row())));
            Assert.False(File.Exists(workspace.CachePath));
            Assert.False(File.Exists(System.IO.Path.Combine(child, "startup-preview.json")));
        }
        finally { Directory.Delete(alias); }
    }

    private sealed class Workspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(PhysicalTemporaryRoot(), "meowchan-startup-preview-tests-" + Guid.NewGuid().ToString("N"));
        public string CachePath => System.IO.Path.Combine(Path, "startup-preview.json");
        public ProfileLease Lease { get; }
        public StartupPreviewStore Store { get; }
        public Workspace()
        {
            Lease = ProfileLease.Acquire(Path);
            Store = new(Path, Lease);
        }
        public void Dispose()
        {
            Lease.Dispose();
            Directory.Delete(Path, recursive: true);
        }

        // macOS's normal /var and /tmp aliases must not make all ordinary fixtures
        // linked-profile tests. Resolve the synthetic fixture destination only.
        private static string PhysicalTemporaryRoot()
        {
            static string Physical(DirectoryInfo directory)
            {
                if (directory.LinkTarget is not null)
                    return Physical(new DirectoryInfo(directory.ResolveLinkTarget(returnFinalTarget: true)!.FullName));
                return directory.Parent is null ? directory.FullName : System.IO.Path.Combine(Physical(directory.Parent), directory.Name);
            }
            return Physical(new DirectoryInfo(System.IO.Path.GetTempPath()));
        }
    }

    private sealed class MutatingRows : IReadOnlyList<StartupPreviewRow>
    {
        private readonly StartupPreviewRow[] _rows = [Row(), Row()];
        public int Count => _rows.Length;
        public StartupPreviewRow this[int index]
        {
            get
            {
                if (index == 1) _rows[0] = _rows[0] with { Title = "changed" };
                return _rows[index];
            }
        }
        public IEnumerator<StartupPreviewRow> GetEnumerator() => ((IEnumerable<StartupPreviewRow>)_rows).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class GrowingStream(int available) : Stream
    {
        public int Consumed { get; private set; }
        public int LargestRequested { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            LargestRequested = Math.Max(LargestRequested, count);
            int returned = Math.Min(Math.Min(count, 7919), available - Consumed);
            Array.Fill(buffer, (byte)' ', offset, returned);
            Consumed += returned;
            return returned;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException("A growing source has no trusted length.");
        public override long Position { get => Consumed; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
