using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;

namespace MeowChan.Desktop;

/// <summary>One profile-local bounded snapshot and one exact-source expansion. UI-thread owned.</summary>
internal sealed class ArchiveSuggestionCache : IDisposable
{
    private readonly Func<CancellationToken, Task<IdentitySuggestions>> _load;
    private readonly Func<string, CancellationToken, Task<ArchiveIdentityPage<ArchiveBoardIdentity>>> _loadScope;
    private readonly CancellationTokenSource _lifetime;
    private Task<IdentitySuggestions>? _initial;
    private string? _scope;
    private ArchiveIdentityPage<ArchiveBoardIdentity>? _scopeBoards;
    private bool _disposed;
    internal ArchiveSuggestionCache(Func<CancellationToken, Task<IdentitySuggestions>> load,
        Func<string, CancellationToken, Task<ArchiveIdentityPage<ArchiveBoardIdentity>>> loadScope, CancellationToken lifetime = default)
    { _load = load; _loadScope = loadScope; _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime); }

    internal async Task<IdentitySuggestion[]> MatchAsync(bool boards, string text, string? source, CancellationToken input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Keystrokes cancel only their waiter. The bounded snapshot is read once.
        using var request = CancellationTokenSource.CreateLinkedTokenSource(input, _lifetime.Token);
        var token = request.Token;
        var snapshot = await (_initial ??= _load(_lifetime.Token)).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        if (boards && snapshot.Boards.HasMore && !string.IsNullOrEmpty(source) && source != _scope)
        {
            await Task.Delay(200, token);
            var items = await _loadScope(source, token);
            token.ThrowIfCancellationRequested();
            _scope = source; _scopeBoards = items;
        }
        token.ThrowIfCancellationRequested();
        return snapshot.Match(boards, text, source, source == _scope ? _scopeBoards : null);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _lifetime.Dispose();
        _initial = null; _scope = null; _scopeBoards = null;
    }
}
