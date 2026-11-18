using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>The sentence under a row that needs a retry: it names the command and, when bound, its shortcut.</summary>
internal static class WorkNextStep
{
    internal static string Text(string commandLabel, string? shortcut) =>
        Texts.T("work.nextStep", string.IsNullOrEmpty(shortcut) ? commandLabel : commandLabel + " (" + shortcut + ")");
}
