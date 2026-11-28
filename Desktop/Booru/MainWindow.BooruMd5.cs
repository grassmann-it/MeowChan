using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
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
        if (!await EnsureBooruContentChoiceAsync(this) || !_session.ActiveBooruServices.Contains(source)
            || !EnsureBooruPage() || _booruPage is not { } page) return;

        (bool opened, int? limit) = OpenMediaBooruSearchDraft(page, query, source);
        if (opened) await page.View.SubmitSearchAsync();
        else if (limit is { } count)
            await DialogPrompt.BuildNotice(Texts.T("booru.ui.title"), Texts.T("booru.tabs.limit", count),
                Texts.T("action.close"), "BooruMd5TabLimit").ShowDialog(this);
    }

    internal bool CanOfferMd5Batch => FindMd5BatchSource() is not null;

    private BooruWorkbenchServices? FindMd5BatchSource() => CanOpenBooru ? _session.ActiveBooruServices.FirstOrDefault(source =>
        source.Search.ProviderId == DanbooruPostReference.ProviderId) : null;

    internal Task OpenMd5BatchAsync(IReadOnlyList<MediaLibraryEntry> entries,
        Func<MediaLibraryEntry, CancellationToken, Task<string?>> readMd5, CancellationToken token)
    {
        if (entries.Count == 0 || FindMd5BatchSource() is not { } source) return Task.CompletedTask;
        BooruContentFilter? ReadFilter() => _session.ReadBooruMd5ContentProfile() is { State: BooruContentProfileState.Ready } profile
            ? profile.Filter : null;
        BooruMd5Batch batch = new(entries, readMd5, source.Search,
            () => _session.Config.BooruMd5BatchEnabled && CanOpenBooru && _session.ActiveBooruServices.Contains(source),
            ReadFilter, source.Clock, _session.Config.BooruSearchPostsPerPage);
        BooruMd5BatchWindow dialog = new(batch, ReadFilter() is not null, _session.Config.BooruMd5BatchEnabled,
            enabled => _session.SaveViewPreferencesAsync(config => config.BooruMd5BatchEnabled = enabled), token);
        return dialog.ShowDialog(this);
    }
}
