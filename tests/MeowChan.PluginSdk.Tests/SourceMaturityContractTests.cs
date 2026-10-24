using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Tests;

public sealed class SourceMaturityContractTests
{
    [Fact]
    public void ExistingConstructorAndDeconstructionKeepTheirOriginalShapeAndDefault()
    {
        SourceDescription source = TestContracts.Source();
        var (sourceId, name, baseUri, capabilities, routes, httpHosts, settings, postOrder) = source;

        Assert.Equal(SourceMaturity.Unspecified, source.Maturity);
        Assert.Equal("synthetic", sourceId);
        Assert.Equal(source.Name, name);
        Assert.Equal(source.BaseUri, baseUri);
        Assert.Equal(source.Capabilities, capabilities);
        Assert.Same(source.Routes, routes);
        Assert.Same(source.HttpHosts, httpHosts);
        Assert.Same(source.Settings, settings);
        Assert.Equal(PostOrderKind.StableOrdinal, postOrder);
        Assert.Empty(SdkValidation.ValidateDescription(TestContracts.Plugin(source)));
    }

    [Fact]
    public void LegacyDescriptionWithoutMaturityDefaultsToUnspecified()
    {
        JsonObject json = JsonNode.Parse(SdkJson.Serialize(TestContracts.Source()))!.AsObject();
        Assert.True(json.Remove("maturity"));

        SourceDescription source = SdkJson.Deserialize<SourceDescription>(Encoding.UTF8.GetBytes(json.ToJsonString()));

        Assert.Equal(SourceMaturity.Unspecified, source.Maturity);
        Assert.Empty(SdkValidation.ValidateDescription(TestContracts.Plugin(source)));
    }

    [Theory]
    [InlineData(SourceMaturity.Unspecified)]
    [InlineData(SourceMaturity.Experimental)]
    public void KnownMaturityRoundTripsAsAnAdditiveStringProperty(SourceMaturity maturity)
    {
        PluginDescription plugin = TestContracts.Plugin(TestContracts.Source() with { Maturity = maturity });
        byte[] json = SdkJson.Serialize(plugin);

        PluginDescription copy = SdkJson.Deserialize<PluginDescription>(json);

        Assert.Equal(maturity, Assert.Single(copy.Sources).Maturity);
        Assert.Contains("\"maturity\":\"" + maturity + "\"", Encoding.UTF8.GetString(json));
        Assert.Empty(SdkValidation.ValidateDescription(copy));
        Assert.Equal(SdkContract.Version, copy.SdkVersion);
    }

    [Fact]
    public void UnknownMaturityIsRejectedByDescriptionValidation()
    {
        SourceDescription source = TestContracts.Source() with { Maturity = (SourceMaturity)42 };

        Assert.Contains(SdkValidation.ValidateDescription(TestContracts.Plugin(source)),
            issue => issue.Path == "plugin.sources[0].maturity" && issue.Code == "unknown_value");
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"FutureMaturity\"")]
    public void UnknownOrNumericWireMaturityIsRejected(string value)
    {
        JsonObject json = JsonNode.Parse(SdkJson.Serialize(TestContracts.Source()))!.AsObject();
        json["maturity"] = JsonNode.Parse(value);

        Assert.Throws<JsonException>(() => SdkJson.Deserialize<SourceDescription>(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }
}
