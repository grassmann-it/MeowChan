using System;
using oChan.Desktop.Localization;
using oChan.Http;

namespace oChan.Desktop;

internal static class RequestStatus
{
    public static string? Format(RequestPolicyProgress? progress)
    {
        return progress switch
        {
            { Phase: RequestPolicyPhase.WaitingForAdmission } => Texts.T("request.waiting"),
            { Phase: RequestPolicyPhase.Cooldown, Delay: TimeSpan delay } =>
                Texts.T("request.cooldownSeconds", Math.Ceiling(delay.TotalSeconds)),
            { Phase: RequestPolicyPhase.Cooldown } => Texts.T("request.cooldown"),
            { Phase: RequestPolicyPhase.Sending } => Texts.T("request.checking"),
            _ => null
        };
    }
}
