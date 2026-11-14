using System.Text.Json;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// PT5: the stamp set is kept in settings.json as a view preference (Config.TagStamp, names in stamp order) until the tagging queue
// file carries it. A damaged value must never cost the other settings.
public sealed class QuickTagStampConfigTests
{
    private sealed class Profile : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("meowchan-stamp-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }

    [Fact]
    public void TheStampNamesRoundTripThroughTheSettingsFileAndCloneAndCopyAreIndependent()
    {
        using Profile profile = new();
        Config config = Config.LoadConfig(profile.Path);
        Assert.Empty(config.TagStamp); // an older profile has no stamp
        config.TagStamp = new[] { "surface", "wip", "/g/" };

        Config clone = config.Clone();
        clone.TagStamp[0] = "changed";
        Assert.Equal("surface", config.TagStamp[0]);
        Config copy = new();
        copy.CopyFrom(config);
        copy.TagStamp[1] = "changed";
        Assert.Equal("wip", config.TagStamp[1]);

        config.SaveConfig(profile.Path);
        Assert.Equal(new[] { "surface", "wip", "/g/" }, Config.LoadConfig(profile.Path).TagStamp);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile.Path, "settings.json")));
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("TagStamp").ValueKind);
    }

    [Theory]
    [InlineData("null", 0)]
    [InlineData("7", 0)]
    [InlineData("\"surface\"", 0)]
    [InlineData("{\"a\":1}", 0)]
    [InlineData("[\"a\",5,null,[\"x\"],{\"k\":1},\"b\"]", 2)]
    public void ADamagedStampValueNeverDiscardsTheOtherSettings(string value, int kept)
    {
        using Profile profile = new();
        File.WriteAllText(Path.Combine(profile.Path, "settings.json"), "{\"VideoVolume\":29,\"TagStamp\":" + value + "}");
        Config config = Config.LoadConfig(profile.Path);

        Assert.Null(config.LoadWarning);
        Assert.Equal(29, config.VideoVolume);
        Assert.Equal(kept, config.TagStamp.Length);
        Assert.Equal(kept == 2 ? new[] { "a", "b" } : Array.Empty<string>(), config.TagStamp);
    }

    [Fact]
    public void AStampOfAnyLengthReadsAtMostTheItemCeilingAndTheModelCutsItToTheStampSize()
    {
        using Profile profile = new();
        string names = string.Join(",", Enumerable.Range(0, 200).Select(index => "\"t" + index + "\""));
        File.WriteAllText(Path.Combine(profile.Path, "settings.json"), "{\"TagStamp\":[" + names + "]}");
        Config config = Config.LoadConfig(profile.Path);

        Assert.Equal(DesktopPreferenceNameListConverter.MaximumItems, config.TagStamp.Length);
        QuickTagStamp stamp = new QuickTagOptions { StampSize = 5 }.CreateStamp(config.TagStamp);
        Assert.Equal(new[] { "t0", "t1", "t2", "t3", "t4" }, stamp.ToNames());
        config.TagStamp = stamp.ToNames().ToArray();
        Assert.Equal(5, config.Clone().TagStamp.Length);
    }
}
