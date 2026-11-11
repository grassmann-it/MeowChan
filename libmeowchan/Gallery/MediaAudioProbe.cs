using System;
using System.Buffers.Binary;
using System.IO;

namespace MeowChan.Gallery;

/// <summary>
/// What a probe could say about the sound of a video file. Unknown covers "not probed yet" as well as
/// "the headers do not tell": it is never rounded to Silent.
/// </summary>
internal enum MediaAudio { Unknown, Silent, HasSound }

/// <summary>
/// Reads the container headers of a video to learn whether it has an audio track. Managed code only:
/// no decoder, no player and no external tool. It reads track headers and skips media payload
/// (mdat boxes, Matroska clusters) by seeking, within a strict budget of bytes actually read.
/// Everything it cannot decide for certain (unsupported container, truncated or damaged headers,
/// over budget, tracks it cannot classify) is <see cref="MediaAudio.Unknown"/>, never a guess.
/// Supported: ISO BMFF (MP4, MOV, M4V) and Matroska (WebM, MKV).
/// </summary>
internal static class MediaAudioProbe
{
    /// <summary>Changes whenever the parser's answers can change, so cached results are probed again.</summary>
    internal const int Version = 1;
    /// <summary>Upper bound of bytes read from the file; seeking over payload is free.</summary>
    internal const int MaximumReadBytes = 4 * 1024 * 1024;
    private const int MaximumElements = 100_000;
    private const int MaximumDepth = 8;

    /// <summary>
    /// The stream must be readable and seekable. IO errors of the stream itself propagate (the file is
    /// then unavailable, which is not the same as undecidable); a short file is a truncated file.
    /// </summary>
    internal static MediaAudio Probe(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        Reader reader = new(stream);
        try
        {
            if (reader.Length < 12) return MediaAudio.Unknown;
            Span<byte> head = stackalloc byte[8];
            reader.Seek(0);
            reader.ReadExactly(head);
            if (BinaryPrimitives.ReadUInt32BigEndian(head) == Ebml.HeaderId) return Matroska.Probe(reader);
            return IsBoxType(head.Slice(4, 4)) ? IsoBmff.Probe(reader) : MediaAudio.Unknown;
        }
        catch (Undecidable) { return MediaAudio.Unknown; }
    }

    // Boxes a file may start with. Anything else is not a container this probe understands.
    private static bool IsBoxType(ReadOnlySpan<byte> type) => type.SequenceEqual("ftyp"u8) || type.SequenceEqual("moov"u8)
        || type.SequenceEqual("mdat"u8) || type.SequenceEqual("free"u8) || type.SequenceEqual("skip"u8)
        || type.SequenceEqual("wide"u8) || type.SequenceEqual("pnot"u8);

    private sealed class Undecidable : Exception { }

    // Budgeted view of the stream: every byte read is counted, seeking is not.
    private sealed class Reader
    {
        private readonly Stream _stream;
        private int _remaining = MaximumReadBytes;
        private int _elements;

        internal Reader(Stream stream) { _stream = stream; Length = stream.Length; }

        internal long Length { get; }
        internal long Position => _stream.Position;

        internal void Seek(long position)
        {
            if (position < 0 || position > Length) throw new Undecidable();
            _stream.Position = position;
        }

        internal void ReadExactly(Span<byte> buffer)
        {
            if (buffer.Length > _remaining) throw new Undecidable(); // over budget
            _remaining -= buffer.Length;
            try { _stream.ReadExactly(buffer); }
            catch (EndOfStreamException) { throw new Undecidable(); } // truncated
        }

        // Each header read counts as one element, so a file of endless tiny boxes ends here too.
        internal void CountElement() { if (++_elements > MaximumElements) throw new Undecidable(); }

        internal long ReadUnsigned(int length)
        {
            Span<byte> buffer = stackalloc byte[8];
            ReadExactly(buffer[..length]);
            long value = 0;
            foreach (byte item in buffer[..length]) value = (value << 8) | item;
            return value;
        }
    }

    private static class IsoBmff
    {
        internal static MediaAudio Probe(Reader reader)
        {
            long position = 0;
            while (position + 8 <= reader.Length)
            {
                reader.Seek(position);
                Box box = ReadBox(reader, reader.Length, topLevel: true);
                // moov may come after a huge mdat: the payload is stepped over, never read.
                if (box.Type == "moov") return Moov(reader, box);
                position = box.End;
            }
            return MediaAudio.Unknown; // no movie header
        }

        private readonly record struct Box(string Type, long Start, long PayloadStart, long End);

        private static Box ReadBox(Reader reader, long parentEnd, bool topLevel = false)
        {
            reader.CountElement();
            long start = reader.Position;
            Span<byte> header = stackalloc byte[8];
            reader.ReadExactly(header);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            long payload = start + 8;
            if (size == 1)
            {
                Span<byte> large = stackalloc byte[8];
                reader.ReadExactly(large);
                ulong value = BinaryPrimitives.ReadUInt64BigEndian(large);
                if (value > long.MaxValue) throw new Undecidable();
                size = (long)value;
                payload += 8;
            }
            else if (size == 0) size = parentEnd - start; // to the end of the enclosing box or file
            if (size < payload - start || start + size > parentEnd) throw new Undecidable(); // too small, or beyond its parent: damaged or truncated
            string type = System.Text.Encoding.Latin1.GetString(header.Slice(4, 4));
            return new Box(type, start, payload, start + size);
        }

        // The movie box holds one trak per track; only its handler type (mdia/hdlr) says what the track is.
        private static MediaAudio Moov(Reader reader, Box moov)
        {
            int tracks = 0, classified = 0;
            foreach (Box child in Children(reader, moov))
            {
                if (child.Type != "trak") continue;
                tracks++;
                string? handler = Handler(reader, child);
                if (handler == null) continue;
                if (handler == "soun") return MediaAudio.HasSound;
                classified++;
            }
            // No sound only if every track is positively something else.
            return tracks > 0 && classified == tracks ? MediaAudio.Silent : MediaAudio.Unknown;
        }

        private static string? Handler(Reader reader, Box trak)
        {
            foreach (Box child in Children(reader, trak))
            {
                if (child.Type != "mdia") continue;
                foreach (Box inner in Children(reader, child))
                {
                    if (inner.Type != "hdlr") continue;
                    // version/flags, pre_defined, then the four-character handler type.
                    if (inner.End - inner.PayloadStart < 12) throw new Undecidable();
                    reader.Seek(inner.PayloadStart + 8);
                    Span<byte> type = stackalloc byte[4];
                    reader.ReadExactly(type);
                    return System.Text.Encoding.Latin1.GetString(type);
                }
            }
            return null;
        }

        // Direct children of a box, found by header reads and seeks only.
        private static System.Collections.Generic.IEnumerable<Box> Children(Reader reader, Box parent)
        {
            long position = parent.PayloadStart;
            while (position + 8 <= parent.End)
            {
                reader.Seek(position);
                Box child = ReadBox(reader, parent.End);
                yield return child;
                position = child.End;
            }
        }
    }

    private static class Ebml
    {
        internal const uint HeaderId = 0x1A45DFA3;
        internal const uint Segment = 0x18538067;
        internal const uint SeekHead = 0x114D9B74;
        internal const uint Seek = 0x4DBB;
        internal const uint SeekId = 0x53AB;
        internal const uint SeekPosition = 0x53AC;
        internal const uint Tracks = 0x1654AE6B;
        internal const uint TrackEntry = 0xAE;
        internal const uint TrackType = 0x83;
        internal const long Unknown = -1;

        internal readonly record struct Element(uint Id, long DataStart, long Size)
        {
            internal bool UnknownSize => Size == Unknown;
            internal long End => DataStart + Size;
        }

        internal static Element Read(Reader reader)
        {
            reader.CountElement();
            Span<byte> first = stackalloc byte[1];
            reader.ReadExactly(first);
            int idLength = System.Numerics.BitOperations.LeadingZeroCount((uint)first[0]) - 24 + 1;
            if (first[0] == 0 || idLength > 4) throw new Undecidable();
            uint id = first[0];
            if (idLength > 1) id = (id << (8 * (idLength - 1))) | (uint)reader.ReadUnsigned(idLength - 1);

            reader.ReadExactly(first);
            int sizeLength = System.Numerics.BitOperations.LeadingZeroCount((uint)first[0]) - 24 + 1;
            if (first[0] == 0) throw new Undecidable();
            long value = first[0] & ((1 << (8 - sizeLength)) - 1);
            long all = (1L << (8 * sizeLength - sizeLength)) - 1;
            if (sizeLength > 1) value = (value << (8 * (sizeLength - 1))) | reader.ReadUnsigned(sizeLength - 1);
            return new Element(id, reader.Position, value == all ? Unknown : value);
        }
    }

    private static class Matroska
    {
        internal static MediaAudio Probe(Reader reader)
        {
            reader.Seek(0);
            Ebml.Element header = Ebml.Read(reader);
            if (header.Id != Ebml.HeaderId || header.UnknownSize) throw new Undecidable();
            long position = header.End;
            reader.Seek(position);
            Ebml.Element segment = Ebml.Read(reader);
            if (segment.Id != Ebml.Segment) throw new Undecidable();
            // A cut-off download declares more than it has; what is there may still hold the tracks.
            long segmentEnd = segment.UnknownSize ? reader.Length : Math.Min(segment.End, reader.Length);
            position = segment.DataStart;
            long? tracksAt = null;
            bool jumped = false;
            while (position + 2 <= segmentEnd)
            {
                reader.Seek(position);
                Ebml.Element element = Ebml.Read(reader);
                if (element.Id == Ebml.Tracks) return Tracks(reader, element, segmentEnd);
                if (jumped) throw new Undecidable(); // the seek head pointed somewhere else than Tracks
                if (element.UnknownSize || element.End > segmentEnd) throw new Undecidable(); // cannot step over it
                if (element.Id == Ebml.SeekHead && tracksAt == null) tracksAt = FindTracks(reader, element, segment.DataStart);
                position = element.End;
                // The seek head says where Tracks is, so the clusters in between never need a visit.
                if (tracksAt is long target && !jumped && target > position && target < segmentEnd) { position = target; jumped = true; }
            }
            return MediaAudio.Unknown;
        }

        private static long? FindTracks(Reader reader, Ebml.Element seekHead, long segmentStart)
        {
            long position = seekHead.DataStart;
            while (position + 2 <= seekHead.End)
            {
                reader.Seek(position);
                Ebml.Element seek = Ebml.Read(reader);
                if (seek.UnknownSize || seek.End > seekHead.End) throw new Undecidable();
                if (seek.Id == Ebml.Seek)
                {
                    uint? id = null; long? at = null;
                    long inner = seek.DataStart;
                    while (inner + 2 <= seek.End)
                    {
                        reader.Seek(inner);
                        Ebml.Element field = Ebml.Read(reader);
                        if (field.UnknownSize || field.End > seek.End || field.Size is < 0 or > 8) throw new Undecidable();
                        if (field.Id == Ebml.SeekId) id = (uint)reader.ReadUnsigned((int)field.Size);
                        else if (field.Id == Ebml.SeekPosition) at = reader.ReadUnsigned((int)field.Size);
                        inner = field.End;
                    }
                    if (id == Ebml.Tracks && at is long relative) return segmentStart + relative;
                }
                position = seek.End;
            }
            return null;
        }

        private static MediaAudio Tracks(Reader reader, Ebml.Element tracks, long segmentEnd)
        {
            if (tracks.UnknownSize || tracks.End > segmentEnd) throw new Undecidable();
            int entries = 0, typed = 0;
            long position = tracks.DataStart;
            while (position + 2 <= tracks.End)
            {
                reader.Seek(position);
                Ebml.Element entry = Ebml.Read(reader);
                if (entry.UnknownSize || entry.End > tracks.End) throw new Undecidable();
                if (entry.Id == Ebml.TrackEntry)
                {
                    entries++;
                    long? type = TrackType(reader, entry);
                    if (type == 2) return MediaAudio.HasSound;
                    if (type != null) typed++;
                }
                position = entry.End;
            }
            // No sound only if every entry positively says what it is.
            return entries > 0 && typed == entries ? MediaAudio.Silent : MediaAudio.Unknown;
        }

        private static long? TrackType(Reader reader, Ebml.Element entry)
        {
            long position = entry.DataStart;
            while (position + 2 <= entry.End)
            {
                reader.Seek(position);
                Ebml.Element field = Ebml.Read(reader);
                if (field.UnknownSize || field.End > entry.End) throw new Undecidable();
                if (field.Id == Ebml.TrackType)
                {
                    if (field.Size is < 1 or > 8) throw new Undecidable();
                    return reader.ReadUnsigned((int)field.Size);
                }
                position = field.End;
            }
            return null;
        }
    }
}
