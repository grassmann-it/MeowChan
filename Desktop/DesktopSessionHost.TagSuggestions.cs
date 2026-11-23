using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Tags;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    internal Task<IReadOnlyList<StoredTagSuggestion>> LoadTagSuggestionsAsync(string sha256, long length, CancellationToken token)
        => RunInteractiveWithAdmissionAsync(nameof(LoadTagSuggestionsAsync), "profile", async admission =>
        {
            await _commands.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureReady();
                lock (_privateTagsSync)
                {
                    _privateTagStore ??= new(_profileLease.ProfileDirectory, _profileLease, _privateTagLimits);
                    TagSuggestionStore store = new(_profileLease, privateTags: _privateTagStore);
                    return store.RecoverReviewAdmitted(admission, token).ReviewFor(sha256, length, store.Settings.ConfidenceThreshold);
                }
            }
            finally { _commands.Release(); }
        }, captureAfter: false);
}
