using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// MEDIA103-NAV P1: a gallery opened from the Medien page names its close action "Back to media". Presentation only:
// no new command, no new shortcut. The real viewer, host and journey are covered by the media page smoke.
[Collection("Localization")]
public sealed class GalleryMediaCloseLabelTests : IDisposable
{
    private readonly string _previous = Texts.Language;
    public GalleryMediaCloseLabelTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    [Theory]
    [InlineData("en", "Back to media", "Close gallery")]
    [InlineData("de", "Zurück zu Medien", "Galerie schließen")]
    [InlineData("es", null, null)]
    [InlineData("fr", null, null)]
    [InlineData("ru", null, null)]
    public void TheMediaCloseLabelIsTranslatedAndDiffersFromTheGalleryClose(string language, string? media, string? close)
    {
        Texts.Initialize();
        Texts.SetLanguage(language);
        if (media == null)
        {
            using JsonDocument pack = JsonDocument.Parse(ReadSource("Desktop/Localization/Packs/" + language + ".json"));
            JsonElement strings = pack.RootElement.GetProperty("strings");
            media = strings.GetProperty("command.gallery.close.media").GetString();
            close = strings.GetProperty("command.gallery.close").GetString();
            Assert.Equal(strings.GetProperty("viewer.help.escape.media").GetString(), Texts.T("viewer.help.escape.media"));
        }
        Assert.False(string.IsNullOrWhiteSpace(media));
        Assert.Equal(media, Texts.T("command.gallery.close.media"));
        Assert.Equal(close, CommandCatalog.Label("gallery.close"));
        Assert.NotEqual(Texts.T("command.gallery.close"), Texts.T("command.gallery.close.media"));
        Assert.NotEqual(Texts.T("viewer.help.escape"), Texts.T("viewer.help.escape.media"));
        Assert.StartsWith("Esc", Texts.T("viewer.help.escape.media"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLabelIsAPresentationEntryAndNoCommand()
    {
        Assert.Contains(CommandLabelCatalog.PresentationEntries, entry => entry.Key == "command.gallery.close.media"
            && entry.English == "Back to media" && entry.German == "Zurück zu Medien");
        Assert.DoesNotContain(CommandLabelCatalog.Entries, entry => entry.Key == "command.gallery.close.media");
        Assert.DoesNotContain(CommandCatalog.All, command => command.LabelKey == "command.gallery.close.media");
        // Escape and the binding stay those of gallery.close.
        Assert.Equal("Escape", CommandCatalog.Get("gallery.close").Shortcut);
    }

    [Fact]
    public void TheViewerFlagOnlyChangesTheCloseLabel()
    {
        var viewer = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        Assert.False(viewer.ClosesToMedia);
        Assert.Equal(CommandCatalog.Label("gallery.close"), viewer.CloseLabel);
        typeof(GalleryViewer).GetField("_returnTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, GalleryReturnTarget.Media);
        Assert.Equal(Texts.T("command.gallery.close.media"), viewer.CloseLabel);
        // The tooltip keeps whatever shortcut and scope gallery.close has; only the leading label changes.
        string standard = CommandBindings.Tooltip("gallery.close");
        Assert.StartsWith(CommandCatalog.Label("gallery.close"), standard, StringComparison.Ordinal);
        Assert.Equal("Back to media" + standard[CommandCatalog.Label("gallery.close").Length..],
            CommandBindings.Tooltip("gallery.close", visibleLabel: viewer.CloseLabel));
    }

    private static string ReadSource(string path)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, path);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(path);
    }
}
