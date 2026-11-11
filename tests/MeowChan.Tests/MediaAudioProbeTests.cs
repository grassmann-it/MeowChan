using System.Buffers.Binary;
using System.Text;
using MeowChan.Gallery;

namespace MeowChan.Tests;

public sealed class MediaAudioProbeTests
{
    private static string Fixture(params string[] parts)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "tests", "Fixtures"))) root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine([root!.FullName, "tests", "Fixtures", .. parts]);
    }

    private static MediaAudio ProbeFile(string path, out long bytesRead)
    {
        using CountingStream stream = new(File.OpenRead(path));
        MediaAudio result = MediaAudioProbe.Probe(stream);
        bytesRead = stream.BytesRead;
        return result;
    }

    private static MediaAudio Probe(byte[] bytes) => MediaAudioProbe.Probe(new MemoryStream(bytes));

    // Counts what the probe actually reads; seeking over payload must not show up here.
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { int n = inner.Read(buffer, offset, count); BytesRead += n; return n; }
        public override int Read(Span<byte> buffer) { int n = inner.Read(buffer); BytesRead += n; return n; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    // ---- real files -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("release102/playback-h264-audio.mp4", "HasSound")]
    [InlineData("release102/playback-h264-silent.mp4", "Silent")]
    [InlineData("release102/playback-vp8-audio.webm", "HasSound")]
    // These two contain only a video stream (tests/Fixtures/README.md says no audio; ffprobe agrees).
    [InlineData("gallery-playback-vp8.webm", "Silent")]
    [InlineData("gallery-playback-vp9.webm", "Silent")]
    public void RealFixturesAreClassifiedByTheirTrackHeaders(string file, string expected)
    {
        Assert.Equal(Enum.Parse<MediaAudio>(expected), ProbeFile(Fixture(file.Split('/')), out long bytesRead));
        // Tracks sit near the start (or in moov): the answer costs a few kilobytes of a file of hundreds.
        Assert.InRange(bytesRead, 1, 64 * 1024);
    }

    [Fact]
    public void ImagesAndGifsAreNeverClassified()
    {
        foreach (string gif in Directory.GetFiles(Fixture("gif-playback"), "*.gif"))
            Assert.Equal(MediaAudio.Unknown, ProbeFile(gif, out _));
    }

    // ---- ISO BMFF, built here -----------------------------------------------------------------------------

    private static byte[] Box(string type, params byte[][] children)
    {
        int size = 8 + children.Sum(child => child.Length);
        byte[] result = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)size);
        Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
        int at = 8;
        foreach (byte[] child in children) { child.CopyTo(result, at); at += child.Length; }
        return result;
    }

    private static byte[] Hdlr(string handler) => Box("hdlr", new byte[8], Encoding.ASCII.GetBytes(handler), new byte[13]);
    private static byte[] Trak(string? handler) => Box("trak", Box("tkhd", new byte[84]), handler == null ? Box("mdia", Box("mdhd", new byte[24])) : Box("mdia", Box("mdhd", new byte[24]), Hdlr(handler), Box("minf", new byte[200])));
    private static byte[] Moov(params byte[][] traks) => Box("moov", [Box("mvhd", new byte[100]), .. traks]);
    private static byte[] Ftyp() => Box("ftyp", Encoding.ASCII.GetBytes("isom"), new byte[8], Encoding.ASCII.GetBytes("isom"));
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    [Fact]
    public void Mp4WithASoundHandlerHasSoundAndWithoutOneIsSilent()
    {
        Assert.Equal(MediaAudio.HasSound, Probe(Concat(Ftyp(), Moov(Trak("vide"), Trak("soun")), Box("mdat", new byte[64]))));
        Assert.Equal(MediaAudio.HasSound, Probe(Concat(Ftyp(), Moov(Trak("soun")))));
        Assert.Equal(MediaAudio.Silent, Probe(Concat(Ftyp(), Moov(Trak("vide")), Box("mdat", new byte[64]))));
        Assert.Equal(MediaAudio.Silent, Probe(Concat(Ftyp(), Moov(Trak("vide"), Trak("text"), Trak("tmcd")))));
    }

    [Fact]
    public void MoovAfterAHugeMdatIsFoundBySeekingNotReading()
    {
        // 6 MiB of payload is more than the whole read budget; the probe must step over it.
        byte[] data = Concat(Ftyp(), Box("mdat", new byte[6 * 1024 * 1024]), Moov(Trak("vide"), Trak("soun")));
        using CountingStream stream = new(new MemoryStream(data));
        Assert.Equal(MediaAudio.HasSound, MediaAudioProbe.Probe(stream));
        Assert.True(stream.BytesRead < 1024, "Read " + stream.BytesRead + " bytes.");

        // 64-bit box size for the mdat, as large files use.
        byte[] mdat = new byte[16 + 5000];
        BinaryPrimitives.WriteUInt32BigEndian(mdat, 1);
        Encoding.ASCII.GetBytes("mdat").CopyTo(mdat, 4);
        BinaryPrimitives.WriteUInt64BigEndian(mdat.AsSpan(8), (ulong)mdat.Length);
        Assert.Equal(MediaAudio.Silent, Probe(Concat(Ftyp(), mdat, Moov(Trak("vide")))));
    }

    [Fact]
    public void AnythingItCannotDecideIsUnknownAndNeverSilent()
    {
        byte[] full = Concat(Ftyp(), Moov(Trak("vide"), Trak("soun")), Box("mdat", new byte[64]));
        // Cut inside the movie box at every length: never a wrong answer, and never "silent".
        int moovEnd = Ftyp().Length + Moov(Trak("vide"), Trak("soun")).Length;
        for (int length = 0; length < Ftyp().Length + 8; length++)
            Assert.Equal(MediaAudio.Unknown, Probe(full[..length]));
        for (int length = Ftyp().Length + 8; length < moovEnd; length++)
            Assert.NotEqual(MediaAudio.Silent, Probe(full[..length]));
        Assert.Equal(MediaAudio.Unknown, Probe(full[..(Ftyp().Length + 40)]));

        // A track whose handler is missing could be the sound track: not "silent".
        Assert.Equal(MediaAudio.Unknown, Probe(Concat(Ftyp(), Moov(Trak("vide"), Trak(null)))));
        // No movie box at all, only payload.
        Assert.Equal(MediaAudio.Unknown, Probe(Concat(Ftyp(), Box("mdat", new byte[500]))));
        Assert.Equal(MediaAudio.Unknown, Probe(Concat(Ftyp(), Moov())));
        // A box that claims more than the file holds, a box smaller than its own header, a handler box that is too short.
        byte[] lying = Concat(Ftyp(), Moov(Trak("vide")));
        BinaryPrimitives.WriteUInt32BigEndian(lying.AsSpan(Ftyp().Length), (uint)lying.Length);
        Assert.Equal(MediaAudio.Unknown, Probe(lying));
        byte[] tiny = Concat(Ftyp(), Moov(Trak("vide")));
        BinaryPrimitives.WriteUInt32BigEndian(tiny.AsSpan(Ftyp().Length), 4);
        Assert.Equal(MediaAudio.Unknown, Probe(tiny));
        Assert.Equal(MediaAudio.Unknown, Probe(Concat(Ftyp(), Box("moov", Box("trak", Box("mdia", Box("hdlr", new byte[6])))))));
        // A mdat that runs to the end of the file (size 0) leaves no room for a movie box.
        byte[] open = Concat(Ftyp(), Box("mdat", new byte[300]));
        BinaryPrimitives.WriteUInt32BigEndian(open.AsSpan(Ftyp().Length), 0);
        Assert.Equal(MediaAudio.Unknown, Probe(open));
    }

    [Fact]
    public void GarbageAndOtherFormatsAreUnknown()
    {
        Assert.Equal(MediaAudio.Unknown, Probe([]));
        Assert.Equal(MediaAudio.Unknown, Probe(new byte[11]));
        Assert.Equal(MediaAudio.Unknown, Probe(new byte[4096]));
        Assert.Equal(MediaAudio.Unknown, Probe(Encoding.ASCII.GetBytes("This is not a video file, merely some text of a certain length.")));
        byte[] random = new byte[20_000];
        new Random(7).NextBytes(random);
        Assert.Equal(MediaAudio.Unknown, Probe(random));
        Assert.Equal(MediaAudio.Unknown, ProbeFile(Fixture("gif-playback", "animated-loop.gif"), out _));
        // Looks like a box file at the start, then junk where boxes belong.
        byte[] junk = Concat(Ftyp(), random);
        Assert.Equal(MediaAudio.Unknown, Probe(junk));
        // The Matroska signature followed by junk.
        Assert.Equal(MediaAudio.Unknown, Probe(Concat([0x1A, 0x45, 0xDF, 0xA3], random)));
        // AVI (not supported) is not guessed at either.
        Assert.Equal(MediaAudio.Unknown, Probe(Concat(Encoding.ASCII.GetBytes("RIFF"), new byte[4], Encoding.ASCII.GetBytes("AVI LIST"), new byte[400])));
    }

    [Fact]
    public void AFileOfEndlessTinyBoxesRunsOutOfBudgetAndIsUnknown()
    {
        // One movie box holding hundreds of thousands of tiny boxes, then a sound track: the headers alone exceed the budget.
        int count = 600_000;
        byte[] filler = new byte[count * 8];
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(filler.AsSpan(i * 8), 8);
            Encoding.ASCII.GetBytes("free").CopyTo(filler, i * 8 + 4);
        }
        Assert.True(filler.Length > MediaAudioProbe.MaximumReadBytes);
        Assert.Equal(MediaAudio.Unknown, Probe(Concat(Ftyp(), Box("moov", filler, Trak("soun")))));
        // And a large payload of ordinary sized boxes does not count against the budget at all.
        Assert.Equal(MediaAudio.HasSound, Probe(Concat(Ftyp(), Box("moov", Box("free", new byte[3 * 1024 * 1024]), Trak("soun")))));
    }

    // ---- Matroska, built here -----------------------------------------------------------------------------

    private static byte[] Vint(long value, int length)
    {
        byte[] result = new byte[length];
        for (int i = length - 1; i >= 0; i--) { result[i] = (byte)value; value >>= 8; }
        result[0] |= (byte)(0x80 >> (length - 1));
        return result;
    }

    private static byte[] Element(uint id, params byte[][] children)
    {
        byte[] idBytes = id > 0xFFFFFF ? [(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id]
            : id > 0xFFFF ? [(byte)(id >> 16), (byte)(id >> 8), (byte)id] : id > 0xFF ? [(byte)(id >> 8), (byte)id] : [(byte)id];
        int size = children.Sum(child => child.Length);
        return Concat(idBytes, Vint(size, 4), Concat(children));
    }

    private static byte[] UnknownSize(uint id, params byte[][] children)
    {
        byte[] idBytes = [(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id];
        return Concat(idBytes, [0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], Concat(children));
    }

    private static byte[] Uint(uint id, long value) => Element(id, [(byte)value]);
    private static byte[] Track(long? type, bool codec = true) => Element(0xAE, Uint(0xD7, 1), type == null ? [] : Uint(0x83, type.Value), codec ? Element(0x86, Encoding.ASCII.GetBytes("V_VP8")) : []);
    private static byte[] Tracks(params byte[][] entries) => Element(0x1654AE6B, entries);
    private static byte[] EbmlHeader() => Element(0x1A45DFA3, Element(0x4282, Encoding.ASCII.GetBytes("webm")));
    private static byte[] Cluster(int bytes) => Element(0x1F43B675, Uint(0xE7, 0), Element(0xA3, new byte[bytes]));
    private static byte[] Segment(params byte[][] children) => Element(0x18538067, children);
    private static byte[] Void(int bytes) => Element(0xEC, new byte[bytes]);

    private static byte[] SeekHeadTo(long relativeTracksPosition) => Element(0x114D9B74,
        Element(0x4DBB, Element(0x53AB, [0x16, 0x54, 0xAE, 0x6B]), Element(0x53AC, [(byte)(relativeTracksPosition >> 24), (byte)(relativeTracksPosition >> 16), (byte)(relativeTracksPosition >> 8), (byte)relativeTracksPosition])));

    [Fact]
    public void WebmTrackTypesDecideSoundAndSilence()
    {
        Assert.Equal(MediaAudio.HasSound, Probe(Concat(EbmlHeader(), Segment(Tracks(Track(1), Track(2)), Cluster(100)))));
        Assert.Equal(MediaAudio.HasSound, Probe(Concat(EbmlHeader(), Segment(Tracks(Track(2)), Cluster(100)))));
        Assert.Equal(MediaAudio.Silent, Probe(Concat(EbmlHeader(), Segment(Tracks(Track(1)), Cluster(100)))));
        Assert.Equal(MediaAudio.Silent, Probe(Concat(EbmlHeader(), Segment(Void(40), Tracks(Track(1), Track(17)), Cluster(100)))));
    }

    [Fact]
    public void WebmTracksBehindClustersAreFoundWithoutReadingThem()
    {
        // Without a seek head: the clusters are stepped over by their size.
        byte[] data = Concat(EbmlHeader(), Segment(Cluster(2_000_000), Cluster(2_000_000), Cluster(2_000_000), Tracks(Track(1), Track(2))));
        using CountingStream plain = new(new MemoryStream(data));
        Assert.Equal(MediaAudio.HasSound, MediaAudioProbe.Probe(plain));
        Assert.True(plain.BytesRead < 1024, "Read " + plain.BytesRead + " bytes.");

        // With a seek head the probe goes straight to Tracks. Segment data starts after the header and the segment id and size (4 + 4).
        byte[] head = EbmlHeader();
        byte[] clusters = Concat(Cluster(300), Cluster(300));
        byte[] seekPlaceholder = SeekHeadTo(0);
        long tracksAt = seekPlaceholder.Length + clusters.Length;
        byte[] seeked = Concat(head, Segment(SeekHeadTo(tracksAt), clusters, Tracks(Track(1))));
        Assert.Equal(MediaAudio.Silent, Probe(seeked));
        // A seek head that points at something else than Tracks is not followed into guesses.
        byte[] wrong = Concat(head, Segment(SeekHeadTo(tracksAt - 3), clusters, Tracks(Track(1))));
        Assert.Equal(MediaAudio.Unknown, Probe(wrong));
    }

    [Fact]
    public void WebmWithUnknownSizesAndDamageIsUnknownUnlessItIsClear()
    {
        // Live-style segment of unknown size with Tracks first.
        Assert.Equal(MediaAudio.HasSound, Probe(Concat(EbmlHeader(), UnknownSize(0x18538067, Tracks(Track(2)), Cluster(10)))));
        // A cluster of unknown size cannot be stepped over, so Tracks behind it cannot be reached.
        Assert.Equal(MediaAudio.Unknown, Probe(Concat(EbmlHeader(), Segment(UnknownSize(0x1F43B675, Uint(0xE7, 0)), Tracks(Track(2))))));
        // A track without a type could be the sound track.
        Assert.Equal(MediaAudio.Unknown, Probe(Concat(EbmlHeader(), Segment(Tracks(Track(1), Track(null))))));
        Assert.Equal(MediaAudio.HasSound, Probe(Concat(EbmlHeader(), Segment(Tracks(Track(null), Track(2))))));
        Assert.Equal(MediaAudio.Unknown, Probe(Concat(EbmlHeader(), Segment(Tracks()))));
        Assert.Equal(MediaAudio.Unknown, Probe(Concat(EbmlHeader(), Segment(Cluster(10)))));
        // Cut off inside Tracks: never "silent".
        byte[] full = Concat(EbmlHeader(), Segment(Tracks(Track(1), Track(2)), Cluster(100)));
        int tracksStart = EbmlHeader().Length + 8;
        for (int length = 0; length < tracksStart + Tracks(Track(1), Track(2)).Length; length++)
            Assert.NotEqual(MediaAudio.Silent, Probe(full[..length]));
        // Segment of the wrong kind or a header that never ends.
        Assert.Equal(MediaAudio.Unknown, Probe(Concat(EbmlHeader(), Element(0x1549A966, new byte[20]))));
        Assert.Equal(MediaAudio.Unknown, Probe(Concat([0x1A, 0x45, 0xDF, 0xA3, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], new byte[100])));
    }
}
