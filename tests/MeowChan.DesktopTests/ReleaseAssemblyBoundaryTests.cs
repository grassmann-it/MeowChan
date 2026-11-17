using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace MeowChan.DesktopTests;

public sealed class ReleaseAssemblyBoundaryTests
{
    [Fact]
    public void ProductAssemblyContainsAcceptanceHarnessOnlyInDebug()
    {
        // Read the actual referenced product binary, including generated/nested types. Metadata avoids
        // loading native UI dependencies and makes an absent type impossible to hide behind load errors.
        using FileStream file = File.OpenRead(typeof(App).Assembly.Location);
        using PEReader pe = new(file);
        MetadataReader metadata = pe.GetMetadataReader();
        string[] names = metadata.TypeDefinitions.Select(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Name)).ToArray();
#if DEBUG
        Assert.Contains("ApphostStartupSmoke", names); // Positive control: the same check sees the Debug harness.
#else
        Assert.DoesNotContain(names, name => name.Contains("Smoke", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Apphost", StringComparison.OrdinalIgnoreCase));
        string[] methods = metadata.MethodDefinitions.Select(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name)).ToArray();
        Assert.DoesNotContain("RunDevelopmentHost", methods); // Even the optional hook is erased.
        Assert.DoesNotContain(methods, name => name.Contains("Smoke", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("MainWindowFactory", StringComparison.Ordinal));
#endif
    }
}
