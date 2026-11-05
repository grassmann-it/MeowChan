using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Testing;

namespace MeowChan.DesktopTests;

public sealed class UrlTextExportFailureReceiptBoundaryTests
{
    [Fact]
    public async Task CancellationAfterSavedReceiptDoesNotReclassifyCompletedWrite()
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-urltext-receipt-");
        try
        {
            ResourceId id = new("4chan", "b", "123");
            ArchiveThreadSummary summary = new(id, "https://boards.4chan.org/b/thread/123",
                "Synthetic receipt fixture", "", ThreadSourceState.NotFound, false,
                DateTimeOffset.UnixEpoch, null, 0, 1);
            var plan = ArchiveUrlTextExportPlan.TryCreate(id, summary, out var error);
            Assert.Equal(ArchiveUrlTextExportPlanError.None, error);
            Assert.NotNull(plan);
            byte[] expected = plan.CopyUtf8();
            string destination = Path.Combine(root, "receipt.txt");
            using CancellationTokenSource cancellation = new();

            // Await the real physical operation, including its finally/Dispose.
            // No cancellation race is guessed from file length or a scheduling delay.
            Task<UrlTextExportFileWriteResult> writing = new UrlTextExportFileWriter()
                .WriteAsync(plan, destination, cancellation.Token);
            var receipt = await writing;
            Assert.Equal(UrlTextExportFileWriteStatus.Saved, receipt.Status);
            Assert.Equal(UrlTextExportFileWriteError.None, receipt.Error);

            cancellation.Cancel();

            Assert.True(cancellation.IsCancellationRequested);
            var observedAgain = await writing; // Observe, do not retry the write.
            Assert.Same(receipt, observedAgain);
            Assert.Equal(UrlTextExportFileWriteStatus.Saved, observedAgain.Status);
            Assert.Equal(UrlTextExportFileWriteError.None, observedAgain.Error);
            using (FileStream reopened = new(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using MemoryStream actual = new();
                reopened.CopyTo(actual);
                Assert.Equal(expected, actual.ToArray());
            }
            Assert.Equal(destination, Assert.Single(Directory.GetFileSystemEntries(root)));
        }
        finally
        {
            // Only this test's synthetic directory, after the real task was joined.
            Directory.Delete(root, recursive: true);
        }
    }
}
