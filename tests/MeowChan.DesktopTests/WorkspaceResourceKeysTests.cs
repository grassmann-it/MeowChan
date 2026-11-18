using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

// Avalonia compiles duplicate resource keys but throws while the compiled styles are loaded.
// Check each dictionary separately: the same key in Dark and Light is intentional and legal.
public sealed class WorkspaceResourceKeysTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void WorkspaceStylesHaveNoRepeatedKeysInTheSameResourceScope()
    {
        XDocument document = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml"));
        Assert.Empty(DuplicateKeys(document));
    }

    [Fact]
    public void ARepeatedKeyInOneThemeIsRejectedEvenIfTheValuesMatch()
    {
        XDocument document = XDocument.Parse("""
            <ResourceDictionary xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <ResourceDictionary.ThemeDictionaries>
                    <ResourceDictionary x:Key="Light">
                        <SolidColorBrush x:Key="RadioButtonCheckIconDefaultBackground">#803B06</SolidColorBrush>
                        <SolidColorBrush x:Key="RadioButtonCheckIconDefaultBackground">#803B06</SolidColorBrush>
                    </ResourceDictionary>
                </ResourceDictionary.ThemeDictionaries>
            </ResourceDictionary>
            """);
        Assert.Equal("RadioButtonCheckIconDefaultBackground", Assert.Single(DuplicateKeys(document)));
    }

    [Fact]
    public void TheSameKeyInSeparateThemeDictionariesIsAllowed()
    {
        XDocument document = XDocument.Parse("""
            <ResourceDictionary xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <ResourceDictionary.ThemeDictionaries>
                    <ResourceDictionary x:Key="Light"><SolidColorBrush x:Key="Accent">#803B06</SolidColorBrush></ResourceDictionary>
                    <ResourceDictionary x:Key="Dark"><SolidColorBrush x:Key="Accent">#6AA7FF</SolidColorBrush></ResourceDictionary>
                </ResourceDictionary.ThemeDictionaries>
            </ResourceDictionary>
            """);
        Assert.Empty(DuplicateKeys(document));
    }

    private static IEnumerable<string> DuplicateKeys(XDocument document) =>
        document.Root!.DescendantsAndSelf().SelectMany(parent => parent.Elements()
            .Select(child => (string?)child.Attribute(X + "Key"))
            .OfType<string>()
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key));

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
