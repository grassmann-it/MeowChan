using System.Diagnostics;

namespace MeowChan.DesktopTests;

public sealed class Released102CycleEnvironmentTests
{
    [Fact]
    public void EveryCycleGetsFreshExistingDirectoriesOutsideTheReleasedProfile()
    {
        using TempDirectory temp = new();
        string result = Path.Combine(temp.Path, "cycle-0.json");
        IReadOnlyDictionary<string, string> first = Released102CycleEnvironment.Create(result);
        IReadOnlyDictionary<string, string> next = Released102CycleEnvironment.Create(Path.Combine(temp.Path, "cycle-1.json"));
        Assert.Equal(6, first.Count);
        Assert.Equal(first["HOME"], next["HOME"]);
        Assert.EndsWith(Path.Combine(temp.Path, "replay-home"), first["HOME"]);
        Assert.NotEqual(first["DOTNET_CLI_HOME"], next["DOTNET_CLI_HOME"]);
        Assert.NotEqual(first["TMPDIR"], next["TMPDIR"]);
        Assert.NotEqual(first["XDG_CONFIG_HOME"], next["XDG_CONFIG_HOME"]);
        Assert.NotEqual(first["XDG_STATE_HOME"], next["XDG_STATE_HOME"]);
        Assert.NotEqual(first["XDG_CACHE_HOME"], next["XDG_CACHE_HOME"]);
        foreach (string directory in first.Values)
            Assert.True(Directory.Exists(directory));
        Assert.True(Directory.Exists(first["HOME"]));
        Assert.StartsWith(result + ".environment" + Path.DirectorySeparatorChar, first["DOTNET_CLI_HOME"]);
        Assert.Throws<IOException>(() => Released102CycleEnvironment.Create(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateEnvironmentOverridesInheritedHomeAndIsExplicitForSudoBubblewrap(bool bubblewrap)
    {
        using TempDirectory temp = new();
        IReadOnlyDictionary<string, string> environment = Released102CycleEnvironment.Create(Path.Combine(temp.Path, "cycle-0.json"));
        ProcessStartInfo start = new("fixture");
        start.Environment["HOME"] = "/root";
        Released102CycleEnvironment.Apply(start, environment, bubblewrap);
        foreach ((string key, string value) in environment) Assert.Equal(value, start.Environment[key]);
        Assert.Equal(bubblewrap ? environment.Count * 3 : 0, start.ArgumentList.Count);
        if (bubblewrap)
            foreach (string key in environment.Keys)
            {
                int index = start.ArgumentList.IndexOf(key);
                Assert.Equal("--setenv", start.ArgumentList[index - 1]);
                Assert.Equal(environment[key], start.ArgumentList[index + 1]);
            }
    }
}
