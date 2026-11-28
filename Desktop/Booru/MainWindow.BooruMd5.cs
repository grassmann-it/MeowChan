using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.Gallery;

namespace MeowChan;

public partial class MainWindow
{
    internal static BooruMd5Lookup? Md5LookupFor(Control owner) => TopLevel.GetTopLevel(owner) is MainWindow { CanOpenBooru: true } window
        ? new BooruMd5Lookup(() => window.CanOpenBooru ? window._session.ActiveBooruServices : [], window.SubmitMd5SearchAsync)
        : null;

    private async Task SubmitMd5SearchAsync(BooruWorkbenchServices source, string query)
    {
        if (!CanOpenBooru || !_session.ActiveBooruServices.Contains(source)) return;
        if (_gallery != null) CloseGallery(restoreOrigin: false);
        if (_largeReaderOpen) CloseLargeReader(restoreOrigin: false);
        // The first content choice may defer the draft. Submit only a tab opened by this call.
        if (OpenBooruSearchDraft(query, source).Opened && _selectedPage == WorkspacePage.Booru
            && _booruPage is { } page && page.Model.Search.View.Input == query)
            await page.View.SubmitSearchAsync();
    }

    internal bool CanOfferMd5Batch => FindMd5BatchSource() is not null;

    private BooruWorkbenchServices? FindMd5BatchSource() => CanOpenBooru ? _session.ActiveBooruServices.FirstOrDefault(source =>
        source.Search.ProviderId == DanbooruPostReference.ProviderId) : null;

    internal Task OpenMd5BatchAsync(IReadOnlyList<MediaLibraryEntry> entries,
        Func<MediaLibraryEntry, CancellationToken, Task<string?>> readMd5, CancellationToken token)
    {
        if (entries.Count == 0 || FindMd5BatchSource() is not { } source) return Task.CompletedTask;
        BooruMd5Batch batch = new(entries, readMd5, source.Search,
            () => _session.Config.BooruMd5BatchEnabled && CanOpenBooru && _session.ActiveBooruServices.Contains(source),
            _session.ReadBooruMd5ContentFilter, source.Clock, _session.Config.BooruSearchPostsPerPage);
        _session.ReadBooruMd5ContentFilter();
        BooruMd5BatchWindow dialog = new(batch, _session.ContentRules, _session.Config.BooruMd5BatchEnabled,
            enabled => _session.SaveViewPreferencesAsync(config => config.BooruMd5BatchEnabled = enabled), token);
        return dialog.ShowDialog(this);
    }
}
