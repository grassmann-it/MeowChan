using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// Danbooru addresses in the Add line (typed, pasted, from the clipboard monitor or dropped: they all arrive in
// SubmitAdmissionBatchAsync). A post address loads that post's original once; a search or the bare site only fills the
// Danbooru window's search field. Both need the Danbooru plugin enabled and composed (BooruServices); a worker is
// started by the first real request, never by recognising an address. The address is never logged: it may carry a query.
public partial class MainWindow
{
    private readonly CancellationTokenSource _booruAddLifetime = new();
    private readonly HashSet<Task> _booruAdmissionWork = new();
    private bool _admissionBooruOpened;

    /// <summary>The entries that are Danbooru addresses, by index. Pure text reading; nothing is requested.</summary>
    private static Dictionary<int, DanbooruAddress> RecognizeBooruAddresses(IReadOnlyList<string> entries)
    {
        Dictionary<int, DanbooruAddress> found = new();
        for (int index = 0; index < entries.Count; index++)
            if (DanbooruAddress.TryParse(entries[index], out DanbooruAddress? address) && address is not null)
                found.Add(index, address);
        return found;
    }

    /// <summary>
    /// Handles one recognised address; true when it was accepted (post queued or already present, or the window opened).
    /// <paramref name="ignoredSearches"/> is the number of earlier search addresses of the same input that were not used;
    /// the one toast of the search that is used names them.
    /// </summary>
    private async Task<bool> AdmitBooruAddressAsync(string entry, DanbooruAddress address, int ignoredSearches = 0)
    {
        if (_isClosing) return false;
        Task<bool> work = AdmitBooruAddressCoreAsync(entry, address, ignoredSearches);
        _booruAdmissionWork.Add(work);
        try { return await work; }
        finally { _booruAdmissionWork.Remove(work); }
    }

    private async Task<bool> AdmitBooruAddressCoreAsync(string entry, DanbooruAddress address, int ignoredSearches)
    {
        BooruWorkbenchServices? services = CanOpenBooru ? _session.ActiveBooruServices.FirstOrDefault(candidate =>
            candidate.Search.ProviderId == DanbooruPostReference.ProviderId) : null;
        if (services is null)
        {
            _admissionResults.Add(new AdmissionFeedback(address.CanonicalText, AdmissionStatus.Unsupported, Texts.T("booru.ui.add.disabled")));
            return false;
        }
        if (address.Kind == DanbooruAddressKind.Post)
        {
            // The feedback shows the canonical post page, not the pasted text (which may have carried a search).
            string url = BooruPostLinks.TryCanonical(address.PostId, out string canonical) ? canonical : entry;
            BooruAddPostOutcome outcome = await BooruAddUrl.LoadPostAsync(services, address.PostId!, _booruAddLifetime.Token);
            _admissionResults.Add(new AdmissionFeedback(url, outcome.Status, outcome.Text));
            return outcome.Status is AdmissionStatus.Added or AdmissionStatus.AlreadyPresent;
        }
        string query = address.PrefilledQuery(out bool addedSafeRating);
        (bool opened, int? limit) = OpenBooruSearchDraft(query, services);
        if (!opened)
        {
            string reason = limit is { } count ? Texts.T("booru.tabs.limit", count) : Texts.T("booru.ui.add.disabled");
            _admissionResults.Add(new AdmissionFeedback(address.CanonicalText, AdmissionStatus.Unsupported, reason));
            return false;
        }
        _admissionBooruOpened = true;
        string message = Texts.T(addedSafeRating ? "booru.ui.add.openedSafe" : "booru.ui.add.opened");
        if (ignoredSearches > 0) message += " " + Texts.T("booru.ui.add.ignored", ignoredSearches);
        ShowNotification(Texts.T("booru.ui.title"), message, NotificationType.Information, operation: "booru.add.search");
        return true;
    }

    /// <summary>Lists a search address of this input that is not used because a later one is. Its query stays out of the text.</summary>
    private void ReportIgnoredBooruSearch(DanbooruAddress address) =>
        _admissionResults.Add(new AdmissionFeedback(address.CanonicalText, null, Texts.T("booru.ui.add.ignoredRow"), Skipped: true));
}
