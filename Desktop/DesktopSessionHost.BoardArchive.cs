using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Boards.Base;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop;

internal sealed record BoardArchiveLocation(string Board, Uri Uri)
{
    public override string ToString() => "/" + Board + "/";
}

internal sealed class BoardArchivePausedException : InvalidOperationException { }

internal sealed partial class DesktopSessionHost
{
    internal BoardArchiveLocation? FindBoardArchive(string url, bool fromBoard = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || _registry.FindAdmissionProvider(uri) is not PluginSourceImageBoard source
            || !source.Router.TryResolve(uri, out SourceResourceKind kind, out string board, out _, out Uri? canonical))
            return null;
        if (kind == SourceResourceKind.BoardArchive) return new(board, canonical!);
        if (!fromBoard || kind != SourceResourceKind.Board
            || !source.Description.Routes.Any(route => route is { Kind: SourceResourceKind.BoardArchive, IsCanonical: true }))
            return null;
        try { return new(board, source.Router.Canonical(SourceResourceKind.BoardArchive, board)); }
        catch (ArgumentException) { return null; }
    }

    internal Task<IReadOnlyList<BoardArchiveThread>?> ReadBoardArchiveAsync(BoardArchiveLocation board, CancellationToken token)
        => RunInteractiveAsync(nameof(ReadBoardArchiveAsync), board.Uri.AbsoluteUri, () =>
        {
            EnsureBoardArchiveReady();
            return ReadArchiveWhenAdmittedAsync(() => ArchiveSource(board.Uri).ReadBoardArchiveAsync(board.Uri, token), token);
        }, captureAfter: false);

    internal Task<BoardArchiveThreadDetails?> ReadBoardArchiveThreadAsync(BoardArchiveThread thread, CancellationToken token)
        => RunInteractiveAsync<BoardArchiveThreadDetails?>(nameof(ReadBoardArchiveThreadAsync), thread.Url, async () =>
        {
            EnsureBoardArchiveReady();
            PluginSourceImageBoard source = ArchiveSource(new Uri(thread.Url));
            BoardArchiveThreadDetails? details = await ReadArchiveWhenAdmittedAsync(
                () => source.ReadBoardArchiveThreadAsync(thread.Url, token), token).ConfigureAwait(false);
            return details == null ? null : details with { AlreadyWatching = _registry.ClassifyAdmission(thread.Url)?.AlreadyWatching == true };
        }, captureAfter: false);

    private PluginSourceImageBoard ArchiveSource(Uri uri) => _registry.FindAdmissionProvider(uri) as PluginSourceImageBoard
        ?? throw new InvalidOperationException("The board archive source is unavailable.");

    private void EnsureBoardArchiveReady()
    {
        EnsureReady();
        if (_downloadQueue.IsPaused) throw new BoardArchivePausedException();
    }

    private static async Task<T> ReadArchiveWhenAdmittedAsync<T>(Func<Task<T>> read, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return await read().ConfigureAwait(false); }
            catch (SourceCheckDeferredException deferred) when (deferred is { InitialNoSend: true, ReasonCode: "admission" })
            {
                await Task.Delay(deferred.Remaining(DateTimeOffset.UtcNow), token).ConfigureAwait(false);
            }
        }
    }
}
