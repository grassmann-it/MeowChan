using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Updates;

internal static class UpdatePresentation
{
    internal static string Status(UpdateView view)
    {
        if (view.EnvironmentDisabled) return Texts.T("updates.disabled");
        if (view.IsChecking) return Texts.T("updates.checking");
        if (view.ManualResult is { Status: UpdateCheckStatus.Failed } failure)
            return Texts.T(failure.Error switch
            {
                UpdateCheckError.Network => "updates.error.network",
                UpdateCheckError.RateLimited => "updates.error.rateLimited",
                UpdateCheckError.InvalidResponse => "updates.error.invalidResponse",
                UpdateCheckError.Timeout => "updates.error.timeout",
                UpdateCheckError.Persistence => "updates.error.persistence",
                _ => "updates.error.http"
            });
        // The retained release is rendered separately beside its actions, including after a failed check.
        if (view.Update != null) return string.Empty;
        if (view.ManualResult is { Status: UpdateCheckStatus.UpToDate }) return Texts.T("updates.current");
        return view.LastCheckedUtc.HasValue ? string.Empty : Texts.T("updates.unchecked");
    }
}
