using System;
using System.Threading.Tasks;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    internal BooruWorkspaceContent? BooruContent => BooruServices?.Content;

    /// <summary>The profile's content rules, shared by the workbench, saved media, MD5 lookups and galleries.</summary>
    internal BooruWorkspaceContent ContentRules => BooruContent ?? LocalMediaContent;

    /// <summary>The single access for every consumer that reads the profile document: the effective rules, never the file itself.</summary>
    internal BooruContentProfileSnapshot ReadEffectiveBooruContent() => ContentRules.ReadEffective(_profileLease);

    /// <summary>The document for the content editor only.</summary>
    internal Task<BooruContentProfileSnapshot> ReadBooruContentForEditAsync(bool acceptPersistedChoice) =>
        Task.Run(() => ContentRules.ReadForEdit(_profileLease, acceptPersistedChoice));

    internal Task<BooruContentProfileSnapshot> SaveBooruContentAsync(BooruContentEdit edit) =>
        RunInteractiveWithAdmissionAsync(nameof(SaveBooruContentAsync), "profile", admission =>
        {
            BooruWorkspaceContent content = BooruContent ?? throw new InvalidOperationException("Booru is unavailable.");
            return Task.FromResult(content.Save(_profileLease, edit, admission));
        }, captureAfter: false);
}
