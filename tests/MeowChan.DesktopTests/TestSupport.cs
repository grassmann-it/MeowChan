using MeowChan.Testing;

namespace MeowChan.DesktopTests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = TemporaryWorkspacePaths.CreateDirectory("meowchan-desktop-tests-");

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
