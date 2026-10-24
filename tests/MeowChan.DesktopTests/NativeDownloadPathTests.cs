namespace MeowChan.DesktopTests;

public sealed class NativeDownloadPathTests
{
    public static IEnumerable<object[]> NativeAbsolutePaths()
    {
        yield return new object[] { Path.Combine(Path.GetTempPath(), "absent fixture ü literal%20") };
        // Metadata/config only: no directory/network access to the UNC destination.
        if (OperatingSystem.IsWindows()) yield return new object[] { @"\\fixture-server\share\folder ü literal%20" };
    }

    [Theory]
    [MemberData(nameof(NativeAbsolutePaths))]
    public void AbsoluteNativePathSurvivesDraftValidationAndProfileReloadWithoutUriDecoding(string path)
        => CheckDraftAndReload(path, path);

    [Fact]
    public void RelativePathKeepsLiteralPercentAndUnicodeAcrossProfileReload()
    {
        string raw = "w23-" + Guid.NewGuid().ToString("N") + " ü literal%20";
        CheckDraftAndReload(raw, Path.Combine(Environment.CurrentDirectory, raw));
    }

    private static void CheckDraftAndReload(string raw, string expected)
    {
        using TempDirectory profile = new();
        Config active = Config.LoadConfig(profile.Path);
        active.DownloadPath = Path.Combine(profile.Path, "original");
        active.SaveConfig();
        string original = active.DownloadPath;
        Config draft = active.Clone();
        draft.DownloadPath = raw;
        draft.Validate();
        Assert.Equal(raw, draft.DownloadPath);
        Assert.Equal(original, active.DownloadPath);
        Assert.Equal(original, Config.LoadConfig(profile.Path).DownloadPath);
        draft.SaveConfig();
        Assert.Equal(expected, Config.LoadConfig(profile.Path).DownloadPath);
        Assert.Equal(original, active.DownloadPath);
    }
}
