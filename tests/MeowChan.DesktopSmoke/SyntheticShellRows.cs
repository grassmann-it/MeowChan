using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// TEST ONLY. Gives old passive-row scenes a real immutable input to the shell's source/search pump.
/// Quiesces capture admission on this window's host, retains its real engine/command handles, and
/// merges synthetic snapshots with the actual existing rows. No provider watches or network work.
/// Candidate-only implementation of the common harness facade. Product access is test-owned
/// reflection at existing boundaries; this file is not imported into release-HEAD measurements.
/// </summary>
internal sealed class SyntheticShellRows : ISyntheticShellRows
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private readonly MainWindow _window;
    private readonly object _host;
    private readonly DesktopThreadEntry[] _threadEntries;
    private readonly DesktopBoardEntry[] _boardEntries;
    private DesktopThreadEntry[] _baseThreads = [];
    private DesktopBoardEntry[] _baseBoards = [];
    private ThreadRow[] _baseThreadRows = [];
    private BoardRow[] _baseBoardRows = [];
    private ThreadRow[] _threads = [];
    private BoardRow[] _boards = [];
    private object? _original;
    private bool _captureTaken, _disposed, _uiSaved;
    private string? _searchText;
    private object? _searchOpen, _threadLead, _boardLead;
    private int _tab;
    private ThreadRow[] _threadSelection = [];
    private BoardRow[] _boardSelection = [];
    private Action? _restoreSort;

    private SyntheticShellRows(MainWindow window, DesktopThreadEntry[] threads, DesktopBoardEntry[] boards)
    {
        Dispatcher.UIThread.VerifyAccess();
        _window = window;
        _host = Get(window, "_session")!;
        _threadEntries = threads;
        _boardEntries = boards;
    }

    internal ThreadRow[] Threads => _threads;
    internal BoardRow[] Boards => _boards;
    IReadOnlyList<ThreadRow> ISyntheticShellRows.Threads => _threads;
    IReadOnlyList<BoardRow> ISyntheticShellRows.Boards => _boards;
    void ISyntheticShellRows.Update(DesktopThreadEntry entry) => Update(entry);
    Task ISyntheticShellRows.UpdateAsync(DesktopThreadEntry entry) => UpdateAsync(entry);
    Task ISyntheticShellRows.UpdateAsync(DesktopBoardEntry entry) => UpdateAsync(entry);
    Task ISyntheticShellRows.WaitForAppliedAsync() => WaitForAppliedAsync();

    internal static async Task<SyntheticShellRows> CreateAsync(MainWindow window,
        IEnumerable<DesktopThreadEntry>? threads = null, IEnumerable<DesktopBoardEntry>? boards = null)
    {
        SyntheticShellRows fixture = new(window, threads?.ToArray() ?? [], boards?.ToArray() ?? []);
        try { await fixture.InitializeAsync(); return fixture; }
        catch (Exception failure)
        {
            try { await fixture.DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException("Synthetic shell setup and restoration failed.", failure, cleanup); }
            throw;
        }
    }

    internal static Task<SyntheticShellRows> FromRowsAsync(MainWindow window,
        IEnumerable<ThreadRow>? threads = null, IEnumerable<BoardRow>? boards = null) =>
        CreateAsync(window, threads?.Select(Snapshot), boards?.Select(Snapshot));

    private async Task InitializeAsync()
    {
        if (Field(_host, "_published") == null || Field(_host, "_captureAdmissionClosed") == null
            || Field(_window, "_sessionRowsApplying") == null)
            throw new InvalidOperationException("SyntheticShellRows requires the candidate's projection/source/search boundaries.");
        if ((bool)Get(_host, "_captureAdmissionClosed")!)
            throw new InvalidOperationException("SyntheticShellRows cannot nest or reopen a closing host.");
        _captureTaken = true;
        await ((Task)Call(_host, "CloseProjectionAsync")!).WaitAsync(Deadline);
        await WaitForAppliedAsync();
        _original = Projection();
        SaveUi();
        _baseThreadRows = _window.UrlList.ToArray(); _baseBoardRows = _window.BoardsList.ToArray();
        _baseThreads = _baseThreadRows.Select(Snapshot).ToArray();
        _baseBoards = _baseBoardRows.Select(Snapshot).ToArray();
        if (_baseThreads.Concat(_threadEntries).Select(entry => entry.WatchId).Distinct().Count() != _baseThreads.Length + _threadEntries.Length
            || _baseBoards.Concat(_boardEntries).Select(entry => entry.WatchId).Distinct().Count() != _baseBoards.Length + _boardEntries.Length)
            throw new InvalidOperationException("Synthetic watch identities collide with this isolated session.");
        Publish(_baseThreads.Concat(_threadEntries).ToArray(), _baseBoards.Concat(_boardEntries).ToArray());
        await WaitForAppliedAsync();
        // Return the actual RowPlan-owned instances, never the caller's disposable templates.
        _threads = _threadEntries.Select(entry => _window.UrlList.Single(row => row.WatchId == entry.WatchId)).ToArray();
        _boards = _boardEntries.Select(entry => _window.BoardsList.Single(row => row.WatchId == entry.WatchId)).ToArray();
        AssertIdentitiesAndSnapshots();
    }

    /// <summary>Publish a new immutable frame, retaining only the newest pending frame in the REAL shell pump.</summary>
    internal void Update(DesktopThreadEntry entry)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) throw new ObjectDisposedException(nameof(SyntheticShellRows));
        int index = Array.FindIndex(_threadEntries, prior => prior.WatchId == entry.WatchId);
        if (index < 0) throw new ArgumentException("Update belongs to another synthetic watch.", nameof(entry));
        _threadEntries[index] = entry;
        Publish(_baseThreads.Concat(_threadEntries).ToArray(), _baseBoards.Concat(_boardEntries).ToArray());
    }
    internal void Update(DesktopBoardEntry entry)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) throw new ObjectDisposedException(nameof(SyntheticShellRows));
        int index = Array.FindIndex(_boardEntries, prior => prior.WatchId == entry.WatchId);
        if (index < 0) throw new ArgumentException("Update belongs to another synthetic watch.", nameof(entry));
        _boardEntries[index] = entry;
        Publish(_baseThreads.Concat(_threadEntries).ToArray(), _baseBoards.Concat(_boardEntries).ToArray());
    }
    internal async Task UpdateAsync(DesktopThreadEntry entry) { Update(entry); await WaitForAppliedAsync(); AssertIdentitiesAndSnapshots(); }
    internal async Task UpdateAsync(DesktopBoardEntry entry) { Update(entry); await WaitForAppliedAsync(); AssertIdentitiesAndSnapshots(); }

    /// <summary>Wait for source, search AND selection repair, including the latest query revision.</summary>
    internal async Task WaitForAppliedAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        Call(_window, "RefreshSession");
        await Program.WaitUntilAsync(() =>
        {
            if ((bool)Get(_window, "_sessionRowsApplying")! || (bool)Get(_window, "_rowSlicePosted")!) return false;
            object? applied = Get(_window, "_appliedSessionProjection");
            object? current = Projection();
            return applied != null && current != null
                && Equals(Property(applied, "Epoch"), Property(current, "Epoch"))
                && Equals(Property(applied, "Version"), Property(current, "Version"))
                && Equals(Get(_window, "_downloadSearchQueryRevision"), Get(_window, "_appliedDownloadSearchRevision"));
        });
    }

    private void Publish(DesktopThreadEntry[] threads, DesktopBoardEntry[] boards, object? template = null)
    {
        object projection = Clone(template ?? _original!);
        object view = Clone(Property(projection, "View")!);
        SetProperty(view, "Threads", Array.AsReadOnly(threads));
        SetProperty(view, "Boards", Array.AsReadOnly(boards));
        SetProperty(projection, "View", view);
        SetProperty(projection, "CapturedAtTimestamp", Stopwatch.GetTimestamp());
        // Keep the host's own version allocator monotonic across fixture frames AND the first restored capture.
        lock (Get(_host, "_lifecycle")!)
        {
            long version = Math.Max((long)Get(_host, "_projectionVersion")!, (long)Property(Projection()!, "Version")!) + 1;
            Set(_host, "_projectionVersion", version);
            SetProperty(projection, "Version", version);
            Thread.MemoryBarrier();
            Set(_host, "_published", projection);
        }
        Call(_window, "RefreshSession");
    }

    private void AssertIdentitiesAndSnapshots()
    {
        foreach (var (entry, row) in _threadEntries.Zip(_threads))
            if (!ReferenceEquals(_window.UrlList.Single(item => item.WatchId == entry.WatchId), row)
                || !ReferenceEquals(row.Snapshot, entry.Snapshot))
                throw new InvalidOperationException("The real thread plan replaced an identity or missed the latest immutable snapshot.");
        foreach (var (entry, row) in _boardEntries.Zip(_boards))
            if (!ReferenceEquals(_window.BoardsList.Single(item => item.WatchId == entry.WatchId), row)
                || !ReferenceEquals(row.Snapshot, entry.Snapshot))
                throw new InvalidOperationException("The real board plan replaced an identity or missed the latest immutable snapshot.");
    }

    private void SaveUi()
    {
        DataGrid threads = Grid(false), boards = Grid(true);
        _threadSelection = threads.SelectedItems.OfType<ThreadRow>().ToArray();
        _boardSelection = boards.SelectedItems.OfType<BoardRow>().ToArray();
        var threadSort = threads.CollectionView.SortDescriptions.ToArray();
        var boardSort = boards.CollectionView.SortDescriptions.ToArray();
        _restoreSort = () =>
        {
            threads.CollectionView.SortDescriptions.Clear(); boards.CollectionView.SortDescriptions.Clear();
            foreach (var sort in threadSort) threads.CollectionView.SortDescriptions.Add(sort);
            foreach (var sort in boardSort) boards.CollectionView.SortDescriptions.Add(sort);
        };
        _searchText = _window.FindControl<TextBox>("DownloadSearchInput")!.Text;
        _searchOpen = Get(_window, "_downloadSearchOpen");
        _threadLead = Get(_window, "_threadLeadId"); _boardLead = Get(_window, "_boardLeadId");
        _tab = _window.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex;
        _uiSaved = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        Dispatcher.UIThread.VerifyAccess();
        _disposed = true;
        try
        {
            if (_uiSaved)
            {
                _window.FindControl<TextBox>("DownloadSearchInput")!.Text = _searchText;
                Set(_window, "_downloadSearchOpen", _searchOpen);
                Publish(_baseThreads, _baseBoards, _original);
                await WaitForAppliedAsync();
                _restoreSort?.Invoke();
                RestoreSelection();
                _window.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex = _tab;
                Call(_window, "RefreshWorkspace");
            }
        }
        finally
        {
            if (_captureTaken)
            {
                // Reopen even if a failed GUI assertion prevented a normal pump drain. A fresh capture
                // restores engine authority instead of leaving a synthetic published frame behind.
                lock (Get(_host, "_captureRequests")!) Set(_host, "_captureAdmissionClosed", false);
                _captureTaken = false;
                await ((Task)Call(_host, "CaptureProjectionAsync")!).WaitAsync(Deadline);
                await WaitForAppliedAsync();
                if (_uiSaved) RestoreSelection();
            }
        }
        if (_uiSaved)
        {
            if (!_baseThreadRows.SequenceEqual(_window.UrlList, ReferenceEqualityComparer.Instance)
                || !_baseBoardRows.SequenceEqual(_window.BoardsList, ReferenceEqualityComparer.Instance)
                || _window.FindControl<TextBox>("DownloadSearchInput")!.Text != _searchText
                || !Equals(Get(_window, "_downloadSearchOpen"), _searchOpen))
                throw new InvalidOperationException("SyntheticShellRows did not restore original row identities/order or search state.");
            if (!Grid(false).SelectedItems.OfType<ThreadRow>().ToHashSet(ReferenceEqualityComparer.Instance)
                    .SetEquals(_threadSelection)
                || !Grid(true).SelectedItems.OfType<BoardRow>().ToHashSet(ReferenceEqualityComparer.Instance)
                    .SetEquals(_boardSelection))
                throw new InvalidOperationException("SyntheticShellRows did not restore original grid selections.");
        }
    }

    private void RestoreSelection()
    {
        Grid(false).SelectedItems.Clear(); Grid(true).SelectedItems.Clear();
        foreach (ThreadRow row in _threadSelection)
            if (_window.VisibleThreads.Contains(row)) Grid(false).SelectedItems.Add(row);
        foreach (BoardRow row in _boardSelection)
            if (_window.VisibleBoards.Contains(row)) Grid(true).SelectedItems.Add(row);
        Set(_window, "_threadLeadId", _threadLead); Set(_window, "_boardLeadId", _boardLead);
        if (_threads.Any(row => _window.UrlList.Contains(row)) || _boards.Any(row => _window.BoardsList.Contains(row)))
            throw new InvalidOperationException("Synthetic rows survived fixture restoration.");
    }
    private DataGrid Grid(bool boards) => _window.FindControl<DataGrid>(boards ? "BoardsDataGrid" : "ThreadsDataGrid")!;
    private object? Projection() => _host.GetType().GetProperty("Projection", Instance)?.GetValue(_host);
    private static DesktopThreadEntry Snapshot(ThreadRow row) => new(row.WatchId, row.Snapshot, row.HostName, row.BoardName);
    private static DesktopBoardEntry Snapshot(BoardRow row) => new(row.WatchId, row.Snapshot, row.HostName, row.NiceName);
    private static FieldInfo? Field(object target, string name) => target.GetType().GetField(name, Instance);
    private static object? Get(object target, string name) => (Field(target, name)
        ?? throw new MissingFieldException(target.GetType().FullName, name)).GetValue(target);
    private static void Set(object target, string name, object? value) => (Field(target, name)
        ?? throw new MissingFieldException(target.GetType().FullName, name)).SetValue(target, value);
    private static object? Property(object target, string name) => (target.GetType().GetProperty(name, Instance)
        ?? throw new MissingMemberException(target.GetType().FullName, name)).GetValue(target);
    private static void SetProperty(object target, string name, object? value) => (target.GetType().GetProperty(name, Instance)
        ?? throw new MissingMemberException(target.GetType().FullName, name)).SetValue(target, value);
    private static object Clone(object target) => typeof(object).GetMethod("MemberwiseClone", Instance)!.Invoke(target, null)!;
    private static object? Call(object target, string name) => (target.GetType().GetMethod(name, Instance)
        ?? throw new MissingMethodException(target.GetType().FullName, name)).Invoke(target, null);
}
