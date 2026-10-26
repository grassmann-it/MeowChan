using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

/// <summary>Shared harness facade: no dependency on candidate projection or pump types.</summary>
internal interface ISyntheticShellRows : IAsyncDisposable
{
    IReadOnlyList<ThreadRow> Threads { get; }
    IReadOnlyList<BoardRow> Boards { get; }
    void Update(DesktopThreadEntry entry);
    Task UpdateAsync(DesktopThreadEntry entry);
    Task UpdateAsync(DesktopBoardEntry entry);
    Task WaitForAppliedAsync();
}
