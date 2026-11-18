using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>
/// PR1 (profiles): the <c>--profile</c> argument and its precedence, the start of a second process for another profile, and
/// the rules of "New profile" / "Switch profile". All folders are private temporary ones; no process is started (a
/// recording launcher stands in) and the real profile and the real profile list are never touched.
/// </summary>
[Collection("Localization")]
public sealed class ProfileSwitchTests
{
    private sealed class RecordingLauncher : IProfileLauncher
    {
        internal readonly List<string> Started = new();
        internal readonly List<string?> MediaFolders = new();
        internal Exception? Failure;
        public void Launch(string profileDirectory, string? mediaFolder = null)
        { if (Failure != null) throw Failure; Started.Add(profileDirectory); MediaFolders.Add(mediaFolder); }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly TempDirectory Temp = new();
        internal readonly RecordingLauncher Launcher = new();
        internal string Current => Path.Combine(Temp.Path, "current");
        internal string CurrentDownloads => Path.Combine(Temp.Path, "Downloads", "MeowChan");
        internal ProfileRegistry Registry => new(Path.Combine(Temp.Path, "config", "MeowChan", ProfileRegistry.FileName));
        internal int Limit = ProfileRegistry.DefaultLimit;
        internal Fixture() { Directory.CreateDirectory(Current); File.WriteAllText(Path.Combine(Current, "settings.json"), "{}"); }
        internal ProfileSwitchService Service(bool withRegistry = true) =>
            new(withRegistry ? Registry : null, Launcher, Current, CurrentDownloads, () => Limit);
        internal string In(params string[] parts) => Path.Combine([Temp.Path, .. parts]);
        public void Dispose() => Temp.Dispose();
    }

    [Fact]
    public void TheProfileArgumentIsRemovedAndEveryOtherArgumentPassesThrough()
    {
        string folder = Path.Combine(Path.GetTempPath(), "meowchan-profile-arg");
        Assert.Equal(new[] { "-psn_0_1", "--other" }, ProfileCommandLine.Extract(new[] { "-psn_0_1", "--profile", folder + Path.DirectorySeparatorChar, "--other" }, out string? profile));
        Assert.Equal(folder, profile);
        Assert.Empty(ProfileCommandLine.Extract(new[] { "--profile=" + folder }, out profile));
        Assert.Equal(folder, profile);
        Assert.Equal(new[] { "x" }, ProfileCommandLine.Extract(new[] { "x" }, out profile));
        Assert.Null(profile);
        // A relative folder is made absolute against the working directory of the start.
        ProfileCommandLine.Extract(new[] { "--profile", "relative-profile" }, out profile);
        Assert.Equal(Path.GetFullPath("relative-profile"), profile);
    }

    [Theory]
    [InlineData("--profile")]
    [InlineData("--profile", "--smoke-startup-seconds")]
    [InlineData("--profile=")]
    [InlineData("--profile", "a", "--profile", "b")]
    public void AProfileArgumentWithoutAFolderOrGivenTwiceIsRefused(params string[] args) =>
        Assert.Throws<ArgumentException>(() => ProfileCommandLine.Extract(args, out _));

    [Fact]
    public void TheArgumentWinsOverTheDefaultAndMustAgreeWithTheEnvironmentVariables()
    {
        string profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "meowchan-arg-profile"));
        string other = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "meowchan-env-profile"));
        Assert.Equal(profile, Config.ResolveProfileOverrides(profile, null, null));
        Assert.Equal(profile, Config.ResolveProfileOverrides(profile, profile + Path.DirectorySeparatorChar, null));
        Assert.Equal(profile, Config.ResolveProfileOverrides(profile, null, profile));
        Assert.Equal(other, Config.ResolveProfileOverrides(null, other, null));
        Assert.Equal(other, Config.ResolveProfileOverrides("  ", null, other));
        Assert.Null(Config.ResolveProfileOverrides(null, null, null));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Config.ResolveProfileOverrides(profile, other, null));
        Assert.Contains("--profile", error.Message);
        Assert.Contains("MEOWCHAN_PROFILE", error.Message);
        Assert.Throws<InvalidOperationException>(() => Config.ResolveProfileOverrides(profile, null, other));
        // The two variables keep their own former rule.
        Assert.Throws<InvalidOperationException>(() => Config.ResolveProfileOverrides(null, profile, other));
    }

    [Fact]
    public void OnlyAProfileChosenByAnEnvironmentVariableStaysOutOfTheList()
    {
        Assert.True(ProfileStartup.SelectedByEnvironment("/p", null, null));
        Assert.True(ProfileStartup.SelectedByEnvironment(null, "/p", null));
        Assert.False(ProfileStartup.SelectedByEnvironment("/p", null, "/p"));
        Assert.False(ProfileStartup.SelectedByEnvironment(null, null, null));
        Assert.False(ProfileStartup.SelectedByEnvironment("", " ", null));
        // Nothing but the app's entry point switches recording on.
        Assert.False(ProfileStartup.RecordsInProfileList);
    }

    [Fact]
    public void TheSecondProcessGetsTheFolderAsArgumentAndNoProfileVariable()
    {
        string folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "meowchan-child-profile"));
        string executable = Path.GetFullPath(Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "MeowChan.exe" : "MeowChan"));
        var info = ProcessProfileLauncher.CreateStartInfo(folder + Path.DirectorySeparatorChar, executable, "/x/MeowChan.dll", null);
        Assert.Equal(executable, info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(new[] { "--profile", folder }, info.ArgumentList);
        Assert.False(info.Environment.ContainsKey("MEOWCHAN_PROFILE"));
        Assert.False(info.Environment.ContainsKey("OCHAN_PROFILE"));
        // A framework-dependent start passes the app assembly first.
        string dotnet = Path.GetFullPath(Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        info = ProcessProfileLauncher.CreateStartInfo(folder, dotnet, "/x/MeowChan.dll", null);
        Assert.Equal(new[] { "/x/MeowChan.dll", "--profile", folder }, info.ArgumentList);
        Assert.Throws<ArgumentException>(() => ProcessProfileLauncher.CreateStartInfo("relative", executable, null, null));
    }

    [Fact]
    public void AnAppImageIsRestartedThroughItsImage()
    {
        using TempDirectory temp = new();
        string image = Path.Combine(temp.Path, "MeowChan.AppImage");
        File.WriteAllText(image, "");
        string folder = Path.Combine(temp.Path, "p");
        var info = ProcessProfileLauncher.CreateStartInfo(folder, "/tmp/.mount_x/usr/lib/meowchan/MeowChan", null, image);
        Assert.Equal(image, info.FileName);
        Assert.Equal(new[] { "--profile", folder }, info.ArgumentList);
    }

    [Fact]
    public void FoldersAreClassifiedWithoutWritingAnything()
    {
        using TempDirectory temp = new();
        string missing = Path.Combine(temp.Path, "missing"), empty = Path.Combine(temp.Path, "empty"),
            profile = Path.Combine(temp.Path, "profile"), foreign = Path.Combine(temp.Path, "pictures"), file = Path.Combine(temp.Path, "file");
        Directory.CreateDirectory(empty); Directory.CreateDirectory(profile); Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(profile, ".meowchan-profile.protocol"), "x");
        File.WriteAllText(Path.Combine(foreign, "cat.png"), "x");
        File.WriteAllText(file, "x");
        Assert.Equal(ProfileFolderKind.New, ProfileOpenPolicy.Classify(missing));
        Assert.Equal(ProfileFolderKind.New, ProfileOpenPolicy.Classify(empty));
        Assert.Equal(ProfileFolderKind.Existing, ProfileOpenPolicy.Classify(profile));
        Assert.Equal(ProfileFolderKind.Foreign, ProfileOpenPolicy.Classify(foreign));
        Assert.Equal(ProfileFolderKind.Unusable, ProfileOpenPolicy.Classify(file));
        Assert.False(Directory.Exists(missing));
        Assert.Empty(Directory.GetFileSystemEntries(empty));
    }

    [Fact]
    public void SuggestionsUseSafeFolderNamesAndNeverAnExistingFolder()
    {
        using TempDirectory temp = new();
        Assert.Equal("My-Cats-_2", ProfileOpenPolicy.Slug("  My Cats/_2 "));
        Assert.Equal("profile", ProfileOpenPolicy.Slug("../.."));
        Assert.Equal(40, ProfileOpenPolicy.Slug(new string('a', 100)).Length);
        Directory.CreateDirectory(Path.Combine(temp.Path, "cats"));
        Assert.Equal(Path.Combine(temp.Path, "cats-2"), ProfileOpenPolicy.FreeFolder(temp.Path, "cats"));
        string downloads = Path.Combine(temp.Path, "Downloads", "MeowChan");
        Assert.Equal(Path.Combine(temp.Path, "Downloads", "MeowChan-cats"), ProfileOpenPolicy.SuggestDownloadFolder(downloads, "cats"));
        Assert.False(ProfileOpenPolicy.Overlaps(downloads, Path.Combine(temp.Path, "Downloads", "MeowChan-cats")));
        Assert.True(ProfileOpenPolicy.Overlaps(downloads, Path.Combine(downloads, "sub")));
        Assert.True(ProfileOpenPolicy.Overlaps(Path.Combine(downloads, "sub"), downloads));
    }

    [Fact]
    public void ANewProfileGetsItsOwnDownloadFolderLanguageAndIdAndIsListedWithTheCurrentOne()
    {
        using Fixture fixture = new();
        ProfileSwitchService service = fixture.Service();
        string folder = fixture.In("profiles", "cats"), downloads = fixture.In("Downloads", "MeowChan-cats");
        ProfileActionResult result = service.Create(new NewProfileRequest("Cats", folder, downloads, "de"));
        Assert.Equal(ProfileActionStatus.Started, result.Status);
        Assert.Null(result.ListNoteKey);
        Assert.Equal(new[] { folder }, fixture.Launcher.Started);
        // The child finds settings with its own download folder and the first-run setup still pending.
        Config settings = Config.LoadConfig(folder);
        Assert.Equal(downloads, settings.DownloadPath);
        Assert.Equal("de", settings.Language);
        Assert.True(settings.FirstRunSetupPending);
        Assert.Equal(ProfileOrigin.ExistingValid, settings.Origin);
        ProfileInstanceIdentity identity = ProfileInstanceIdentity.TryRead(folder)!;
        Assert.False(ProfileLease.IsInUse(folder));
        // Nothing was written into the current profile or the download folders.
        Assert.Equal(new[] { "settings.json" }, Directory.GetFileSystemEntries(fixture.Current).Select(Path.GetFileName));
        Assert.False(Directory.Exists(downloads));
        ProfileRegistryEntry[] entries = fixture.Registry.Read().Entries.ToArray();
        Assert.Equal(new[] { fixture.Current, folder }, entries.Select(entry => entry.Path));
        Assert.Equal("current", entries[0].Name);
        Assert.Equal(("Cats", (Guid?)identity.Id, (DateTimeOffset?)null), (entries[1].Name, entries[1].Id, entries[1].LastOpenedUtc));
    }

    [Fact]
    public void AnExistingProfileFolderIsOpenedAsItIs()
    {
        using Fixture fixture = new();
        string folder = fixture.In("old");
        using (ProfileLease lease = ProfileLease.Acquire(folder))
        {
            Config old = Config.LoadConfig(folder, lease);
            old.DownloadPath = fixture.In("old-downloads");
            old.SaveConfig(folder);
        }
        byte[] before = File.ReadAllBytes(Path.Combine(folder, "settings.json"));
        ProfileActionResult result = fixture.Service().Create(new NewProfileRequest("Old", folder, "", null));
        Assert.Equal(ProfileActionStatus.Started, result.Status);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(folder, "settings.json")));
        Assert.Null(ProfileInstanceIdentity.TryRead(folder));
        Assert.Contains(fixture.Registry.Read().Entries, entry => entry.Path == folder && entry.Name == "Old");
    }

    [Fact]
    public void NewProfileRefusesEveryUnsafeFolderBeforeWriting()
    {
        using Fixture fixture = new();
        ProfileSwitchService service = fixture.Service();
        string foreign = fixture.In("pictures");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "cat.png"), "x");
        string fresh = fixture.In("fresh"), downloads = fixture.In("dl-fresh");
        Assert.Equal("profile.error.nameRequired", service.Validate(" ", fresh, downloads));
        Assert.Equal("profile.error.folderAbsolute", service.Validate("A", "relative", downloads));
        Assert.Equal("profile.error.folderCurrent", service.Validate("A", fixture.Current + Path.DirectorySeparatorChar, downloads));
        Assert.Equal("profile.error.folderNested", service.Validate("A", Path.Combine(fixture.Current, "inner"), downloads));
        Assert.Equal("profile.error.folderNested", service.Validate("A", fixture.Temp.Path, downloads));
        Assert.Equal("profile.error.folderNested", service.Validate("A", fixture.In("config"), downloads));
        Assert.Equal("profile.error.folderForeign", service.Validate("A", foreign, downloads));
        Assert.Equal("profile.error.downloadsAbsolute", service.Validate("A", fresh, "relative"));
        Assert.Equal("profile.error.downloadsShared", service.Validate("A", fresh, fixture.CurrentDownloads));
        Assert.Equal("profile.error.downloadsShared", service.Validate("A", fresh, Path.Combine(fixture.CurrentDownloads, "sub")));
        Assert.Equal("profile.error.downloadsShared", service.Validate("A", fresh, fixture.In("Downloads")));
        Assert.Equal("profile.error.downloadsShared", service.Validate("A", fresh, Path.Combine(fixture.Current, "dl")));
        Assert.Equal("profile.error.downloadsShared", service.Validate("A", fresh, fresh));
        Assert.Null(service.Validate("A", fresh, downloads));
        ProfileActionResult refused = service.Create(new NewProfileRequest("A", foreign, downloads, null));
        Assert.Equal((ProfileActionStatus.Refused, "profile.error.folderForeign"), (refused.Status, refused.MessageKey));
        Assert.Equal(new[] { "cat.png" }, Directory.GetFileSystemEntries(foreign).Select(Path.GetFileName));
        Assert.Empty(fixture.Launcher.Started);
        Assert.Equal(ProfileRegistryState.Missing, fixture.Registry.Read().State);
    }

    [Fact]
    public void OneWindowPerProfileTheCurrentAndAnOpenProfileAreNotStartedAgain()
    {
        using Fixture fixture = new();
        ProfileSwitchService service = fixture.Service();
        Assert.Equal(ProfileActionStatus.Current, service.Open(fixture.Current).Status);
        string other = fixture.In("other");
        using (ProfileLease lease = ProfileLease.Acquire(other))
        {
            ProfileActionResult busy = service.Open(other);
            Assert.Equal((ProfileActionStatus.InUse, "profile.status.inUse"), (busy.Status, busy.MessageKey));
            ProfileActionResult viaNew = service.Create(new NewProfileRequest("Other", other, "", null));
            Assert.Equal(ProfileActionStatus.InUse, viaNew.Status);
        }
        Assert.Empty(fixture.Launcher.Started);
        Assert.Equal(ProfileActionStatus.Started, service.Open(other).Status);
        Assert.Equal(new[] { other }, fixture.Launcher.Started);
    }

    [Fact]
    public void AMissingOrForeignListedFolderIsNeitherCreatedNorStarted()
    {
        using Fixture fixture = new();
        ProfileSwitchService service = fixture.Service();
        string gone = fixture.In("gone");
        ProfileActionResult missing = service.Open(gone);
        Assert.Equal((ProfileActionStatus.Refused, "profile.status.missing"), (missing.Status, missing.MessageKey));
        Assert.False(Directory.Exists(gone));
        string foreign = fixture.In("documents");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "a.txt"), "x");
        Assert.Equal("profile.status.notProfile", service.Open(foreign).MessageKey);
        Assert.Empty(fixture.Launcher.Started);
    }

    [Fact]
    public void AFullOrUnavailableListStillOpensTheProfileAndSaysSo()
    {
        using Fixture fixture = new();
        fixture.Limit = 1;
        ProfileActionResult full = fixture.Service().Create(new NewProfileRequest("Full", fixture.In("full"), fixture.In("dl-full"), null));
        Assert.Equal(ProfileActionStatus.Started, full.Status);
        Assert.Equal(("profile.note.listFull", (object?)1), (full.ListNoteKey, full.ListNoteArgument));
        Assert.Equal(new[] { fixture.Current }, fixture.Registry.Read().Entries.Select(entry => entry.Path));
        ProfileActionResult unlisted = fixture.Service(withRegistry: false).Create(new NewProfileRequest("Free", fixture.In("free"), fixture.In("dl-free"), null));
        Assert.Equal((ProfileActionStatus.Started, "profile.note.listUnavailable"), (unlisted.Status, unlisted.ListNoteKey));
    }

    [Fact]
    public void AFailedStartIsReportedAndForgetNeverTouchesTheCurrentProfile()
    {
        using Fixture fixture = new();
        fixture.Launcher.Failure = new System.ComponentModel.Win32Exception("no such file");
        ProfileSwitchService service = fixture.Service();
        string other = fixture.In("other");
        using (ProfileLease lease = ProfileLease.Acquire(other)) { }
        ProfileActionResult failed = service.Open(other);
        Assert.Equal((ProfileActionStatus.Failed, "profile.status.startFailed"), (failed.Status, failed.MessageKey));
        Assert.Equal(ProfileRegistryChange.Added, service.RecordCurrent(Guid.NewGuid()));
        Assert.False(service.Forget(fixture.Current));
        Assert.Single(fixture.Registry.Read().Entries);
    }

    [Fact]
    public void TheProfileCommandsAreAppMenuCommandsWithoutDefaultGestures()
    {
        foreach (string id in new[] { "profile.new", "profile.switch", "profile.open-folder" })
        {
            CommandDefinition command = CommandCatalog.Get(id);
            Assert.Null(command.Shortcut);
            Assert.Equal(CommandScope.Application, command.Scope);
            Assert.Contains("app-menu", command.Contexts);
        }
    }

    [Fact]
    public void EveryProfileTextExistsInEveryBundledLanguage()
    {
        string[] keys = ProfileCatalog.Entries.Select(entry => entry.Key).ToArray();
        foreach (string key in keys)
        {
            Assert.True(BuiltInCatalog.English.ContainsKey(key), key);
            Assert.True(BuiltInCatalog.German.ContainsKey(key), key);
        }
        foreach (string code in Texts.BundledPackCodes())
        {
            string previous = Texts.Language;
            try
            {
                Texts.SetLanguage(code);
                foreach (string key in keys) Assert.NotEqual(key, Texts.T(key));
            }
            finally { Texts.SetLanguage(previous); }
        }
    }
}
