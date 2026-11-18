using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using MeowChan.Gallery;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;

namespace MeowChan.FolderSources;

/// <summary>
/// Where one item of a folder source stands. <see cref="Seen"/>: found by a scan, bytes not hashed yet (it may be shown,
/// never written to). <see cref="Hashed"/>: its SHA-256 is known, so favorites, ratings and tags can name it.
/// <see cref="Missing"/>: the file is gone or its bytes changed; the row stays so tags, favorites and ratings keep a target.
/// </summary>
internal enum FolderSourceItemState { Seen = 0, Hashed = 1, Missing = 2 }

/// <summary>Stable error codes of the shadow catalog. Never a path or an exception text.</summary>
internal enum FolderSourceError
{
    None,
    /// <summary>The catalog file is damaged or not a folder source catalog.</summary>
    CatalogDamaged,
    /// <summary>The catalog was written by a newer version.</summary>
    CatalogNewer,
    /// <summary>The catalog reached its configured size limit.</summary>
    CatalogFull,
    /// <summary>The configured number of folder sources is reached.</summary>
    TooManyRoots,
    /// <summary>The folder is missing, not a folder, a link, or unreadable.</summary>
    FolderUnavailable,
    /// <summary>The folder is already a source, or lies inside or around one.</summary>
    FolderOverlaps,
    /// <summary>The folder is the profile folder or lies inside it.</summary>
    FolderIsProfile,
    /// <summary>The folder is the download folder or lies inside it.</summary>
    FolderIsDownloads,
    /// <summary>The name is empty or too long.</summary>
    InvalidName,
    /// <summary>No such folder source.</summary>
    UnknownRoot
}

internal sealed class FolderSourceException : IOException
{
    internal FolderSourceException(FolderSourceError error) : base("Folder source: " + error) => Error = error;
    internal FolderSourceError Error { get; }
}

/// <summary>One folder source: the user's folder (absolute, as chosen), its display name and options.</summary>
internal sealed record FolderSourceRoot(Guid Id, string Path, string Name, DateTimeOffset CreatedUtc, bool IncludeSubfolders,
    DateTimeOffset? LastScanUtc, FolderSourceScanStatus? LastScanStatus)
{
    internal ResourceId Thread => FolderSourceIdentity.Thread(Id);
}

/// <summary>
/// One file of a folder source as the shadow catalog knows it. <see cref="ModifiedTicks"/> is the file's last write time
/// in UTC ticks when it was seen; together with <see cref="Length"/> it decides whether a later scan sees the same file.
/// </summary>
internal sealed record FolderSourceItem(Guid Id, Guid RootId, string RelativePath, long Length, long ModifiedTicks,
    string? Sha256, SavedMediaKind Kind, FolderSourceItemState State, DateTimeOffset FirstSeenUtc)
{
    internal string Name => RelativePath.Contains('/') ? RelativePath[(RelativePath.LastIndexOf('/') + 1)..] : RelativePath;
    internal string AssetId => FolderSourceIdentity.AssetId(Id);
    internal string MediaIdentifier => FolderSourceIdentity.MediaIdentifier(Id);
    internal string RequestedFileName => FolderSourceIdentity.RequestedFileName(Id, RelativePath);
    internal DateTimeOffset Modified => new(Math.Clamp(ModifiedTicks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), TimeSpan.Zero);
}

/// <summary>A digest to record: the item and the length and write time its bytes had while they were hashed.</summary>
internal sealed record FolderSourceDigest(Guid Item, long Length, long ModifiedTicks, string Sha256);

/// <summary>Counts of one folder source (active items, hashed ones, missing rows).</summary>
internal sealed record FolderSourceCounts(Guid RootId, int Files, int Hashed, int Missing);

/// <summary>An immutable view of the whole shadow catalog.</summary>
internal sealed class FolderSourceCatalogSnapshot
{
    private readonly Dictionary<Guid, FolderSourceRoot> _roots;
    private readonly FolderSourceRoot[] _rootArray;
    private readonly FolderSourceItem[] _itemArray;

    internal FolderSourceCatalogSnapshot(long revision, string virtualRoot, IEnumerable<FolderSourceRoot> roots, IEnumerable<FolderSourceItem> items)
        : this(revision, virtualRoot, roots.OrderBy(root => root.CreatedUtc).ThenBy(root => root.Id).ToArray(), items.ToArray(), null, null) { }

    private FolderSourceCatalogSnapshot(long revision, string virtualRoot, FolderSourceRoot[] roots, FolderSourceItem[] items,
        Dictionary<Guid, int>? positions, Dictionary<(Guid, string), FolderSourceItem[]>? versions)
    {
        Revision = revision;
        VirtualRoot = virtualRoot;
        _rootArray = roots;
        _itemArray = items;
        Roots = Array.AsReadOnly(roots);
        Items = Array.AsReadOnly(items);
        _roots = roots.ToDictionary(root => root.Id);
        _positions = positions;
        _versions = versions;
    }

    internal static FolderSourceCatalogSnapshot Empty(string virtualRoot) =>
        new(0, virtualRoot, Array.Empty<FolderSourceRoot>(), Array.Empty<FolderSourceItem>());

    internal long Revision { get; }
    /// <summary>The folder below which the virtual item folders of this profile are named.</summary>
    internal string VirtualRoot { get; }
    internal IReadOnlyList<FolderSourceRoot> Roots { get; }
    internal IReadOnlyList<FolderSourceItem> Items { get; }
    internal FolderSourceRoot? Root(Guid id) => _roots.GetValueOrDefault(id);

    private Dictionary<Guid, int>? _positions;

    /// <summary>One item by id (the lookup is built on first use and shared with the views <see cref="WithHashed"/> derives).</summary>
    internal FolderSourceItem? Item(Guid id)
    {
        Dictionary<Guid, int> positions = Volatile.Read(ref _positions) ?? BuildPositions();
        return positions.TryGetValue(id, out int position) ? _itemArray[position] : null;
    }

    /// <summary>
    /// This view after the given digests were recorded (W3-06): the same rows in the same order, each named item now hashed, under
    /// <paramref name="revision"/>. The id lookup and the earlier-version lookup are shared (recording a digest changes neither the
    /// order nor any missing row), so keeping the view current while hashing costs one array copy per committed batch instead of a
    /// full read of the catalog.
    /// </summary>
    internal FolderSourceCatalogSnapshot WithHashed(long revision, IReadOnlyCollection<(Guid Item, string Sha256)> hashed)
    {
        Dictionary<Guid, int> positions = Volatile.Read(ref _positions) ?? BuildPositions();
        FolderSourceItem[] items = _itemArray;
        if (hashed.Count > 0)
        {
            items = (FolderSourceItem[])_itemArray.Clone();
            foreach ((Guid id, string sha) in hashed)
                if (positions.TryGetValue(id, out int position) && items[position].State == FolderSourceItemState.Seen)
                    items[position] = items[position] with { Sha256 = sha, State = FolderSourceItemState.Hashed };
        }
        return new(revision, VirtualRoot, _rootArray, items, positions, Volatile.Read(ref _versions));
    }

    private Dictionary<(Guid, string), FolderSourceItem[]>? _versions;

    /// <summary>
    /// The earlier hashed versions of the file at <paramref name="relativePath"/> of a root: its missing rows (bytes changed, file gone)
    /// that were once hashed, newest first. Empty when there are none. The lookup is built on first use over the missing rows only.
    /// </summary>
    internal IReadOnlyList<FolderSourceItem> PreviousVersions(Guid root, string relativePath)
    {
        Dictionary<(Guid, string), FolderSourceItem[]> versions = Volatile.Read(ref _versions) ?? BuildVersions();
        return versions.TryGetValue((root, relativePath), out FolderSourceItem[]? found) ? found : Array.Empty<FolderSourceItem>();
    }

    private Dictionary<(Guid, string), FolderSourceItem[]> BuildVersions()
    {
        Dictionary<(Guid, string), FolderSourceItem[]> versions = Items
            .Where(item => item.State == FolderSourceItemState.Missing && item.Sha256 != null)
            .GroupBy(item => (item.RootId, item.RelativePath))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.FirstSeenUtc).ToArray());
        Volatile.Write(ref _versions, versions);
        return versions;
    }

    private Dictionary<Guid, int> BuildPositions()
    {
        Dictionary<Guid, int> positions = new(_itemArray.Length);
        for (int index = 0; index < _itemArray.Length; index++) positions[_itemArray[index].Id] = index;
        Volatile.Write(ref _positions, positions);
        return positions;
    }
}

/// <summary>
/// The shadow catalog of the folder sources of one profile: SQLite <c>folder-sources.db</c> in the profile folder, schema 1
/// (own file, no change to <c>archive.db</c>). Holds the roots (path, name, options), the items (relative path, length, last
/// write time, SHA-256 once known, kind, state) and the scan runs. Writes are admitted like every profile store
/// (<see cref="ProfileLease.RunStoreWrite{T}"/>) and run in short transactions, so a scan of 100,000 files never holds the
/// profile for long. Reads never take the profile-wide lease monitor (W3-06): the catalog is its own file, so its own lock
/// serializes its readers and writers, and a long read of 2 million rows delays no other store. The catalog file is created by
/// the first added folder; reading a profile without one writes nothing. Nothing here opens the user's files.
/// </summary>
internal sealed class FolderSourceCatalog
{
    internal const string FileName = "folder-sources.db";
    internal const int SchemaVersion = 1;
    /// <summary>"MCFS": the SQLite application id of a folder source catalog.</summary>
    internal const int ApplicationId = 0x4D434653;
    /// <summary>Fixed ceiling: items (with missing ones) a profile's catalog may hold, whatever the settings say.</summary>
    internal const int MaximumItems = 2_000_000;
    /// <summary>Fixed ceiling: characters of a relative path and of a root path.</summary>
    internal const int MaximumPathLength = 1024;
    internal const int MaximumNameLength = 120;
    /// <summary>Rows written per transaction by a scan; large enough that Windows full-sync commit latency does not dominate large scans.</summary>
    internal const int WriteBatchSize = 20_000;

    internal const int DefaultMaxFiles = 200_000, MinimumMaxFiles = 1_000, MaxFilesCeiling = 2_000_000;
    internal const int DefaultMaxRoots = 8, MinimumMaxRoots = 1, MaxRootsCeiling = 64;
    internal const int DefaultCatalogLimitMiB = 256, MinimumCatalogLimitMiB = 16, CatalogLimitMiBCeiling = 4_096;

    private readonly ProfileLease _lease;
    private readonly string _path;
    private readonly string _virtualRoot;
    // Lock order: _io (this file), then the lease monitor (only inside a write transaction), then _sync (the kept view).
    private readonly object _io = new();
    private readonly object _sync = new();
    private FolderSourceCatalogSnapshot? _cached;
    private long _limitBytes = (long)DefaultCatalogLimitMiB * 1024 * 1024;
    private int _maxFiles = DefaultMaxFiles;
    private int _maxRoots = DefaultMaxRoots;

    internal FolderSourceCatalog(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        string directory = ProfileLease.NormalizeDirectory(profileDirectory);
        lease.EnsureMatches(directory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, FileName);
        _virtualRoot = FolderSourceIdentity.VirtualRoot(lease.ProfileDirectory);
    }

    internal string FilePath => _path;
    internal string VirtualRoot => _virtualRoot;
    internal string ProfileDirectory => _lease.ProfileDirectory;

    /// <summary>Size limit of the catalog file (a setting, clamped to its range).</summary>
    internal long LimitBytes
    {
        get => Interlocked.Read(ref _limitBytes);
        set => Interlocked.Exchange(ref _limitBytes, Math.Clamp(value, (long)MinimumCatalogLimitMiB * 1024 * 1024, (long)CatalogLimitMiBCeiling * 1024 * 1024));
    }

    /// <summary>Test seam: a size limit below the settings range, so a test can fill a catalog to its limit with a few rows.</summary>
    internal void SetLimitBytesForTests(long bytes) => Interlocked.Exchange(ref _limitBytes, Math.Max(0, bytes));

    /// <summary>Test seam: runs under the catalog's file lock before every read or write of the file.</summary>
    internal Action? BeforeFileAccessForTests { get; set; }

    private int _fullReads;
    /// <summary>How often <see cref="Read"/> read every row from the file (not from the kept view).</summary>
    internal int FullReads => Volatile.Read(ref _fullReads);

    /// <summary>Most active (not missing) items over all roots (a setting). Lowering it keeps every row; scans add no more.</summary>
    internal int MaxFiles { get => Volatile.Read(ref _maxFiles); set => Volatile.Write(ref _maxFiles, Math.Clamp(value, MinimumMaxFiles, MaxFilesCeiling)); }

    /// <summary>Most folder sources (a setting). Lowering it keeps every root; only new ones are refused.</summary>
    internal int MaxRoots { get => Volatile.Read(ref _maxRoots); set => Volatile.Write(ref _maxRoots, Math.Clamp(value, MinimumMaxRoots, MaxRootsCeiling)); }

    // ---- Reading ----

    /// <summary>
    /// The whole catalog, reused while its revision is unchanged (one small query). A profile without a catalog file is
    /// empty. A damaged or newer catalog throws <see cref="FolderSourceException"/>; it is never turned into "no sources".
    /// </summary>
    internal FolderSourceCatalogSnapshot Read() => ReadFile(() =>
    {
        if (!File.Exists(_path)) { lock (_sync) return _cached = FolderSourceCatalogSnapshot.Empty(_virtualRoot); }
        using SqliteConnection connection = Open(SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        Validate(connection, transaction);
        long revision = Revision(connection, transaction);
        lock (_sync) if (_cached is { } cached && cached.Revision == revision && revision > 0) return cached;
        List<FolderSourceRoot> roots = ReadRoots(connection, transaction);
        List<FolderSourceItem> items = new();
        Interlocked.Increment(ref _fullReads);
        using (SqliteCommand command = Command(connection, transaction,
            "SELECT id,root_id,rel_path,length,mtime_ticks,sha256,kind,state,first_seen_utc FROM items ORDER BY root_id, rel_path, first_seen_utc"))
        using (SqliteDataReader reader = command.ExecuteReader())
            while (reader.Read()) items.Add(ItemOf(reader));
        transaction.Commit();
        FolderSourceCatalogSnapshot read = new(revision, _virtualRoot, roots, items);
        // No write can run meanwhile (both hold _io), so this view is the current one.
        lock (_sync) _cached = read;
        return read;
    });

    /// <summary>The roots only (cheap; the Sources page lists them).</summary>
    internal IReadOnlyList<FolderSourceRoot> ReadRoots() => ReadFile(() =>
    {
        if (!File.Exists(_path)) return (IReadOnlyList<FolderSourceRoot>)Array.Empty<FolderSourceRoot>();
        using SqliteConnection connection = Open(SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        Validate(connection, transaction);
        List<FolderSourceRoot> roots = ReadRoots(connection, transaction);
        transaction.Commit();
        return roots.AsReadOnly();
    });

    /// <summary>One item and its root, or null when either is unknown (one indexed query).</summary>
    internal (FolderSourceItem Item, FolderSourceRoot Root)? Find(Guid item)
    {
        ObjectDisposedException.ThrowIf(!_lease.IsHeld, _lease);
        // Only this process writes the catalog (the profile lease is exclusive) and every write patches or drops the kept view, so
        // a kept view is current: a gallery window of 200 files needs no 200 queries, and the lookup waits for no file access.
        lock (_sync)
            if (_cached is { Revision: > 0 } cached)
                return cached.Item(item) is { } known && cached.Root(known.RootId) is { } owner ? (known, owner) : null;
        return ReadFile(() => FindInFile(item));
    }

    private (FolderSourceItem Item, FolderSourceRoot Root)? FindInFile(Guid item)
    {
        if (!File.Exists(_path)) return null;
        using SqliteConnection connection = Open(SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        Validate(connection, transaction);
        FolderSourceItem? found = FindItem(connection, transaction, item);
        FolderSourceRoot? root = found == null ? null : ReadRoots(connection, transaction).FirstOrDefault(candidate => candidate.Id == found.RootId);
        transaction.Commit();
        return found == null || root == null ? null : (found, root);
    }

    /// <summary>Counts per root (active files, hashed ones, missing rows).</summary>
    internal IReadOnlyList<FolderSourceCounts> Counts() => ReadFile(() =>
    {
        if (!File.Exists(_path)) return (IReadOnlyList<FolderSourceCounts>)Array.Empty<FolderSourceCounts>();
        using SqliteConnection connection = Open(SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        Validate(connection, transaction);
        List<FolderSourceCounts> counts = new();
        using (SqliteCommand command = Command(connection, transaction, """
            SELECT r.id,
              (SELECT COUNT(*) FROM items i WHERE i.root_id = r.id AND i.state <> 2),
              (SELECT COUNT(*) FROM items i WHERE i.root_id = r.id AND i.state = 1),
              (SELECT COUNT(*) FROM items i WHERE i.root_id = r.id AND i.state = 2)
            FROM roots r
            """))
        using (SqliteDataReader reader = command.ExecuteReader())
            while (reader.Read())
                counts.Add(new(ParseId(reader.GetString(0)), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
        transaction.Commit();
        return counts.AsReadOnly();
    });

    /// <summary>
    /// Up to <paramref name="count"/> unhashed items for the background pass, in catalog order after <paramref name="afterRow"/>,
    /// no larger than <paramref name="maximumLength"/>. Returns the row cursor to continue from.
    /// </summary>
    internal (IReadOnlyList<FolderSourceItem> Items, long NextRow) NextUnhashed(int count, long maximumLength, long afterRow) => ReadFile(() =>
    {
        if (!File.Exists(_path)) return ((IReadOnlyList<FolderSourceItem>)Array.Empty<FolderSourceItem>(), afterRow);
        using SqliteConnection connection = Open(SqliteOpenMode.ReadOnly);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        Validate(connection, transaction);
        List<FolderSourceItem> items = new();
        long next = afterRow;
        using (SqliteCommand command = Command(connection, transaction,
            "SELECT id,root_id,rel_path,length,mtime_ticks,sha256,kind,state,first_seen_utc,rowid FROM items WHERE state = 0 AND rowid > $after AND length <= $max ORDER BY rowid LIMIT $count"))
        {
            command.Parameters.AddWithValue("$after", afterRow);
            command.Parameters.AddWithValue("$max", maximumLength);
            command.Parameters.AddWithValue("$count", Math.Max(1, count));
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) { items.Add(ItemOf(reader)); next = reader.GetInt64(9); }
        }
        transaction.Commit();
        return ((IReadOnlyList<FolderSourceItem>)items.AsReadOnly(), next);
    });

    // ---- Writing ----

    /// <summary>Adds a folder source. The caller has validated the folder (see <see cref="FolderSourceValidation"/>).</summary>
    internal FolderSourceRoot AddRoot(string path, string name, bool includeSubfolders, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        string normalizedName = ValidName(name);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Length > MaximumPathLength) throw new FolderSourceException(FolderSourceError.FolderUnavailable);
        return Write(token, (connection, transaction) =>
        {
            List<FolderSourceRoot> roots = ReadRoots(connection, transaction);
            if (roots.Count >= MaxRoots) throw new FolderSourceException(FolderSourceError.TooManyRoots);
            if (roots.Any(root => FolderSourceValidation.Overlaps(root.Path, full))) throw new FolderSourceException(FolderSourceError.FolderOverlaps);
            FolderSourceRoot added = new(Guid.NewGuid(), full, normalizedName, nowUtc.ToUniversalTime(), includeSubfolders, null, null);
            using SqliteCommand insert = Command(connection, transaction,
                "INSERT INTO roots(id,path,name,created_utc,include_subfolders) VALUES($id,$path,$name,$created,$sub)");
            insert.Parameters.AddWithValue("$id", FolderSourceIdentity.Format(added.Id));
            insert.Parameters.AddWithValue("$path", added.Path);
            insert.Parameters.AddWithValue("$name", added.Name);
            insert.Parameters.AddWithValue("$created", added.CreatedUtc.UtcTicks);
            insert.Parameters.AddWithValue("$sub", includeSubfolders ? 1 : 0);
            insert.ExecuteNonQuery();
            return added;
        }, create: true, grows: true);
    }

    internal FolderSourceRoot RenameRoot(Guid root, string name, CancellationToken token = default)
    {
        string normalizedName = ValidName(name);
        return Write(token, (connection, transaction) =>
        {
            using SqliteCommand update = Command(connection, transaction, "UPDATE roots SET name = $name WHERE id = $id");
            update.Parameters.AddWithValue("$name", normalizedName);
            update.Parameters.AddWithValue("$id", FolderSourceIdentity.Format(root));
            if (update.ExecuteNonQuery() != 1) throw new FolderSourceException(FolderSourceError.UnknownRoot);
            return ReadRoots(connection, transaction).First(candidate => candidate.Id == root);
        });
    }

    /// <summary>
    /// Removes a folder source and its catalog rows only. The user's folder is not touched; tags, favorites and ratings that
    /// named its files stay stored and show up as orphans (as for any missing loose file). Allowed above the size limit, and the
    /// file gives the freed pages back afterwards (W3-03), so removing a source is always the way out of a full catalog.
    /// </summary>
    internal void RemoveRoot(Guid root, CancellationToken token = default) => Write<object?>(token, (connection, transaction) =>
    {
        string id = FolderSourceIdentity.Format(root);
        using (SqliteCommand items = Command(connection, transaction, "DELETE FROM items WHERE root_id = $id"))
        { items.Parameters.AddWithValue("$id", id); items.ExecuteNonQuery(); }
        using (SqliteCommand scans = Command(connection, transaction, "DELETE FROM scans WHERE root_id = $id"))
        { scans.Parameters.AddWithValue("$id", id); scans.ExecuteNonQuery(); }
        using SqliteCommand roots = Command(connection, transaction, "DELETE FROM roots WHERE id = $id");
        roots.Parameters.AddWithValue("$id", id);
        if (roots.ExecuteNonQuery() != 1) throw new FolderSourceException(FolderSourceError.UnknownRoot);
        return null;
    }, afterCommit: Shrink);

    // A SQLite file does not shrink on DELETE. A catalog made with auto_vacuum=INCREMENTAL frees its pages cheaply; an older one is
    // rewritten once (VACUUM) and then has that mode. The rows are already removed: a failure here only leaves the file larger.
    private static void Shrink(SqliteConnection connection)
    {
        try
        {
            using SqliteCommand mode = connection.CreateCommand();
            mode.CommandText = "PRAGMA auto_vacuum";
            bool incremental = Convert.ToInt32(mode.ExecuteScalar(), CultureInfo.InvariantCulture) == 2;
            using SqliteCommand shrink = connection.CreateCommand();
            shrink.CommandText = incremental ? "PRAGMA incremental_vacuum;" : "PRAGMA auto_vacuum=INCREMENTAL; VACUUM;";
            shrink.ExecuteNonQuery();
        }
        catch (SqliteException) { }
    }

    /// <summary>
    /// Applies one scan of a root. New files become <see cref="FolderSourceItemState.Seen"/>; an unhashed file whose length or
    /// write time changed is updated in place (no identity was bound to it yet); a hashed file whose length or write time
    /// changed is a new item and the old one becomes missing (its tags do not follow, see <see cref="FolderSourceScanResult.Replaced"/>);
    /// a file that is back with the length and write time of a missing row gets that row back. Files not found become missing
    /// only when the scan was complete, and never below a folder the scan could not read. New items stop at
    /// <see cref="MaxFiles"/> active items over all roots and at the fixed <see cref="MaximumItems"/> rows.
    /// </summary>
    internal FolderSourceScanResult ApplyScan(Guid rootId, FolderSourceScan scan, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        string root = FolderSourceIdentity.Format(rootId);
        // Plan in memory against the current rows (one read), then write in short transactions.
        Dictionary<string, FolderSourceItem> active = new(StringComparer.Ordinal);
        Dictionary<(string, long, long), FolderSourceItem> missing = new();
        int activeElsewhere, rows;
        (activeElsewhere, rows) = ReadFile(() =>
        {
            using SqliteConnection connection = Open(SqliteOpenMode.ReadOnly);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            Validate(connection, transaction);
            if (!ReadRoots(connection, transaction).Any(candidate => candidate.Id == rootId)) throw new FolderSourceException(FolderSourceError.UnknownRoot);
            using (SqliteCommand command = Command(connection, transaction,
                "SELECT id,root_id,rel_path,length,mtime_ticks,sha256,kind,state,first_seen_utc FROM items WHERE root_id = $root ORDER BY first_seen_utc"))
            {
                command.Parameters.AddWithValue("$root", root);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    FolderSourceItem item = ItemOf(reader);
                    if (item.State == FolderSourceItemState.Missing) missing[(item.RelativePath, item.Length, item.ModifiedTicks)] = item;
                    else active[item.RelativePath] = item;
                }
            }
            int elsewhere = Scalar(connection, transaction, "SELECT COUNT(*) FROM items WHERE state <> 2 AND root_id <> $root", root);
            int all = Scalar(connection, transaction, "SELECT COUNT(*) FROM items", null);
            transaction.Commit();
            return (elsewhere, all);
        });
        // Operations that change or drop rows run first, new rows last: a replaced file's old row is missing before its new row exists
        // (one active row per path), and a full catalog still records what is gone and what changed (W3-03).
        List<Action<SqliteConnection, SqliteTransaction>> operations = new(), inserts = new();
        int added = 0, changed = 0, replaced = 0, revived = 0, gone = 0, unchanged = 0;
        bool limitReached = scan.LimitReached;
        int budget = Math.Max(0, MaxFiles - activeElsewhere);
        int kept = 0;
        HashSet<string> seen = new(StringComparer.Ordinal);
        long now = nowUtc.UtcTicks;
        foreach (FolderSourceScannedFile file in scan.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(file.RelativePath)) continue;
            if (active.TryGetValue(file.RelativePath, out FolderSourceItem? current))
            {
                kept++;
                if (current.Length == file.Length && current.ModifiedTicks == file.ModifiedTicks) { unchanged++; continue; }
                if (current.State == FolderSourceItemState.Seen)
                {
                    // No identity bound yet: the row simply describes the file as it is now.
                    changed++;
                    Guid id = current.Id;
                    operations.Add((c, t) => UpdateMetadata(c, t, id, file));
                    continue;
                }
                // Hashed bytes changed: the old item stays as a missing target, the file is a new item.
                replaced++;
                Guid old = current.Id;
                operations.Add((c, t) => SetState(c, t, old, FolderSourceItemState.Missing));
                if (rows >= MaximumItems) { limitReached = true; continue; }
                rows++;
                inserts.Add((c, t) => Insert(c, t, Guid.NewGuid(), root, file, now));
                continue;
            }
            if (missing.TryGetValue((file.RelativePath, file.Length, file.ModifiedTicks), out FolderSourceItem? back))
            {
                if (kept >= budget) { limitReached = true; continue; }
                kept++;
                revived++;
                Guid id = back.Id;
                FolderSourceItemState state = back.Sha256 == null ? FolderSourceItemState.Seen : FolderSourceItemState.Hashed;
                operations.Add((c, t) => SetState(c, t, id, state));
                continue;
            }
            if (kept >= budget || rows >= MaximumItems) { limitReached = true; continue; }
            kept++;
            rows++;
            added++;
            inserts.Add((c, t) => Insert(c, t, Guid.NewGuid(), root, file, now));
        }
        if (scan.Complete)
            foreach (FolderSourceItem item in active.Values)
            {
                if (seen.Contains(item.RelativePath) || scan.IsBelowUnreadable(item.RelativePath)) continue;
                gone++;
                Guid id = item.Id;
                operations.Add((c, t) => SetState(c, t, id, FolderSourceItemState.Missing));
            }
        WriteBatches(operations, grows: false, token);
        // New rows stop at the size limit: what was written stays, the scan is recorded, and the caller learns the catalog is full.
        bool full = false;
        try { WriteBatches(inserts, grows: true, token); }
        catch (FolderSourceException error) when (error.Error == FolderSourceError.CatalogFull) { full = true; limitReached = true; }
        FolderSourceScanStatus status = !scan.RootAvailable ? FolderSourceScanStatus.RootUnavailable
            : limitReached ? FolderSourceScanStatus.LimitReached
            : !scan.Complete ? FolderSourceScanStatus.Incomplete : FolderSourceScanStatus.Complete;
        FolderSourceScanResult result = new(status, scan.Files.Count, added, changed, replaced, revived, gone, unchanged, scan.UnreadableFolders, scan.SkippedLinks);
        Write<object?>(token, (connection, transaction) =>
        {
            using (SqliteCommand update = Command(connection, transaction, "UPDATE roots SET last_scan_utc = $at, last_scan_status = $status WHERE id = $id"))
            {
                update.Parameters.AddWithValue("$at", now);
                update.Parameters.AddWithValue("$status", (int)status);
                update.Parameters.AddWithValue("$id", root);
                update.ExecuteNonQuery();
            }
            using SqliteCommand insert = Command(connection, transaction,
                "INSERT INTO scans(root_id,finished_utc,status,files,added,changed,replaced,revived,missing) VALUES($id,$at,$status,$files,$added,$changed,$replaced,$revived,$missing)");
            insert.Parameters.AddWithValue("$id", root);
            insert.Parameters.AddWithValue("$at", now);
            insert.Parameters.AddWithValue("$status", (int)status);
            insert.Parameters.AddWithValue("$files", result.Files);
            insert.Parameters.AddWithValue("$added", added);
            insert.Parameters.AddWithValue("$changed", changed);
            insert.Parameters.AddWithValue("$replaced", replaced);
            insert.Parameters.AddWithValue("$revived", revived);
            insert.Parameters.AddWithValue("$missing", gone);
            insert.ExecuteNonQuery();
            // Only the last runs per root are kept: the history is for the status line, not an audit log.
            using SqliteCommand trim = Command(connection, transaction,
                "DELETE FROM scans WHERE root_id = $id AND id NOT IN (SELECT id FROM scans WHERE root_id = $id ORDER BY id DESC LIMIT 16)");
            trim.Parameters.AddWithValue("$id", root);
            trim.ExecuteNonQuery();
            return null;
        });
        if (full) throw new FolderSourceException(FolderSourceError.CatalogFull);
        return result;
    }

    private void WriteBatches(List<Action<SqliteConnection, SqliteTransaction>> operations, bool grows, CancellationToken token)
    {
        for (int offset = 0; offset < operations.Count; offset += WriteBatchSize)
        {
            token.ThrowIfCancellationRequested();
            List<Action<SqliteConnection, SqliteTransaction>> batch = operations.GetRange(offset, Math.Min(WriteBatchSize, operations.Count - offset));
            Write<object?>(token, (connection, transaction) =>
            {
                foreach (Action<SqliteConnection, SqliteTransaction> operation in batch) operation(connection, transaction);
                return null;
            }, grows: grows);
        }
    }

    /// <summary>
    /// Records the SHA-256 of an item, only while its row still describes exactly the bytes that were hashed (same length
    /// and write time, still active). An item already hashed keeps its digest. False when nothing was recorded.
    /// </summary>
    internal bool RecordHash(Guid item, long length, long modifiedTicks, string sha256, DateTimeOffset nowUtc, CancellationToken token = default) =>
        RecordHashes(new[] { new FolderSourceDigest(item, length, modifiedTicks, sha256) }, nowUtc, token)[0];

    /// <summary>
    /// <see cref="RecordHash"/> for many items in one transaction (W3-06: the hasher commits its digests in batches). Allowed above
    /// the size limit: a digest fills a column of an existing row. The kept view is updated in place instead of being dropped, so
    /// lookups and the media index keep reading memory while files are hashed. One result per digest, in order.
    /// </summary>
    internal IReadOnlyList<bool> RecordHashes(IReadOnlyList<FolderSourceDigest> digests, DateTimeOffset nowUtc, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(digests);
        string[] normalized = digests.Select(digest => digest.Sha256 is { Length: 64 } sha && sha.All(Uri.IsHexDigit)
            ? sha.ToLowerInvariant() : throw new ArgumentException("A SHA-256 is required.", nameof(digests))).ToArray();
        if (digests.Count == 0) return Array.Empty<bool>();
        return Write(token, (connection, transaction) =>
        {
            bool[] recorded = new bool[digests.Count];
            using SqliteCommand update = Command(connection, transaction,
                "UPDATE items SET sha256 = $sha, state = 1, hashed_utc = $at WHERE id = $id AND state = 0 AND length = $len AND mtime_ticks = $mtime");
            SqliteParameter sha = update.Parameters.Add("$sha", SqliteType.Text), id = update.Parameters.Add("$id", SqliteType.Text);
            SqliteParameter length = update.Parameters.Add("$len", SqliteType.Integer), modified = update.Parameters.Add("$mtime", SqliteType.Integer);
            update.Parameters.AddWithValue("$at", nowUtc.UtcTicks);
            for (int index = 0; index < digests.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                (sha.Value, id.Value, length.Value, modified.Value) = (normalized[index], FolderSourceIdentity.Format(digests[index].Item),
                    digests[index].Length, digests[index].ModifiedTicks);
                recorded[index] = update.ExecuteNonQuery() == 1;
            }
            return (IReadOnlyList<bool>)recorded;
        }, patch: (recorded, kept, revision) => kept.WithHashed(revision,
            Enumerable.Range(0, digests.Count).Where(index => recorded[index]).Select(index => (digests[index].Item, normalized[index])).ToArray()));
    }

    // ---- Plumbing ----

    // Reads hold this catalog's own lock only, never the lease monitor shared by every store (W3-06).
    private T ReadFile<T>(Func<T> read)
    {
        ObjectDisposedException.ThrowIf(!_lease.IsHeld, _lease);
        lock (_io)
        {
            ObjectDisposedException.ThrowIf(!_lease.IsHeld, _lease);
            BeforeFileAccessForTests?.Invoke();
            return read();
        }
    }

    /// <summary>
    /// One write transaction. Only a growing write (<paramref name="grows"/>: a new root, new rows) is refused above the size limit
    /// (W3-03); removing, renaming, state changes and digests always run, so a full catalog can always be shrunk. After the commit
    /// <paramref name="patch"/> may carry the kept view to the new revision; without it the view is dropped.
    /// </summary>
    private T Write<T>(CancellationToken token, Func<SqliteConnection, SqliteTransaction, T> operation, bool create = false, bool grows = false,
        Func<T, FolderSourceCatalogSnapshot, long, FolderSourceCatalogSnapshot>? patch = null, Action<SqliteConnection>? afterCommit = null)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        lock (_io)
            return _lease.RunStoreWrite(admission, () =>
            {
                token.ThrowIfCancellationRequested();
                BeforeFileAccessForTests?.Invoke();
                bool exists = File.Exists(_path);
                if (!exists && !create) throw new FolderSourceException(FolderSourceError.UnknownRoot);
                if (grows && exists && new FileInfo(_path).Length > LimitBytes) throw new FolderSourceException(FolderSourceError.CatalogFull);
                using SqliteConnection connection = Open(SqliteOpenMode.ReadWriteCreate);
                if (!exists) CreateSchema(connection);
                T result;
                long revision;
                using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
                {
                    Validate(connection, transaction);
                    result = operation(connection, transaction);
                    using (SqliteCommand bump = Command(connection, transaction, "UPDATE meta SET value = CAST(value AS INTEGER) + 1 WHERE key = 'revision'"))
                        bump.ExecuteNonQuery();
                    revision = Revision(connection, transaction);
                    transaction.Commit();
                }
                lock (_sync)
                    _cached = patch != null && _cached is { Revision: > 0 } kept && kept.Revision == revision - 1 ? patch(result, kept, revision) : null;
                afterCommit?.Invoke(connection);
                return result;
            });
    }

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _path, Mode = mode, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 5, ForeignKeys = true
        }.ToString());
        try
        {
            connection.Open();
            using SqliteCommand pragma = connection.CreateCommand();
            pragma.CommandText = mode == SqliteOpenMode.ReadOnly ? "PRAGMA busy_timeout=5000;" : "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch (SqliteException) { connection.Dispose(); throw new FolderSourceException(FolderSourceError.CatalogDamaged); }
        catch { connection.Dispose(); throw; }
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA auto_vacuum=INCREMENTAL;
            PRAGMA journal_mode=DELETE;
            PRAGMA application_id={ApplicationId};
            PRAGMA user_version={SchemaVersion};
            CREATE TABLE meta(key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL) STRICT;
            INSERT INTO meta(key,value) VALUES('revision','0');
            CREATE TABLE roots(id TEXT PRIMARY KEY NOT NULL, path TEXT NOT NULL UNIQUE, name TEXT NOT NULL, created_utc INTEGER NOT NULL,
              include_subfolders INTEGER NOT NULL, last_scan_utc INTEGER, last_scan_status INTEGER) STRICT;
            CREATE TABLE items(id TEXT PRIMARY KEY NOT NULL, root_id TEXT NOT NULL REFERENCES roots(id), rel_path TEXT NOT NULL,
              length INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL, sha256 TEXT, kind INTEGER NOT NULL, state INTEGER NOT NULL,
              first_seen_utc INTEGER NOT NULL, hashed_utc INTEGER) STRICT;
            CREATE UNIQUE INDEX items_active ON items(root_id, rel_path) WHERE state <> 2;
            CREATE INDEX items_root ON items(root_id, rel_path);
            CREATE INDEX items_unhashed ON items(state) WHERE state = 0;
            CREATE TABLE scans(id INTEGER PRIMARY KEY AUTOINCREMENT, root_id TEXT NOT NULL, finished_utc INTEGER NOT NULL, status INTEGER NOT NULL,
              files INTEGER NOT NULL, added INTEGER NOT NULL, changed INTEGER NOT NULL, replaced INTEGER NOT NULL, revived INTEGER NOT NULL,
              missing INTEGER NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    private static void Validate(SqliteConnection connection, SqliteTransaction transaction)
    {
        try
        {
            int application = Scalar(connection, transaction, "PRAGMA application_id", null);
            int version = Scalar(connection, transaction, "PRAGMA user_version", null);
            if (application != ApplicationId) throw new FolderSourceException(FolderSourceError.CatalogDamaged);
            if (version > SchemaVersion) throw new FolderSourceException(FolderSourceError.CatalogNewer);
            if (version != SchemaVersion) throw new FolderSourceException(FolderSourceError.CatalogDamaged);
            using SqliteCommand columns = Command(connection, transaction, """
                SELECT key,value FROM meta LIMIT 0;
                SELECT id,path,name,created_utc,include_subfolders,last_scan_utc,last_scan_status FROM roots LIMIT 0;
                SELECT id,root_id,rel_path,length,mtime_ticks,sha256,kind,state,first_seen_utc,hashed_utc FROM items LIMIT 0;
                SELECT id,root_id,finished_utc,status,files,added,changed,replaced,revived,missing FROM scans LIMIT 0;
                """);
            using SqliteDataReader reader = columns.ExecuteReader();
            while (reader.NextResult()) { }
        }
        catch (SqliteException) { throw new FolderSourceException(FolderSourceError.CatalogDamaged); }
    }

    private static long Revision(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = Command(connection, transaction, "SELECT value FROM meta WHERE key = 'revision'");
        return long.TryParse(Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            ? value : throw new FolderSourceException(FolderSourceError.CatalogDamaged);
    }

    private static List<FolderSourceRoot> ReadRoots(SqliteConnection connection, SqliteTransaction transaction)
    {
        List<FolderSourceRoot> roots = new();
        using SqliteCommand command = Command(connection, transaction,
            "SELECT id,path,name,created_utc,include_subfolders,last_scan_utc,last_scan_status FROM roots ORDER BY created_utc, id");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string path = reader.GetString(1), name = reader.GetString(2);
            FolderSourceScanStatus? status = reader.IsDBNull(6) ? null : (FolderSourceScanStatus)reader.GetInt32(6);
            if (path.Length is 0 or > MaximumPathLength || !Path.IsPathFullyQualified(path) || name.Length is 0 or > MaximumNameLength
                || status is { } known && !Enum.IsDefined(known)) throw new FolderSourceException(FolderSourceError.CatalogDamaged);
            roots.Add(new(ParseId(reader.GetString(0)), path, name, Utc(reader.GetInt64(3)), reader.GetInt64(4) != 0,
                reader.IsDBNull(5) ? null : Utc(reader.GetInt64(5)), status));
        }
        return roots;
    }

    private static FolderSourceItem? FindItem(SqliteConnection connection, SqliteTransaction transaction, Guid item)
    {
        using SqliteCommand command = Command(connection, transaction,
            "SELECT id,root_id,rel_path,length,mtime_ticks,sha256,kind,state,first_seen_utc FROM items WHERE id = $id");
        command.Parameters.AddWithValue("$id", FolderSourceIdentity.Format(item));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ItemOf(reader) : null;
    }

    private static FolderSourceItem ItemOf(SqliteDataReader reader)
    {
        string relative = reader.GetString(2);
        string? sha = reader.IsDBNull(5) ? null : reader.GetString(5);
        SavedMediaKind kind = (SavedMediaKind)reader.GetInt32(6);
        FolderSourceItemState state = (FolderSourceItemState)reader.GetInt32(7);
        long length = reader.GetInt64(3);
        if (!FolderSourceIdentity.TryNormalizeRelative(relative, out string normalized) || normalized != relative || length < 0
            || !Enum.IsDefined(kind) || !Enum.IsDefined(state)
            || sha != null && (sha.Length != 64 || !sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            || state == FolderSourceItemState.Hashed && sha == null || state == FolderSourceItemState.Seen && sha != null)
            throw new FolderSourceException(FolderSourceError.CatalogDamaged);
        return new FolderSourceItem(ParseId(reader.GetString(0)), ParseId(reader.GetString(1)), relative, length, reader.GetInt64(4),
            sha, kind, state, Utc(reader.GetInt64(8)));
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, Guid id, string root, FolderSourceScannedFile file, long now)
    {
        using SqliteCommand insert = Command(connection, transaction,
            "INSERT INTO items(id,root_id,rel_path,length,mtime_ticks,kind,state,first_seen_utc) VALUES($id,$root,$path,$len,$mtime,$kind,0,$now)");
        insert.Parameters.AddWithValue("$id", FolderSourceIdentity.Format(id));
        insert.Parameters.AddWithValue("$root", root);
        insert.Parameters.AddWithValue("$path", file.RelativePath);
        insert.Parameters.AddWithValue("$len", file.Length);
        insert.Parameters.AddWithValue("$mtime", file.ModifiedTicks);
        insert.Parameters.AddWithValue("$kind", (int)file.Kind);
        insert.Parameters.AddWithValue("$now", now);
        insert.ExecuteNonQuery();
    }

    private static void UpdateMetadata(SqliteConnection connection, SqliteTransaction transaction, Guid id, FolderSourceScannedFile file)
    {
        using SqliteCommand update = Command(connection, transaction,
            "UPDATE items SET length = $len, mtime_ticks = $mtime, kind = $kind WHERE id = $id AND state = 0");
        update.Parameters.AddWithValue("$len", file.Length);
        update.Parameters.AddWithValue("$mtime", file.ModifiedTicks);
        update.Parameters.AddWithValue("$kind", (int)file.Kind);
        update.Parameters.AddWithValue("$id", FolderSourceIdentity.Format(id));
        update.ExecuteNonQuery();
    }

    private static void SetState(SqliteConnection connection, SqliteTransaction transaction, Guid id, FolderSourceItemState state)
    {
        using SqliteCommand update = Command(connection, transaction, "UPDATE items SET state = $state WHERE id = $id");
        update.Parameters.AddWithValue("$state", (int)state);
        update.Parameters.AddWithValue("$id", FolderSourceIdentity.Format(id));
        update.ExecuteNonQuery();
    }

    private static int Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql, string? root)
    {
        using SqliteCommand command = Command(connection, transaction, sql);
        if (root != null) command.Parameters.AddWithValue("$root", root);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static Guid ParseId(string text) =>
        FolderSourceIdentity.TryParseId(text, out Guid id) ? id : throw new FolderSourceException(FolderSourceError.CatalogDamaged);

    private static DateTimeOffset Utc(long ticks) =>
        ticks is >= 0 and <= 3155378975999999999 ? new DateTimeOffset(ticks, TimeSpan.Zero) : throw new FolderSourceException(FolderSourceError.CatalogDamaged);

    private static string ValidName(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > MaximumNameLength || trimmed.Any(char.IsControl)) throw new FolderSourceException(FolderSourceError.InvalidName);
        return trimmed;
    }
}
