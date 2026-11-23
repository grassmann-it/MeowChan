using MeowChan.Desktop;
using MeowChan.FolderSources;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class StartupMediaFolderSessionTests
{
    [Fact]
    public void APositionalFolderIsResolvedWithoutCreatingIt()
    {
        string relative = "N7 folder " + Guid.NewGuid().ToString("N");
        string? path = StartupFolderSession.ReadPositionalPath([relative + Path.DirectorySeparatorChar]);

        Assert.Equal(Path.GetFullPath(relative), path);
        using StartupFolderSession start = new(path!);
        Assert.True(start.Registration.OpenMediaView);
        Assert.Equal("profile.error.mediaUnavailable", start.SourceProblem([]));
        Assert.False(Directory.Exists(start.Source));
        Assert.Null(StartupFolderSession.ReadPositionalPath([]));
        Assert.Null(StartupFolderSession.ReadPositionalPath(["--media-folder", relative]));
        Assert.Null(StartupFolderSession.ReadPositionalPath(["--profile", relative]));
    }

    [Fact]
    public void SourceAndTargetChecksRefuseLinksAndProtectedLocations()
    {
        using TempDirectory root = new();
        string media = Directory.CreateDirectory(Path.Combine(root.Path, "media")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(media, "photos")).FullName;
        string profile = Path.Combine(root.Path, "profile");
        ProfileSwitchService.PrepareNew(profile, Path.Combine(root.Path, "downloads"), "en");

        using StartupFolderSession direct = new(child);
        Assert.Null(direct.SourceProblem([]));
        Assert.Equal("profile.error.mediaUnsafe", direct.SourceProblem([media]));
        Assert.Null(direct.ProfileProblem(profile, [profile]));
        Assert.Equal("profile.status.notProfile", direct.ProfileProblem(Path.Combine(root.Path, "missing-profile"), []));
        Assert.False(Directory.Exists(Path.Combine(root.Path, "missing-profile")));
        string unsafeProfile = Path.Combine(root.Path, "overlapping-downloads");
        ProfileSwitchService.PrepareNew(unsafeProfile, Path.Combine(child, "downloads"), "en");
        Assert.Equal("profile.error.mediaUnsafe", direct.ProfileProblem(unsafeProfile, []));
        Assert.False(Directory.Exists(Path.Combine(child, "downloads")));

        // Creating a symbolic link on Windows requires a privilege unavailable on some test runners.
        if (OperatingSystem.IsWindows()) return;
        string link = Path.Combine(root.Path, "link");
        Directory.CreateSymbolicLink(link, media);
        using StartupFolderSession linked = new(Path.Combine(link, "photos"));
        Assert.Equal("profile.error.mediaUnavailable", linked.SourceProblem([]));
        string targetLink = Path.Combine(root.Path, "target-link");
        Directory.CreateSymbolicLink(targetLink, profile);
        Assert.Equal("profile.status.notProfile", direct.ProfileProblem(targetLink, []));
    }

    [Fact]
    public async Task ReadOnlySourceSurvivesTemporaryDiscardAndReopensInTheSelectedProfile()
    {
        using TempDirectory root = new();
        string media = Directory.CreateDirectory(Path.Combine(root.Path, "media")).FullName;
        string original = Path.Combine(media, "cat.png");
        byte[] image = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 3];
        File.WriteAllBytes(original, image);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(original, UnixFileMode.UserRead);
            File.SetUnixFileMode(media, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        try
        {
            string selectedProfile = Path.Combine(root.Path, "selected");
            ProfileSwitchService.PrepareNew(selectedProfile, Path.Combine(root.Path, "downloads"), "en");
            using StartupFolderSession start = new(media);
            Assert.Null(start.ProfileProblem(selectedProfile, []));
            string temporaryProfile = start.PrepareTemporaryProfile("en", [selectedProfile]);
            Assert.StartsWith(temporaryProfile + Path.DirectorySeparatorChar, Config.LoadConfig(temporaryProfile).DownloadPath);

            await using (DesktopSessionHost selected = new(selectedProfile, Array.Empty<IImageBoard>(), readOnlyStartupSource: media))
            {
                await selected.StartAsync();
                Assert.Equal("profile.status.inUse", start.ProfileProblem(selectedProfile, []));

                await using (DesktopSessionHost temporary = new(temporaryProfile, Array.Empty<IImageBoard>()))
                {
                    await temporary.StartAsync();
                    Assert.False(start.RemoveTemporaryProfile());
                    Assert.Equal(StartupMediaFolderStatus.Added, (await StartupMediaFolderRegistration.RegisterAsync(
                        temporary, start.Registration, [selectedProfile])).Status);
                    Assert.Equal(media, Assert.Single(temporary.ReadFolderSourceRoots()).Path);
                    Assert.Empty(selected.ReadFolderSourceRoots());
                }
                Assert.True(start.RemoveTemporaryProfile());
                Assert.False(Directory.Exists(temporaryProfile));

                Assert.Equal(StartupMediaFolderStatus.Added, (await StartupMediaFolderRegistration.RegisterAsync(
                    selected, start.Registration, [])).Status);
            }

            await using DesktopSessionHost reopened = new(selectedProfile, Array.Empty<IImageBoard>());
            await reopened.StartAsync();
            Assert.Equal(StartupMediaFolderStatus.AlreadyAdded, (await StartupMediaFolderRegistration.RegisterAsync(
                reopened, start.Registration, [])).Status);
            Assert.Equal(media, Assert.Single(reopened.ReadFolderSourceRoots()).Path);
            Assert.Equal(image, File.ReadAllBytes(original));
            Assert.Equal(original, Assert.Single(Directory.GetFileSystemEntries(media)));
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(media, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void DiscardRemovesOnlyTheOwnedDirectoryAndDoesNotFollowItsLinks()
    {
        if (OperatingSystem.IsWindows()) return;
        using TempDirectory root = new();
        string marker = Path.Combine(root.Path, "keep");
        File.WriteAllText(marker, "keep");
        using StartupFolderSession start = new(root.Path);
        string temporary = start.PrepareTemporaryProfile("en", []);
        Directory.CreateSymbolicLink(Path.Combine(temporary, "outside"), root.Path);
        string moved = Path.Combine(Path.GetDirectoryName(temporary)!, Path.GetFileName(temporary) + "-moved");
        Directory.Move(temporary, moved);
        try
        {
            Directory.CreateDirectory(temporary);
            Assert.False(start.RemoveTemporaryProfile());
            Assert.True(Directory.Exists(temporary));
            Directory.Delete(temporary);
        }
        finally { Directory.Move(moved, temporary); }

        Assert.True(start.RemoveTemporaryProfile());
        Assert.False(Directory.Exists(temporary));
        Assert.Equal("keep", File.ReadAllText(marker));
    }

    [Fact]
    public async Task ASecondProfileOpenCannotWinDuringDiscard()
    {
        using TempDirectory root = new();
        using StartupFolderSession start = new(root.Path);
        string temporary = start.PrepareTemporaryProfile("en", []);
        TaskCompletionSource locked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim resume = new();
        TimeSpan timeout = TimeSpan.FromSeconds(10);
        Task<bool> discard = Task.Run(() => start.RemoveTemporaryProfile(() =>
        {
            locked.SetResult();
            if (!resume.Wait(timeout)) throw new TimeoutException();
        }));
        try
        {
            await locked.Task.WaitAsync(timeout);
            await Task.Run(() => Assert.Throws<IOException>(() =>
            {
                using ProfileLease unexpected = ProfileLease.Acquire(temporary);
            })).WaitAsync(timeout);
            Assert.True(File.Exists(Path.Combine(temporary, ".meowchan-profile.protocol")));
        }
        finally { resume.Set(); }
        Assert.True(await discard.WaitAsync(timeout));
        Assert.False(Directory.Exists(temporary));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInterruptedDiscardResumesOnlyForTheMarkedDirectory(bool alreadyMoved)
    {
        using TempDirectory root = new();
        string original = Directory.CreateDirectory(Path.Combine(root.Path, TemporaryProfileDiscard.Prefix + "marked")).FullName;
        string unrelated = Directory.CreateDirectory(Path.Combine(root.Path, TemporaryProfileDiscard.Prefix + "unmarked")).FullName;
        string remaining = Path.Combine(original, "media", "remaining");
        Directory.CreateDirectory(Path.GetDirectoryName(remaining)!);
        File.WriteAllText(remaining, "left after interruption");
        ProfileDirectoryIdentity identity = ProfileDirectoryIdentity.Capture(original);
        string pending = original;
        if (alreadyMoved)
            pending = TemporaryProfileDiscard.Quarantine(original, identity);
        else
            File.WriteAllText(Path.Combine(pending, ProfileLease.DiscardMarkerName), identity.DiscardToken);
        Assert.Throws<IOException>(() =>
        {
            using ProfileLease unexpected = ProfileLease.Acquire(pending);
        });

        using ProfileLease? replacement = alreadyMoved ? ProfileLease.Acquire(original) : null;
        File.WriteAllText(Path.Combine(unrelated, ProfileLease.DiscardMarkerName), identity.DiscardToken);
        TemporaryProfileDiscard.RetryPending(root.Path);

        Assert.Equal(alreadyMoved, Directory.Exists(original));
        Assert.False(Directory.Exists(pending));
        Assert.True(Directory.Exists(unrelated));
    }
}
