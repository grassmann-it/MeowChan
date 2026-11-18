using Avalonia.Controls;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopSmoke;

// AP2: the File name and SHA-256 searches have no own toolbar buttons any more. Their product entry is the
// scope chip of the reader find bar (or of the archive field) followed by Search or Enter.
internal static class UnifiedSearchEntry
{
    /// <summary>Opens the reader find bar if needed, chooses <paramref name="scope"/> and returns its Search button.</summary>
    internal static Button ReaderScope(ArchivePage page, SearchScope scope)
    {
        StackPanel bar = page.FindControl<StackPanel>("ReaderFindBar") ?? throw new InvalidOperationException("Missing ReaderFindBar.");
        if (!bar.IsVisible)
            SmokeActionClick.Execute(page.FindControl<Button>("ReaderFindOpen") ?? throw new InvalidOperationException("Missing ReaderFindOpen."));
        if (!bar.IsVisible) throw new InvalidOperationException("Reader find bar did not open for the " + scope + " scope.");
        (page.FindControl<SearchScopeChip>("ReaderFindScope") ?? throw new InvalidOperationException("Missing ReaderFindScope.")).Scope = scope;
        return page.FindControl<Button>("ReaderFindSearch") ?? throw new InvalidOperationException("Missing ReaderFindSearch.");
    }
}
