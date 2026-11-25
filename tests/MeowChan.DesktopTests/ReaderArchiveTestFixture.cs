using Avalonia;
using Avalonia.Platform;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopTests;

internal sealed class ReaderArchiveTestFixture : IDisposable
{
    private readonly TempDirectory _profile = new();

    internal async Task<ArchivedThreadView> CaptureAsync(ResourceId id, string title, CapturedPost[] posts)
    {
        using (ProfileLease lease = ProfileLease.Acquire(_profile.Path))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(_profile.Path, lease))
        {
            await catalog.CaptureAsync(new(id, "https://example.invalid/not-acquired", title,
                ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, Path.Combine(_profile.Path, "uncreated-media"), posts));
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(_profile.Path);
        return Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(id));
    }

    internal void Run(Action<ArchivePage> test, bool hosted = false, Func<ResourceId, bool, Task>? setCapture = null) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        using IDisposable? platform = hosted ? BooruReviewTestPlatform.Enter() : null;
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        if (!hosted) TokenApplication.Bind();
        ArchivePage page = new(token => ArchiveReader.OpenAsync(_profile.Path, token), setCapture ?? ((_, _) => Task.CompletedTask), _ => { });
        try
        {
            page.IsReadable = true;
            test(page);
        }
        finally
        {
            page.EndLifetime();
            BooruComponentDispatcher.Wait(page.MediaLoadTask);
        }
    });

    internal static void Load(ArchivePage page, ArchivedThreadView view, bool resetPage = true)
    {
        BooruComponentDispatcher.Wait(page.LoadThreadAsync(view.Summary.Id, resetPage, view));
        Assert.Equal(view.Summary.Id, page.CurrentThread?.Summary.Id);
    }

    public void Dispose() => _profile.Dispose();
}
