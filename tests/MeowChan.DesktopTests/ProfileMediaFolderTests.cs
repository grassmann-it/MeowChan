using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.FolderSources;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>
/// The "Add a media folder" option of "New profile": the <c>--media-folder</c> start argument, its validation (the folder must
/// exist, be a readable directory and no unsafe location, and is never created) and its registration as a read-only folder
/// source on the new process' first start. Temp folders only; no process is started (a recording launcher stands in).
/// </summary>
[Collection("Localization")]
public sealed class ProfileMediaFolderTests : IDisposable
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 3, 1, 4 };
    private readonly string _previous = Texts.Language;
    public ProfileMediaFolderTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    private sealed class RecordingLauncher : IProfileLauncher
    {
        internal readonly List<(string Profile, string? Media)> Started = new();
        public void Launch(string profileDirectory, string? mediaFolder = null) => Started.Add((profileDirectory, mediaFolder));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly TempDirectory Temp = new();
        internal readonly RecordingLauncher Launcher = new();
        internal string Current => Path.Combine(Temp.Path, "current");
        internal string CurrentDownloads => Path.Combine(Temp.Path, "Downloads", "MeowChan");
        internal ProfileRegistry Registry => new(Path.Combine(Temp.Path, "config", "MeowChan", ProfileRegistry.FileName));
        internal Fixture() { Directory.CreateDirectory(Current); File.WriteAllText(Path.Combine(Current, "settings.json"), "{}"); }
        internal ProfileSwitchService Service() => new(Registry, Launcher, Current, CurrentDownloads, () => ProfileRegistry.DefaultLimit);
        internal string In(params string[] parts) => Path.Combine([Temp.Path, .. parts]);
        internal string MediaWithPicture(string name = "media")
        {
            string folder = In(name);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "a.png"), Png);
            return folder;
        }
        public void Dispose() => Temp.Dispose();
    }

    // ---- The start argument ----

    [Fact]
    public void TheMediaFolderArgumentIsReadAndRemovedAndEveryOtherArgumentPassesThrough()
    {
        string folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "meowchan-media-arg"));
        string[] rest = StartupMediaFolderCommandLine.Extract(new[] { "-psn_0_1", "--media-folder", folder + Path.DirectorySeparatorChar, "--other" }, true, out var argument);
        Assert.Equal(new[] { "-psn_0_1", "--other" }, rest);
        Assert.Equal(new StartupMediaFolderArgument(folder, StartupMediaFolderProblem.None), argument);
        Assert.Empty(StartupMediaFolderCommandLine.Extract(new[] { "--media-folder=" + folder }, true, out argument));
        Assert.Equal(folder, argument!.Path);
        Assert.Equal(new[] { "x" }, StartupMediaFolderCommandLine.Extract(new[] { "x" }, true, out argument));
        Assert.Null(argument);
        // The switch and a profile switch are read independently, in either order.
        string[] both = ProfileCommandLine.Extract(StartupMediaFolderCommandLine.Extract(new[] { "--profile", "p", "--media-folder", folder }, true, out argument), out string? profile);
        Assert.Empty(both);
        Assert.Equal(Path.GetFullPath("p"), profile);
        Assert.Equal(folder, argument!.Path);
    }

    [Theory]
    [InlineData("--media-folder")]
    [InlineData("--media-folder", "--other")]
    [InlineData("--media-folder=")]
    [InlineData("--media-folder", " ")]
    [InlineData("--media-folder", "a", "--media-folder", "b")]
    public void AMalformedMediaFolderArgumentNeverStopsTheStartAndIsMarkedForTheWindowToExplain(params string[] args)
    {
        string[] rest = StartupMediaFolderCommandLine.Extract(args, true, out StartupMediaFolderArgument? argument);
        Assert.Equal(new StartupMediaFolderArgument(null, StartupMediaFolderProblem.Malformed), argument);
        // Only the switch and its value are consumed.
        Assert.DoesNotContain("--media-folder", rest);
    }

    [Fact]
    public void AMediaFolderWithoutAProfileArgumentIsRefusedForTheWindowToExplain()
    {
        string folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "meowchan-media-arg"));
        StartupMediaFolderCommandLine.Extract(new[] { "--media-folder", folder }, false, out StartupMediaFolderArgument? argument);
        Assert.Equal(new StartupMediaFolderArgument(folder, StartupMediaFolderProblem.NeedsProfile), argument);
    }

    [Fact]
    public void TheWindowTakesTheArgumentOnce()
    {
        ProfileStartup.MediaFolder = new StartupMediaFolderArgument("/x", StartupMediaFolderProblem.None);
        Assert.NotNull(ProfileStartup.TakeMediaFolder());
        Assert.Null(ProfileStartup.TakeMediaFolder());
        Assert.Null(ProfileStartup.MediaFolder);
    }

    [Fact]
    public void TheSecondProcessGetsTheMediaFolderAfterTheProfileAndNoProfileVariable()
    {
        string profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "meowchan-child-profile"));
        string media = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "meowchan-child-media"));
        string executable = Path.GetFullPath(Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "MeowChan.exe" : "MeowChan"));
        var info = ProcessProfileLauncher.CreateStartInfo(profile, executable, null, null, media + Path.DirectorySeparatorChar);
        Assert.Equal(new[] { "--profile", profile, "--media-folder", media }, info.ArgumentList);
        Assert.False(info.Environment.ContainsKey("MEOWCHAN_PROFILE"));
        Assert.Equal(new[] { "--profile", profile }, ProcessProfileLauncher.CreateStartInfo(profile, executable, null, null).ArgumentList);
        Assert.Throws<ArgumentException>(() => ProcessProfileLauncher.CreateStartInfo(profile, executable, null, null, "relative"));
        // What the launcher writes is what the child reads back.
        StartupMediaFolderCommandLine.Extract(info.ArgumentList.Skip(2).ToArray(), true, out StartupMediaFolderArgument? read);
        Assert.Equal(media, read!.Path);
    }

    // ---- Validation in the dialog (before anything is created or started) ----

    [Fact]
    public void AValidMediaFolderPassesAndIsHandedToTheLauncherUnchanged()
    {
        using Fixture fixture = new();
        string media = fixture.MediaWithPicture();
        string profile = fixture.In("profiles", "cats"), downloads = fixture.In("Downloads", "MeowChan-cats");
        ProfileSwitchService service = fixture.Service();
        Assert.Null(service.Validate("Cats", profile, downloads, media));
        ProfileActionResult result = service.Create(new NewProfileRequest("Cats", profile, downloads, "en", media + Path.DirectorySeparatorChar));
        Assert.Equal(ProfileActionStatus.Started, result.Status);
        Assert.Equal((profile, media), fixture.Launcher.Started.Single());
        // Without the option nothing is passed.
        Assert.Equal(ProfileActionStatus.Started, service.Create(new NewProfileRequest("Dogs", fixture.In("profiles", "dogs"), fixture.In("Downloads", "MeowChan-dogs"), "en")).Status);
        Assert.Null(fixture.Launcher.Started[1].Media);
    }

    [Fact]
    public void AMissingMediaFolderIsRefusedAndNeverCreatedAndNothingIsStarted()
    {
        using Fixture fixture = new();
        string missing = fixture.In("not-there", "photos");
        string profile = fixture.In("profiles", "cats"), downloads = fixture.In("Downloads", "MeowChan-cats");
        ProfileSwitchService service = fixture.Service();
        Assert.Equal("profile.error.mediaUnavailable", service.Validate("Cats", profile, downloads, missing));
        ProfileActionResult result = service.Create(new NewProfileRequest("Cats", profile, downloads, "en", missing));
        Assert.Equal((ProfileActionStatus.Refused, "profile.error.mediaUnavailable"), (result.Status, result.MessageKey));
        Assert.Empty(fixture.Launcher.Started);
        // The folder was not created, and neither was anything for the new profile: the check comes first.
        Assert.False(Directory.Exists(missing));
        Assert.False(Directory.Exists(fixture.In("not-there")));
        Assert.False(Directory.Exists(profile));
        Assert.False(File.Exists(fixture.Registry.FilePath));
        Assert.NotEqual("profile.error.mediaUnavailable", Texts.T("profile.error.mediaUnavailable"));
        Assert.Contains("does not create it", Texts.T("profile.error.mediaUnavailable"));
    }

    [Fact]
    public void AFileInsteadOfAFolderAndARelativeOrEmptyPathAreRefused()
    {
        using Fixture fixture = new();
        string file = fixture.In("photo.png");
        File.WriteAllBytes(file, Png);
        ProfileSwitchService service = fixture.Service();
        string profile = fixture.In("profiles", "cats"), downloads = fixture.In("Downloads", "MeowChan-cats");
        Assert.Equal("profile.error.mediaUnavailable", service.Validate("Cats", profile, downloads, file));
        Assert.Equal("profile.error.mediaAbsolute", service.Validate("Cats", profile, downloads, "relative/photos"));
        Assert.Equal("profile.error.mediaAbsolute", service.Validate("Cats", profile, downloads, "  "));
        Assert.Equal("profile.error.mediaAbsolute", service.Validate("Cats", profile, downloads, ""));
        Assert.Empty(fixture.Launcher.Started);
        Assert.False(Directory.Exists(profile));
    }

    [Fact]
    public void ALinkToAFolderIsRefused()
    {
        using Fixture fixture = new();
        string media = fixture.MediaWithPicture(), link = fixture.In("link");
        try { Directory.CreateSymbolicLink(link, media); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
        Assert.Equal("profile.error.mediaUnavailable", fixture.Service().Validate("Cats", fixture.In("profiles", "cats"), fixture.In("d"), link));
    }

    [Fact]
    public void AFolderThatCannotBeReadIsRefused()
    {
        if (OperatingSystem.IsWindows()) return;
        using Fixture fixture = new();
        string media = fixture.MediaWithPicture();
        File.SetUnixFileMode(media, UnixFileMode.None);
        try
        {
            bool readable;
            try { using IEnumerator<string> probe = Directory.EnumerateFileSystemEntries(media).GetEnumerator(); probe.MoveNext(); readable = true; }
            catch (UnauthorizedAccessException) { readable = false; }
            if (readable) return; // running with rights that read it anyway (root)
            Assert.Equal("profile.error.mediaUnavailable", fixture.Service().Validate("Cats", fixture.In("profiles", "cats"), fixture.In("d"), media));
        }
        finally { File.SetUnixFileMode(media, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void UnsafeLocationsAreRefusedInBothDirections()
    {
        using Fixture fixture = new();
        ProfileSwitchService service = fixture.Service();
        string profile = fixture.In("profiles", "cats"), downloads = fixture.In("Downloads", "MeowChan-cats");
        // Inside the new profile's download folder (it may exist already), and around the new profile and download folders (MeowChan
        // would create them inside the person's folder).
        Directory.CreateDirectory(Path.Combine(downloads, "sub"));
        Assert.Equal("profile.error.mediaUnsafe", service.Validate("Cats", profile, downloads, Path.Combine(downloads, "sub")));
        Directory.CreateDirectory(fixture.In("pics"));
        Assert.Equal("profile.error.mediaUnsafe", service.Validate("Dogs", fixture.In("pics", "profiles", "dogs"), fixture.In("pics", "downloads-dogs"), fixture.In("pics")));
        // Inside the folder of an existing profile.
        string existing = fixture.In("old");
        using (ProfileLease lease = ProfileLease.Acquire(existing)) { Config old = Config.LoadConfig(existing, lease); old.DownloadPath = fixture.In("old-downloads"); old.SaveConfig(existing); }
        Directory.CreateDirectory(Path.Combine(existing, "inner"));
        Assert.Equal("profile.error.mediaUnsafe", service.Validate("Old", existing, "", Path.Combine(existing, "inner")));
        // This window's profile and download folder, inside and around.
        Directory.CreateDirectory(fixture.CurrentDownloads);
        Directory.CreateDirectory(Path.Combine(fixture.Current, "inner"));
        string anyProfile = fixture.In("profiles", "owls"), anyDownloads = fixture.In("owl-downloads");
        foreach (string unsafeFolder in new[] { fixture.Current, Path.Combine(fixture.Current, "inner"), fixture.CurrentDownloads, fixture.In("Downloads") })
            Assert.Equal("profile.error.mediaUnsafe", service.Validate("Owls", anyProfile, anyDownloads, unsafeFolder));
        // A listed profile's folder and the folder of the profile list.
        string listed = fixture.In("listed");
        Directory.CreateDirectory(listed);
        fixture.Registry.Register(listed, "Listed", null, null, ProfileRegistry.DefaultLimit);
        Assert.Equal("profile.error.mediaUnsafe", service.Validate("Owls", anyProfile, anyDownloads, listed));
        Assert.Equal("profile.error.mediaUnsafe", service.Validate("Owls", anyProfile, anyDownloads, fixture.In("config")));
        // None of it created anything.
        Assert.False(Directory.Exists(anyProfile));
        Assert.Empty(fixture.Launcher.Started);
        Assert.NotEqual("profile.error.mediaUnsafe", Texts.T("profile.error.mediaUnsafe"));
    }

    [Fact]
    public void AnExistingProfileTakesAMediaFolderToo()
    {
        using Fixture fixture = new();
        string folder = fixture.In("old");
        using (ProfileLease lease = ProfileLease.Acquire(folder)) { Config old = Config.LoadConfig(folder, lease); old.DownloadPath = fixture.In("old-downloads"); old.SaveConfig(folder); }
        string media = fixture.MediaWithPicture();
        ProfileActionResult result = fixture.Service().Create(new NewProfileRequest("Old", folder, "", null, media));
        Assert.Equal(ProfileActionStatus.Started, result.Status);
        Assert.Equal((folder, media), fixture.Launcher.Started.Single());
        // Around its own profile folder is still refused.
        Assert.Equal("profile.error.mediaUnsafe", fixture.Service().Validate("Old", folder, "", fixture.Temp.Path));
    }

    [Fact]
    public void EveryNewTextExistsInEnglishAndGerman()
    {
        foreach (string key in new[] { "profile.new.media.option", "profile.new.media.folder", "profile.new.media.hint", "profile.new.chooseMedia",
            "profile.error.mediaAbsolute", "profile.error.mediaUnavailable", "profile.error.mediaUnsafe", "profile.startMedia.added",
            "profile.startMedia.refused", "profile.startMedia.badArgument", "profile.startMedia.needsProfile" })
        {
            Assert.NotEqual(key, Texts.T(key));
            Texts.SetLanguage("de");
            Assert.NotEqual(key, Texts.T(key));
            Assert.NotEqual(BuiltInCatalog.English[key], Texts.T(key));
            Texts.SetLanguage("en");
        }
    }

    // ---- Registration on the new process' first start ----

    private static async Task<(DesktopSessionHost Host, string Profile)> StartHostAsync(string root, string downloads)
    {
        string profile = Path.Combine(root, "newprofile");
        new Config { DownloadPath = downloads, DownloadsPaused = true, AutomaticUpdateChecks = false, FolderSourceBackgroundHashing = false }.SaveConfig(profile);
        DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        return (host, profile);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The folder source did not reach the expected state.");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task TheFirstStartRegistersTheFolderAsAReadOnlyFolderSourceAndNeverChangesIt()
    {
        using Fixture fixture = new();
        string media = fixture.MediaWithPicture("Holiday Photos");
        long ticks = File.GetLastWriteTimeUtc(Path.Combine(media, "a.png")).Ticks;
        DateTime folderTime = Directory.GetLastWriteTimeUtc(media);
        (DesktopSessionHost host, _) = await StartHostAsync(fixture.Temp.Path, fixture.In("downloads"));
        await using (host)
        {
            StartupMediaFolderResult result = await StartupMediaFolderRegistration.RegisterAsync(host,
                new StartupMediaFolderArgument(media, StartupMediaFolderProblem.None), Array.Empty<string>());
            Assert.Equal(StartupMediaFolderStatus.Added, result.Status);
            Assert.Equal("Holiday Photos", result.Name);
            Assert.Contains("Holiday Photos", StartupMediaFolderRegistration.Message(result));
            FolderSourceView view = Assert.Single(host.ListFolderSources());
            Assert.Equal((media, "Holiday Photos", true), (view.Root.Path, view.Root.Name, view.Root.IncludeSubfolders));
            await WaitAsync(() => host.ListFolderSources().Single().Counts.Files == 1 && !host.ListFolderSources().Single().Scanning);
            // The first look-through ran by itself; nothing in the folder changed.
            Assert.Equal(new[] { "a.png" }, Directory.GetFileSystemEntries(media).Select(Path.GetFileName));
            Assert.Equal(ticks, File.GetLastWriteTimeUtc(Path.Combine(media, "a.png")).Ticks);
            Assert.Equal(folderTime, Directory.GetLastWriteTimeUtc(media));
        }
    }

    [Fact]
    public async Task ARegisteredFolderIsNotRegisteredTwiceAndSaysNothing()
    {
        using Fixture fixture = new();
        string media = fixture.MediaWithPicture();
        (DesktopSessionHost host, _) = await StartHostAsync(fixture.Temp.Path, fixture.In("downloads"));
        await using (host)
        {
            StartupMediaFolderArgument argument = new(media, StartupMediaFolderProblem.None);
            Assert.Equal(StartupMediaFolderStatus.Added, (await StartupMediaFolderRegistration.RegisterAsync(host, argument, Array.Empty<string>())).Status);
            StartupMediaFolderResult again = await StartupMediaFolderRegistration.RegisterAsync(host, argument, Array.Empty<string>());
            Assert.Equal((StartupMediaFolderStatus.AlreadyAdded, (string?)null), (again.Status, again.MessageKey));
            Assert.Equal(string.Empty, StartupMediaFolderRegistration.Message(again));
            Assert.Single(host.ListFolderSources());
        }
    }

    [Fact]
    public async Task AnInvalidArgumentIsExplainedNothingIsCreatedAndTheStartGoesOn()
    {
        using Fixture fixture = new();
        string media = fixture.MediaWithPicture();
        string downloads = fixture.In("downloads");
        Directory.CreateDirectory(Path.Combine(downloads, "saved"));
        string missing = fixture.In("gone", "photos"), file = fixture.In("a-file.png");
        File.WriteAllBytes(file, Png);
        (DesktopSessionHost host, string profile) = await StartHostAsync(fixture.Temp.Path, downloads);
        await using (host)
        {
            async Task<StartupMediaFolderResult> Register(StartupMediaFolderArgument argument, params string[] others) =>
                await StartupMediaFolderRegistration.RegisterAsync(host, argument, others);
            StartupMediaFolderResult result = await Register(new(missing, StartupMediaFolderProblem.None));
            Assert.Equal((StartupMediaFolderStatus.Refused, "profile.startMedia.refused", "profile.error.mediaUnavailable"), (result.Status, result.MessageKey, result.ReasonKey));
            Assert.Contains("does not create it", StartupMediaFolderRegistration.Message(result));
            Assert.StartsWith("The media folder for this profile was not added.", StartupMediaFolderRegistration.Message(result));
            Assert.Equal("profile.error.mediaUnavailable", (await Register(new(file, StartupMediaFolderProblem.None))).ReasonKey);
            // Unsafe: inside the profile, inside or around the download folder, around the profile, or a known other profile.
            Assert.Equal("profile.error.mediaUnsafe", (await Register(new(profile, StartupMediaFolderProblem.None))).ReasonKey);
            Assert.Equal("profile.error.mediaUnsafe", (await Register(new(Path.Combine(downloads, "saved"), StartupMediaFolderProblem.None))).ReasonKey);
            Assert.Equal("profile.error.mediaUnsafe", (await Register(new(fixture.Temp.Path, StartupMediaFolderProblem.None))).ReasonKey);
            Assert.Equal("profile.error.mediaUnsafe", (await Register(new(media, StartupMediaFolderProblem.None), media)).ReasonKey);
            // The argument itself was unusable.
            Assert.Equal("profile.startMedia.badArgument", (await Register(new(null, StartupMediaFolderProblem.Malformed))).ReasonKey);
            Assert.Equal("profile.startMedia.needsProfile", (await Register(new(media, StartupMediaFolderProblem.NeedsProfile))).ReasonKey);
            foreach (string reason in new[] { "profile.startMedia.badArgument", "profile.startMedia.needsProfile", "profile.error.mediaUnsafe" })
                Assert.NotEqual(reason, Texts.T(reason));
            // Nothing was registered or created, and the host is still running normally.
            Assert.Empty(host.ListFolderSources());
            Assert.False(Directory.Exists(missing));
            Assert.False(Directory.Exists(fixture.In("gone")));
            Assert.Equal(DesktopSessionState.Ready, host.State);
        }
    }

    [Fact]
    public void TheSourceNameFollowsTheFolderAndStaysWithinTheCatalogLimit()
    {
        Assert.Equal("Holiday", StartupMediaFolderRegistration.NameOf(Path.Combine(Path.GetTempPath(), "Holiday") + Path.DirectorySeparatorChar));
        Assert.Equal(FolderSourceCatalog.MaximumNameLength, StartupMediaFolderRegistration.NameOf(Path.Combine(Path.GetTempPath(), new string('a', 200))).Length);
        Assert.Equal(Texts.T("folderSource.section.title"), StartupMediaFolderRegistration.NameOf(Path.GetPathRoot(Path.GetTempPath())!));
    }
}
