using System;
using System.Threading.Tasks;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    internal BooruWorkspaceContent? BooruContent => BooruServices?.Content;

    internal Task<BooruContentProfileSnapshot> ReadBooruContentAsync() =>
        Task.Run(() => new BooruContentProfileStore(_profileLease).Load());

    internal Task<BooruContentProfileSnapshot> SaveBooruContentAsync(BooruContentSettings settings, long revision) =>
        RunInteractiveWithAdmissionAsync(nameof(SaveBooruContentAsync), "profile", admission =>
        {
            BooruWorkspaceContent content = BooruContent ?? throw new InvalidOperationException("Booru is unavailable.");
            BooruContentProfileSnapshot snapshot = new BooruContentProfileStore(_profileLease).SaveAdmitted(settings, revision, admission);
            content.Publish(snapshot);
            return Task.FromResult(snapshot);
        }, captureAfter: false);
}
