using System.Runtime.CompilerServices;

namespace MeowChan.DesktopTests.DesignLint;

// XML/source contracts only. No Avalonia application or native layout proof.
public sealed class SourcesPluginsInsetContractTests
{
    private const string Source = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:local="clr-namespace:MeowChan" x:Class="MeowChan.SourcesPage">
          <local:PluginsPage x:Name="SourcePluginManagement">
            <local:PluginsPage.SourceContent>
              <ScrollViewer x:Name="SourcesScroll"><StackPanel /></ScrollViewer>
            </local:PluginsPage.SourceContent>
          </local:PluginsPage>
        </UserControl>
        """;
    private const string Owner = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="MeowChan.PluginsPage">
          <Grid Margin="{StaticResource Workspace.Inset.Page}"><ContentControl x:Name="SourceContentHost" /></Grid>
        </UserControl>
        """;

    [Fact]
    public void MergedSourcesInheritsExactlyOneInsetFromItsRealManagementOwner()
    {
        Assert.Empty(InsetViolations(Source, Owner));
        Assert.Empty(InsetViolations(Read(DesignLintScanner.MergedSourcesFile), Read(DesignLintScanner.PluginManagementFile)));
    }

    [Theory]
    [InlineData("missing-owner")]
    [InlineData("owner-inset")]
    [InlineData("owner-class")]
    [InlineData("content-host")]
    [InlineData("detached-content-host")]
    [InlineData("duplicate-slot")]
    [InlineData("embedding")]
    [InlineData("source-slot")]
    [InlineData("standalone")]
    [InlineData("double-inset")]
    public void BrokenCompositionAndMissingOrDoubledInsetsRemainViolations(string mutation)
    {
        string source = Source;
        string? owner = Owner;
        switch (mutation)
        {
            case "missing-owner": owner = null; break;
            case "owner-inset": owner = Owner.Replace("{StaticResource Workspace.Inset.Page}", "12", StringComparison.Ordinal); break;
            case "owner-class": owner = Owner.Replace("MeowChan.PluginsPage", "MeowChan.OtherPage", StringComparison.Ordinal); break;
            case "content-host": owner = Owner.Replace("SourceContentHost", "OtherContentHost", StringComparison.Ordinal); break;
            case "detached-content-host": owner = Owner.Replace("<ContentControl x:Name=\"SourceContentHost\" /></Grid>",
                "</Grid><ContentControl x:Name=\"SourceContentHost\" />", StringComparison.Ordinal); break;
            case "duplicate-slot": source = Source.Replace("</local:PluginsPage>",
                "<local:PluginsPage.SourceContent /></local:PluginsPage>", StringComparison.Ordinal); break;
            case "embedding": source = Source.Replace("local:PluginsPage", "local:Border", StringComparison.Ordinal); break;
            case "source-slot": source = Source.Replace("PluginsPage.SourceContent", "PluginsPage.Tag", StringComparison.Ordinal); break;
            case "standalone": source = "<UserControl><ScrollViewer><StackPanel Margin=\"{StaticResource Workspace.Inset.Page}\" /></ScrollViewer></UserControl>"; break;
            case "double-inset": source = Source.Replace("<StackPanel />", "<StackPanel Margin=\"12\" />", StringComparison.Ordinal); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.NotEmpty(InsetViolations(source, owner));
    }

    private static DesignViolation[] InsetViolations(string source, string? owner) =>
        DesignLintScanner.ScanFile(DesignLintScanner.MergedSourcesFile, source, mergedInsetOwner: owner)
            .Where(violation => violation.Rule == DesignRules.PageInset).ToArray();

    private static string Read(string path, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../../..", path)));
}
