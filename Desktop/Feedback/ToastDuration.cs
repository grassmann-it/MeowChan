using System;
using Avalonia.Controls.Notifications;

namespace MeowChan.Desktop.Feedback;

/// <summary>
/// How long a shell toast stays: confirmations leave quickly, warnings stay longer and errors keep the
/// toast default (they also persist in the shell banner and the diagnostics history). A toast that
/// offers an action stays long enough to use it. Longer texts get proportionally more reading time.
/// </summary>
internal static class ToastDuration
{
    internal static readonly TimeSpan Action = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan Warning = TimeSpan.FromSeconds(6);
    internal static readonly TimeSpan Minimum = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan Maximum = TimeSpan.FromSeconds(8);
    private const int ShortTextLength = 40;
    private const int CharactersPerExtraSecond = 25;

    /// <summary>Null keeps the notification manager default.</summary>
    internal static TimeSpan? For(NotificationType type, int textLength, bool hasAction)
    {
        if (hasAction) return Action;
        return type switch
        {
            NotificationType.Error => null,
            NotificationType.Warning => Warning,
            _ => Reading(textLength)
        };
    }

    private static TimeSpan Reading(int textLength)
    {
        double extra = Math.Max(0, textLength - ShortTextLength) / (double)CharactersPerExtraSecond;
        TimeSpan value = Minimum + TimeSpan.FromSeconds(extra);
        return value > Maximum ? Maximum : value;
    }
}
