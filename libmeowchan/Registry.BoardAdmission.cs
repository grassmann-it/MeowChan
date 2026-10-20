using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Admission;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan;

public sealed partial class Registry
{
    private BoardAdmissionStore? _boardAdmission;
    public void ConfigureBoardAdmission(BoardAdmissionStore store)
    {
        if (_restored) throw new InvalidOperationException("Configure admission before restoring watches.");
        _boardAdmission = store;
        if (store.LoadWarning != null) Warn(store.LoadWarning);
    }

    public BoardAdmissionPolicy GetBoardAdmissionPolicy(string url) => _boardAdmission?.Get(url) ?? BoardAdmissionPolicy.All;

    public BoardAdmissionTarget? ClassifyAdmission(string url)
    {
        if (Resolve(url, out Resource? resource) != AdmissionStatus.Added || resource == null) return null;
        lock (_state) return new(resource.Url, resource.Board != null, _watches.ContainsKey(resource.Key));
    }

    /// <summary>Only catalog HTTP; never starts a watch, persists intent, or fetches thread/media endpoints.</summary>
    public async Task<BoardAdmissionPreview> PreviewBoardAdmissionAsync(string url, CancellationToken cancellationToken = default)
    {
        if (Resolve(url, out Resource? resource) != AdmissionStatus.Added || resource?.Board == null)
            throw new ArgumentException("A supported board URL is required.", nameof(url));
        IBoard board = resource.Board;
        IThread[] entries = (await board.GetThreadsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var threads = entries.Select(thread => new BoardCatalogThread(thread.ThreadId, thread.ThreadUri.AbsoluteUri,
                thread.Title, thread.CatalogOpeningText, _suppressed.Contains(Key(thread))
                    || _workResources.TryGetValue(Key(thread), out WorkResource? work) && work.Intent == WorkIntent.Suppressed,
                _watches.ContainsKey(Key(thread)), _watches.ContainsKey(Key(thread)) && !_session.DiscoveredThreadUrls.Contains(thread.ThreadUri.AbsoluteUri, StringComparer.Ordinal))).ToArray();
            return new(resource.Url, board.NiceName, _boardAdmission?.Get(resource.Url) ?? BoardAdmissionPolicy.All, Array.AsReadOnly(threads));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Publishes a durable selection before starting monitoring. Explicit includes reactivate suppressed threads.</summary>
    public async Task<AdmissionResult> ApplyBoardAdmissionAsync(string url, BoardAdmissionPolicy policy, IReadOnlyCollection<string>? reactivateThreadUrls = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            EnsureRestoredForPersistence();
            if (Resolve(url, out Resource? resource) != AdmissionStatus.Added || resource?.Board == null)
                throw new ArgumentException("A supported board URL is required.", nameof(url));
            foreach (string exception in policy.Exceptions.Keys)
            {
                if (Resolve(exception, out Resource? candidate) != AdmissionStatus.Added || candidate?.Thread == null
                    || candidate.Url != exception || Key(candidate.Thread.Board) != resource.Key) throw new ArgumentException("Thread choices must belong to this board.");
            }
            if (_boardAdmission == null) throw new InvalidOperationException("Durable board admission storage is not configured.");
            _boardAdmission.Save(resource.Url, policy);

        }
        finally { _gate.Release(); }
        foreach (var choice in policy.Exceptions.Where(pair => pair.Value == ThreadAdmissionException.Include
            && reactivateThreadUrls?.Contains(pair.Key, StringComparer.Ordinal) == true))
            await AddAsync(choice.Key).ConfigureAwait(false);
        SignalChanged();
        // Use normal admission so a previous stopping board lifetime is drained before replacement.
        return await AddAsync(url).ConfigureAwait(false);
    }

    private bool AllowsBoardDiscovery(IBoard board, IThread thread)
    {
        BoardAdmissionPolicy policy = _boardAdmission?.Get(board.BoardUri.AbsoluteUri) ?? BoardAdmissionPolicy.All;
        return policy.Evaluate(new(thread.ThreadId, thread.ThreadUri.AbsoluteUri, thread.Title, thread.CatalogOpeningText)).Included;
    }
}
