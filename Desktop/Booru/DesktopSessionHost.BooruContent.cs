using System;
using System.Threading.Tasks;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    internal BooruWorkspaceContent? BooruContent => BooruServices?.Content;

    internal Task<BooruContentProfileSnapshot> ReadBooruContentAsync(bool acceptPersistedChoice) =>
        Task.Run(() => ReadBooruContentProfile(acceptPersistedChoice));

    private BooruContentProfileSnapshot ReadBooruContentProfile(bool acceptPersistedChoice = false) =>
        (BooruContent ?? LocalMediaContent).Read(_profileLease, acceptPersistedChoice);

    internal Task<BooruContentProfileSnapshot> SaveBooruContentAsync(BooruContentSettings settings, long revision) =>
        RunInteractiveWithAdmissionAsync(nameof(SaveBooruContentAsync), "profile", admission =>
        {
            BooruWorkspaceContent content = BooruContent ?? throw new InvalidOperationException("Booru is unavailable.");
            return Task.FromResult(content.Save(_profileLease, settings, revision, admission));
        }, captureAfter: false);
}
