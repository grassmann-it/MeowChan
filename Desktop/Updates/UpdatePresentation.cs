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
        if (view.Update is { } update) return Texts.T("updates.available", update.Version);
        if (view.ManualResult is { Status: UpdateCheckStatus.UpToDate }) return Texts.T("updates.current");
        return view.LastCheckedUtc.HasValue ? string.Empty : Texts.T("updates.unchecked");
    }
}
