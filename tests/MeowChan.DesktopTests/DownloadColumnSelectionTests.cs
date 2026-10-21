using MeowChan;

namespace MeowChan.DesktopTests;

public sealed class DownloadColumnSelectionTests
{
    [Fact]
    public void ThreadColumnsKeepAnIdentifyingColumn()
    {
        string[] remaining = ["Title", "Status", "Completion"];
        Assert.False(MainWindow.CanHideDownloadColumn("ThreadsDataGrid", "Title", remaining));
        Assert.True(MainWindow.CanHideDownloadColumn("ThreadsDataGrid", "Status", remaining));
        Assert.True(MainWindow.CanHideDownloadColumn("ThreadsDataGrid", "Title", ["ThreadNumber", "Title", "Status"]));
        Assert.False(MainWindow.CanHideDownloadColumn("ThreadsDataGrid", "Title", ["SourceBoard", "Title", "Status"]));
    }

    [Fact]
    public void BoardColumnsKeepAnIdentifyingColumn()
    {
        Assert.False(MainWindow.CanHideDownloadColumn("BoardsDataGrid", "NiceName", ["NiceName", "Status"]));
        Assert.True(MainWindow.CanHideDownloadColumn("BoardsDataGrid", "HostName", ["HostName", "NiceName", "Status"]));
        Assert.False(MainWindow.CanHideDownloadColumn("BoardsDataGrid", "NiceName", ["HostName", "NiceName", "Status"]));
    }

    [Fact]
    public void SavedVisibilityWithoutAnIdentityRecoversOnDisplay()
    {
        Assert.Equal("Title", MainWindow.RequiredDownloadIdentityToRestore("ThreadsDataGrid", ["SourceBoard", "Status"]));
        Assert.Equal("NiceName", MainWindow.RequiredDownloadIdentityToRestore("BoardsDataGrid", ["HostName", "Status"]));
        Assert.Null(MainWindow.RequiredDownloadIdentityToRestore("ThreadsDataGrid", ["ThreadNumber", "Status"]));
    }
}
