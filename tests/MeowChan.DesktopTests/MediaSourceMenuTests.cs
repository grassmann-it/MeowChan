using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class MediaSourceMenuTests
{
    [Fact]
    public void ProviderCommandsLaunchOnlyOnActivationAndEncodeTheCompleteOriginalUrl()
    {
        BooruComponentDispatcher.Run(() =>
        {
            const string original = "https://cdn.example.com/a%20b.png?file=1&ref=two#part";
            List<string> opened = [];
            List<string> copied = [];
            bool current = true;
            ContextMenuBuilder builder = new();
            MediaSourceMenu.AddTo(builder, new(original, ["zebra", "blue eyes", "artist\tname", " ", "Árbol"]),
                () => current, text => { copied.Add(text); return Task.CompletedTask; },
                url => { opened.Add(url); return Task.CompletedTask; });
            ContextMenu menu = Assert.IsType<ContextMenu>(builder.Build());
            MenuItem search = ById(menu, MediaSourceMenu.SearchId);
            Assert.True(search.IsEnabled);
            Assert.Equal(Texts.T("media.menu.reverseSearch.hint"), ToolTip.GetTip(search));
            Assert.Empty(opened);
            Assert.Empty(copied);

            foreach ((string id, string host, string path) in new[]
            {
                ("saucenao", "saucenao.com", "/search.php"),
                ("iqdb", "iqdb.org", "/"),
                ("lens", "lens.google.com", "/uploadbyurl")
            })
            {
                MenuItem service = ById(search, MediaSourceMenu.SearchId + "." + id);
                Assert.Null(service.InputGesture);
                service.Command!.Execute(null);
                Uri destination = new(opened[^1]);
                Assert.Equal("https", destination.Scheme);
                Assert.Equal(host, destination.Host);
                Assert.Equal(path, destination.AbsolutePath);
                Assert.Equal("?url=" + Uri.EscapeDataString(original), destination.Query);
            }
            MenuItem copy = ById(menu, MediaSourceMenu.CopyId);
            Assert.Null(copy.InputGesture);
            copy.Command!.Execute(null);
            Assert.Equal("zebra blue_eyes artist_name Árbol", Assert.Single(copied));

            current = false;
            ById(search, MediaSourceMenu.SearchId + ".lens").Command!.Execute(null);
            copy.Command.Execute(null);
            Assert.Equal(3, opened.Count);
            Assert.Single(copied);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("file:///tmp/my-image.png")]
    [InlineData("https://localhost./image.png")]
    [InlineData("http://127.1/image.png")]
    [InlineData("https://10.5.4.3/image.png")]
    [InlineData("https://[::ffff:192.168.1.10]/image.png")]
    [InlineData("https://[fd00::10]/image.png")]
    [InlineData("https://image-host/image.png")]
    [InlineData("https://media.home.arpa/image.png")]
    [InlineData("https://user:secret@cdn.example.com/image.png")]
    public void LocalUnusableOrCredentialedSourcesAndEmptyTagsStayDisabled(string? source)
    {
        BooruComponentDispatcher.Run(() =>
        {
            ContextMenuBuilder builder = new();
            MediaSourceMenu.AddTo(builder, new(source, [" ", "\n"]), () => true,
                _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
            ContextMenu menu = Assert.IsType<ContextMenu>(builder.Build());
            MenuItem search = ById(menu, MediaSourceMenu.SearchId);
            Assert.False(search.IsEnabled);
            Assert.Empty(search.Items);
            Assert.True(ToolTip.GetShowOnDisabled(search));
            Assert.Equal(Texts.T("media.menu.reverseSearch.noUrl"), AutomationProperties.GetHelpText(search));
            MenuItem copy = ById(menu, MediaSourceMenu.CopyId);
            Assert.False(copy.IsEnabled);
            Assert.True(ToolTip.GetShowOnDisabled(copy));
            Assert.Contains(Texts.T("media.menu.copyBooruTags.empty"), Assert.IsType<string>(ToolTip.GetTip(copy)));
            copy.Command!.Execute(null);
        });
    }

    [Fact]
    public void ReadingLocalMetadataEnablesSearchWithoutOpeningAnything()
    {
        BooruComponentDispatcher.Run(async () =>
        {
            List<string> opened = [];
            TaskCompletionSource<string?> metadata = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ContextMenuBuilder builder = new();
            MediaSourceMenu.AddTo(builder, new(null, ["my private tag"]), () => true, _ => Task.CompletedTask,
                url => { opened.Add(url); return Task.CompletedTask; }, () => metadata.Task);
            ContextMenu menu = Assert.IsType<ContextMenu>(builder.Build());
            MenuItem search = ById(menu, MediaSourceMenu.SearchId);
            Assert.False(search.IsEnabled);
            Assert.True(ById(menu, MediaSourceMenu.CopyId).IsEnabled);
            metadata.SetResult("http://images.example.com/photo.jpg");
            await BooruTestSupport.WaitAsync(() => search.IsEnabled, "the original link to be available");
            Assert.Empty(opened);
            ById(search, MediaSourceMenu.SearchId + ".iqdb").Command!.Execute(null);
            Assert.Equal("https://iqdb.org/?url=http%3A%2F%2Fimages.example.com%2Fphoto.jpg", Assert.Single(opened));
        });
    }

    [Fact]
    public void AStoredProviderClaimUsesTheOriginalThenThePostAndKeepsProviderTagOrder()
    {
        BooruPostClaim claim = new("danbooru", "42", BooruPostClaimPresence.All,
            Tags: [new("zebra", BooruPostClaimTagCategory.General), new("alice", BooruPostClaimTagCategory.Artist)],
            Media: [new(BooruPostClaimMediaRole.Sample, "https://cdn.donmai.us/sample.jpg"),
                new(BooruPostClaimMediaRole.Original, "https://cdn.donmai.us/original.jpg")]);
        MediaSourceMenuData original = MediaSourceMenuData.ForProvider("42", claim);
        Assert.Equal("https://cdn.donmai.us/original.jpg", original.Url);
        Assert.Equal("zebra alice", MediaSourceMenu.TagText(original.Tags));
        MediaSourceMenuData restricted = MediaSourceMenuData.ForProvider("42", claim with { IsBanned = true },
            "https://cdn.donmai.us/previously-saved.jpg");
        Assert.Equal("https://danbooru.donmai.us/posts/42", restricted.Url);
    }

    [Fact]
    public void LabelsAndTooltipsHaveTranslationsAndTheCopyCommandHasNoMacShortcut()
    {
        string oldLanguage = Texts.Language;
        string[] keys = [MediaSourceMenu.SearchId, "media.menu.reverseSearch.hint", "media.menu.reverseSearch.noUrl",
            "command.media.copy-booru-tags", "media.menu.copyBooruTags.empty"];
        try
        {
            Texts.Initialize();
            foreach (string language in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
            {
                Texts.SetLanguage(language);
                Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(Texts.Own(key)), $"{language}: {key} missing"));
            }
            Assert.Empty(EffectiveShortcutBindings.Build(null, ShortcutPlatform.MacOS).For(MediaSourceMenu.CopyId));
        }
        finally { Texts.SetLanguage(oldLanguage); }
    }

    private static MenuItem ById(ItemsControl menu, string id) =>
        Assert.Single(menu.Items.OfType<MenuItem>(), item => AutomationProperties.GetAutomationId(item) == id);
}
