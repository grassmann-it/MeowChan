using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan.SourceAdapter.Tests;

public sealed class PluginSourceCapabilityMappingTests
{
    private const ProviderCapabilities AllKnown = ProviderCapabilities.ThreadWatch | ProviderCapabilities.BoardCatalog
        | ProviderCapabilities.PostCapture | ProviderCapabilities.OriginalMedia | ProviderCapabilities.Previews;

    [Theory]
    [InlineData(SourceCapabilities.None, ProviderCapabilities.None)]
    [InlineData(SourceCapabilities.ThreadRead, ProviderCapabilities.ThreadWatch)]
    [InlineData(SourceCapabilities.BoardCatalog, ProviderCapabilities.BoardCatalog)]
    [InlineData(SourceCapabilities.PostCapture, ProviderCapabilities.PostCapture)]
    [InlineData(SourceCapabilities.OriginalMedia, ProviderCapabilities.OriginalMedia)]
    [InlineData(SourceCapabilities.Previews, ProviderCapabilities.Previews)]
    public void EachKnownFlagMapsToItsDescriptorCounterpart(SourceCapabilities sdk, ProviderCapabilities expected) =>
        Assert.Equal(expected, PluginSourceImageBoard.MapCapabilities(sdk));

    [Fact]
    public void PostSearchHasNoDescriptorCounterpartAndIsDropped()
    {
        Assert.Equal(ProviderCapabilities.None, PluginSourceImageBoard.MapCapabilities(SourceCapabilities.PostSearch));
        Assert.Equal(ProviderCapabilities.Previews | ProviderCapabilities.OriginalMedia,
            PluginSourceImageBoard.MapCapabilities(SourceCapabilities.PostSearch | SourceCapabilities.Previews | SourceCapabilities.OriginalMedia));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(1 << 20)]
    [InlineData(-1)]
    public void UnknownFutureSdkFlagsNeverLeakAsUndefinedValues(int raw)
    {
        ProviderCapabilities mapped = PluginSourceImageBoard.MapCapabilities((SourceCapabilities)raw);
        Assert.Equal(mapped & AllKnown, mapped);
        if (raw == -1) Assert.Equal(AllKnown, mapped); // every known SDK flag set, nothing else
        else Assert.Equal(ProviderCapabilities.None, mapped);
    }
}
