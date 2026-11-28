using System;
using System.Threading.Tasks;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    internal BooruWorkspaceContent? BooruContent => BooruServices?.Content;

    /// <summary>The document for the content editor only.</summary>
    internal Task<BooruContentProfileSnapshot> ReadBooruContentForEditAsync() =>
        Task.Run(() => new BooruContentProfileStore(_profileLease).Load());

    internal Task<BooruContentProfileSnapshot> SaveBooruContentAsync(BooruContentEdit edit) =>
        RunInteractiveWithAdmissionAsync(nameof(SaveBooruContentAsync), "profile", admission =>
        {
            BooruWorkspaceContent content = BooruContent ?? throw new InvalidOperationException("Booru is unavailable.");
            BooruContentProfileSnapshot snapshot = new BooruContentProfileStore(_profileLease).SaveEditAdmitted(edit, admission);
            content.Publish(snapshot);
            return Task.FromResult(snapshot);
        }, captureAfter: false);
}
