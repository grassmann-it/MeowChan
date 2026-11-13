using System.Runtime.CompilerServices;

namespace MeowChan.DesktopTests;

// The Boards empty state leads to the same next step as the Threads state (clipboard import);
// the button is offered only for a truly empty, current list, never for a search without hits.
public sealed class BoardsEmptyStateActionTests
{
    [Fact]
    public void BoardsEmptyStateHasTheImportActionWiredLikeTheThreadsState()
    {
        string axaml = Read("MainWindow.axaml");
        Assert.Contains("x:Name=\"BoardsEmptyImportButton\"", axaml);
        string button = axaml[axaml.IndexOf("x:Name=\"BoardsEmptyImportButton\"", StringComparison.Ordinal)..];
        Assert.Contains("Click=\"OnEmptyStateImportClick\"", button[..button.IndexOf("/>", StringComparison.Ordinal)]);
        Assert.Contains("BoardsEmptyImportButton.IsVisible = BoardsEmptyState.IsVisible && current && !active;", Read("MainWindow.DownloadSearch.cs"));
        Assert.Contains("CommandBindings.Apply(BoardsEmptyImportButton, \"watch.import-clipboard\", iconWithLabel: true);", Read("MainWindow.Presentation.cs"));
        Assert.Contains("BoardsEmptyImportButton.IsEnabled = ready && !_importing;", Read("MainWindow.axaml.cs"));
    }

    private static string Read(string name, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../../Desktop/Shell", name)));
}
