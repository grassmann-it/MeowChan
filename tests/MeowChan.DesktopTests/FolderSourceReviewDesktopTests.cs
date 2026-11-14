using System.Collections.Concurrent;
using System.Diagnostics;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.FolderSources;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// Wave 3 review, folder sources on the desktop side: adding a folder and the startup media folder never check the catalog or
// the file system on the caller's (UI) thread (W3-12), and failures are shown as localized sentences, never as exception texts
// that may name paths (W3-15). Temp profiles and temp folders only.
[Collection("Localization")]
public sealed class FolderSourceReviewDesktopTests : IDisposable
{
    private readonly string _previous = Texts.Language;
    public FolderSourceReviewDesktopTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    [Fact]
    public async Task AddingAFolderAndTheStartupFolderCheckTheCatalogOnAWorkerNotOnTheCaller()
    {
        using TempDirectory temp = new();
        string downloads = Path.Combine(temp.Path, "downloads"), profile = Path.Combine(temp.Path, "profile");
        string added = Path.Combine(temp.Path, "added"), startup = Path.Combine(temp.Path, "startup");
        foreach (string folder in new[] { downloads, added, startup }) Directory.CreateDirectory(folder);
        new Config { DownloadPath = downloads, DownloadsPaused = true, AutomaticUpdateChecks = false, FolderSourceBackgroundHashing = false }.SaveConfig(profile);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync();

        int caller = Environment.CurrentManagedThreadId;
        bool inCall = false;
        ConcurrentBag<bool> onCallerDuringCall = new();
        using ManualResetEventSlim release = new();
        host.FolderSourceCatalogForTests.BeforeFileAccessForTests = () =>
        {
            onCallerDuringCall.Add(Volatile.Read(ref inCall) && Environment.CurrentManagedThreadId == caller);
            release.Wait(TimeSpan.FromSeconds(30));
        };
        Stopwatch call = Stopwatch.StartNew();
        Volatile.Write(ref inCall, true);
        Task<FolderSourceRoot> adding = host.AddFolderSourceAsync(added, "Added", includeSubfolders: true);
        Task<StartupMediaFolderResult> registering = StartupMediaFolderRegistration.RegisterAsync(host,
            new StartupMediaFolderArgument(startup, StartupMediaFolderProblem.None), Array.Empty<string>());
        Volatile.Write(ref inCall, false);
        call.Stop();
        // Both calls returned while every catalog access was still held back: nothing ran synchronously on the caller.
        Assert.True(call.Elapsed < TimeSpan.FromSeconds(10), "A call waited for the catalog on the caller's thread.");
        Assert.False(adding.IsCompleted);
        Assert.False(registering.IsCompleted);
        release.Set();
        Assert.Equal("Added", (await adding).Name);
        Assert.Equal(StartupMediaFolderStatus.Added, (await registering).Status);
        host.FolderSourceCatalogForTests.BeforeFileAccessForTests = null;
        Assert.NotEmpty(onCallerDuringCall);
        Assert.DoesNotContain(true, onCallerDuringCall);
        Assert.Equal(2, host.ReadFolderSourceRoots().Count);
    }

    [Fact]
    public void FolderSourceFailuresAreLocalizedSentencesThatNeverNameAPath()
    {
        const string secret = "/home/someone/Pictures/private-holiday";
        Exception[] failures =
        {
            new FolderSourceException(FolderSourceError.CatalogFull),
            FolderSourceManifests.Unavailable(new FolderSourceException(FolderSourceError.CatalogNewer)),
            new IOException("Disk full while writing " + secret),
            new UnauthorizedAccessException("Access to the path '" + secret + "' is denied."),
            new InvalidDataException("Bad data in " + secret),
            new Microsoft.Data.Sqlite.SqliteException("database disk image is malformed: " + secret, 11),
            new ObjectDisposedException(secret),
        };
        string[] keys = failures.Select(FolderSourceErrorText.Key).ToArray();
        Assert.Equal(new[]
        {
            "folderSource.error.full", "folderSource.error.newer", "folderSource.error.io", "folderSource.error.io",
            "folderSource.error.damaged", "folderSource.error.damaged", "folderSource.error.io"
        }, keys);
        foreach (string language in new[] { "en", "de", "fr", "ja", "pl" })
        {
            Texts.SetLanguage(language);
            foreach (Exception failure in failures)
            {
                string shown = FolderSourceErrorText.Message(failure);
                Assert.False(string.IsNullOrWhiteSpace(shown));
                Assert.DoesNotContain(secret, shown);
                Assert.DoesNotContain(failure.Message, shown);
                Assert.DoesNotContain(secret, FolderSourceErrorText.Code(failure));
            }
        }
        Texts.SetLanguage("en");
        // A full catalog says how to get out of it: remove a folder or raise the named setting.
        string full = FolderSourceErrorText.Message(failures[0]);
        Assert.Contains(Texts.T("budgets.folderSourceCatalog"), full);
        Assert.Contains("remove a folder", full);
        // Only catalog failures are rephrased where other views show their own error text (the gallery).
        Assert.NotNull(FolderSourceErrorText.Describe(failures[1]));
        Assert.Null(FolderSourceErrorText.Describe(failures[2]));
        Assert.Equal("FolderSource.CatalogNewer", FolderSourceErrorText.Code(failures[1]));
        Assert.StartsWith("SqliteException 11/", FolderSourceErrorText.Code(failures[5]));
    }
}
