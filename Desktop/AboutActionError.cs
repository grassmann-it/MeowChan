using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>One current host-action error; refresh translates its wrapper, not raw platform details.</summary>
internal sealed record AboutActionError(string? Link, string Details)
{
    internal string Render() => Link is null
        ? Texts.T("about.copyFailed", Details)
        : Texts.T("about.linkFailed", Link, Details);
}
