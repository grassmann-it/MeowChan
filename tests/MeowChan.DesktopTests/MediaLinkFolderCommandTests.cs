using System.Runtime.CompilerServices;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class MediaLinkFolderCommandTests : IDisposable
{
    public MediaLinkFolderCommandTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));

    [Fact]
    public void EmptyMediaLinkFolderCommandIsRegisteredWithItsPresentationContract()
    {
        CommandDefinition command = CommandCatalog.Get("media.link-folder");

        Assert.Equal("command.media.link-folder", command.LabelKey);
        Assert.Equal("Link folder…", command.LabelEn);
        Assert.Equal("Ordner verknüpfen…", command.LabelDe);
        Assert.Equal("folder", command.Icon);
        Assert.Null(command.Shortcut);
        Assert.Equal(CommandScope.Media, command.Scope);
        Assert.Equal(["media-empty-state"], command.Contexts);
        Assert.True(command.IsImplemented);
        Assert.True(File.Exists(Root("Assets/icons/feather/masks/folder.png")));

        Assert.Equal("Link folder…", CommandCatalog.Label(command.Id));
        Texts.SetLanguage("de");
        Assert.Equal("Ordner verknüpfen…", CommandCatalog.Label(command.Id));

        string mediaPage = File.ReadAllText(Root("Desktop/Views/Media/MediaPage.cs"));
        Assert.Contains("CommandBindings.Apply(_linkFolder, \"media.link-folder\", iconWithLabel: true);", mediaPage);
    }
}
