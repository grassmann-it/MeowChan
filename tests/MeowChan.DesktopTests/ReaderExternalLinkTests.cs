using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Clickable external links in post text: what may open, what the confirmation shows and when a gesture counts.
public sealed class ReaderExternalLinkTests
{
    [Theory]
    [InlineData("https://example.org/a?b=1#c")]
    [InlineData("http://example.org")]
    [InlineData("HTTPS://Example.org/x")]
    [InlineData("  https://example.org/padded  ")]
    public void OnlyAbsoluteHttpAndHttpsAddressesAreLinks(string text)
    {
        Uri? uri = ReaderExternalLink.Parse(text);
        Assert.NotNull(uri);
        Assert.True(ReaderExternalLink.IsOpenable(uri));
        Assert.Equal(text.Trim().ToLowerInvariant().Split('/')[0], uri!.Scheme + ":");
    }

    [Theory]
    [InlineData("ftp://files.example/f")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("mailto:someone@example.org")]
    [InlineData("data:text/html,x")]
    [InlineData("example.org/path")]
    [InlineData("//example.org/a")]
    [InlineData("/relative/path")]
    [InlineData("http://")]
    [InlineData("https://exa mple.org/")]
    [InlineData("https://example.org/a\nb")]
    [InlineData("http://example.org\\evil")]
    [InlineData("the site")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseStaysPlainText(string? text) => Assert.Null(ReaderExternalLink.Parse(text));

    [Theory]
    [InlineData("https://user@example.org/")]
    [InlineData("https://user:secret@example.org/")]
    [InlineData("http://example.org@evil.example/")]
    [InlineData("http://trusted.example:80@evil.example/x")]
    [InlineData("http://@evil.example/")]
    public void AddressesWithUserInformationAreRejected(string text)
    {
        Assert.Null(ReaderExternalLink.Parse(text));
        if (Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)) Assert.False(ReaderExternalLink.IsOpenable(uri));
    }

    [Fact]
    public void AnAddressLongerThanTheLimitIsNotALink() =>
        Assert.Null(ReaderExternalLink.Parse("https://example.org/" + new string('a', ReaderExternalLink.MaxLength)));

    [Fact]
    public void ALinkOpensOnlyItsVisibleText()
    {
        // The marks carry no address: whatever the anchor's href was, the text decides.
        string text = "the site https://example.org/b and https://shown.example/x";
        ReaderBodyDocument document = ReaderBodyFormatting.Format(text, [],
            [new ArchiveTextMark(ArchiveTextMarkKind.Link, 0, 8), new ArchiveTextMark(ArchiveTextMarkKind.Link, 9, 21)]);
        ReaderBodySegment[] links = document.Segments.Where(segment => segment.Link != null).ToArray();
        Assert.Equal(2, links.Length);
        Assert.All(links, segment => Assert.Equal(segment.Link!.OriginalString, document.Text.Substring(segment.Start, segment.Length)));
        Assert.Equal(["https://example.org/b", "https://shown.example/x"], links.Select(segment => segment.Link!.OriginalString));
        // The label "the site" is marked as a link but is not an address: no link flag, no underline.
        ReaderBodySegment label = document.Segments.First(segment => document.Text.Substring(segment.Start, segment.Length).Contains("the site"));
        Assert.Null(label.Link);
        Assert.Equal(ArchiveTextStyle.None, label.Style & ArchiveTextStyle.Link);
    }

    [Fact]
    public void UserInformationAndOtherSchemesLeaveNoLinkInTheDocument()
    {
        ReaderBodyDocument document = ReaderBodyFormatting.Format("see http://user@evil.example/x and ftp://files.example/f and https://ok.example/", []);
        Uri link = Assert.Single(document.Segments, segment => segment.Link != null).Link!;
        Assert.Equal("https://ok.example/", link.OriginalString);
        Assert.DoesNotContain(document.Segments, segment => segment.Link != null && segment.Link.OriginalString.Contains('@'));
    }

    [Fact]
    public void TheConfirmationShowsTheHostInAsciiSoLookAlikesAreVisible()
    {
        (string prefix, string host, string tail) = ReaderExternalLink.Display(new Uri("https://bücher.example/path?x=1#top"));
        Assert.Equal("https://", prefix);
        Assert.Equal("xn--bcher-kva.example", host);
        Assert.Equal("/path?x=1#top", tail);
        (_, _, string withPort) = ReaderExternalLink.Display(new Uri("http://example.org:8443/a"));
        Assert.Equal(":8443/a", withPort);
        (_, string cyrillic, _) = ReaderExternalLink.Display(new Uri("https://аpple.example/")); // Cyrillic a
        Assert.StartsWith("xn--", cyrillic);
    }

    private static ReaderBodySegment LinkSegment(ArchiveTextStyle style = ArchiveTextStyle.Link, string address = "https://example.org/a") =>
        new(ReaderBodySegmentKind.Text, 0, address.Length, null, style, new Uri(address));

    [Fact]
    public void ALinkInAHiddenSpoilerNeverOpensAndOneOnARevealedSpoilerDoes()
    {
        ReaderBodySegment inSpoiler = LinkSegment(ArchiveTextStyle.Link | ArchiveTextStyle.Spoiler);
        Assert.Null(ReaderExternalLink.ForActivation(inSpoiler, spoilersRevealed: false, selectionActive: false));
        Assert.NotNull(ReaderExternalLink.ForActivation(inSpoiler, spoilersRevealed: true, selectionActive: false));
        Assert.NotNull(ReaderExternalLink.ForActivation(LinkSegment(), spoilersRevealed: false, selectionActive: false));
    }

    [Fact]
    public void AClickDuringATextSelectionNeverOpens()
    {
        Assert.Null(ReaderExternalLink.ForActivation(LinkSegment(), spoilersRevealed: true, selectionActive: true));
        Assert.Null(ReaderExternalLink.ForActivation(null, true, false));
        Assert.Null(ReaderExternalLink.ForActivation(new ReaderBodySegment(ReaderBodySegmentKind.Text, 0, 4), true, false));
    }

    [Fact]
    public void EnterOpensTheLinkInTheSelectionOrTheOnlyLink()
    {
        ReaderBodyDocument two = ReaderBodyFormatting.Format("a https://first.example/a b https://second.example/b", []);
        Assert.Null(ReaderExternalLink.ForKeyboard(two, 0, 0, true)); // Two links, nothing selected.
        int second = two.Text.IndexOf("https://second", StringComparison.Ordinal);
        Assert.Equal("https://second.example/b", ReaderExternalLink.ForKeyboard(two, second + 3, second + 1, true)?.OriginalString);
        Assert.Equal("https://first.example/a", ReaderExternalLink.ForKeyboard(two, 0, two.Text.Length, true)?.OriginalString);
        ReaderBodyDocument one = ReaderBodyFormatting.Format("only https://one.example/ here", []);
        Assert.Equal("https://one.example/", ReaderExternalLink.ForKeyboard(one, 0, 0, true)?.OriginalString);
        Assert.Null(ReaderExternalLink.ForKeyboard(null, 0, 0, true));
        // A selection that touches no link opens nothing.
        Assert.Null(ReaderExternalLink.ForKeyboard(one, 0, 3, true));
    }

    [Fact]
    public void ASpoilerLinkIsIgnoredByEnterUntilRevealed()
    {
        ReaderBodyDocument document = ReaderBodyFormatting.Format("x https://hidden.example/ y", [],
            [new ArchiveTextMark(ArchiveTextMarkKind.Spoiler, 0, 27)]);
        Assert.Null(ReaderExternalLink.ForKeyboard(document, 0, 0, spoilersRevealed: false));
        Assert.NotNull(ReaderExternalLink.ForKeyboard(document, 0, 0, spoilersRevealed: true));
    }

    [Fact]
    public void OnlyAClickReleaseOrEnterRaisesTheLinkEvent()
    {
        // Hover (moved/entered/exited), focus and press never raise it: exactly two raise sites, in release and Enter.
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Desktop", "ReaderBodyTextBlock.cs"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(source, "new ReaderLinkInvokedEventArgs").Count);
        string Method(string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature);
            int next = source.IndexOf("\n    protected override", start + signature.Length, StringComparison.Ordinal);
            return source[start..(next < 0 ? source.Length : next)];
        }
        Assert.Contains("ReaderLinkInvokedEventArgs", Method("protected override void OnPointerReleased"));
        Assert.Contains("ReaderLinkInvokedEventArgs", Method("protected override void OnKeyDown"));
        foreach (string handler in new[] { "protected override void OnPointerMoved", "protected override void OnPointerExited", "protected override void OnPointerPressed", "protected override void OnGotFocus" })
            if (source.Contains(handler)) Assert.DoesNotContain("ReaderLinkInvokedEventArgs", Method(handler));
    }

    [Fact]
    public void ThePageValidatesAgainBeforeAnythingIsShownOrLaunched()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Desktop", "Views", "Archive", "ArchivePage.ExternalLinks.cs"));
        Assert.Contains("if (_ended || !ReaderExternalLink.IsOpenable(uri)) return;", source);
        Assert.Contains("if (!ReaderExternalLink.IsOpenable(uri)) return;", source);
        Assert.DoesNotContain("ExternalLauncher", source); // Its fallback opens any string.
        Assert.DoesNotContain("Settings", source); // The session choice is not persisted.
    }

    [Fact]
    public void TheConfirmationStartsOnCancelSoAHeldEnterCannotConfirmIt()
    {
        // The Enter (or its auto-repeat) that opened the dialog must land on Cancel; the smoke proves it with a second Enter.
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Desktop", "Views", "Archive", "ArchivePage.ExternalLinks.cs"));
        Assert.Contains("ReaderLinkCancel.Focus();", source);
        Assert.DoesNotContain("ReaderLinkOpen.Focus()", source);
    }

    [Fact]
    public void TheConfirmationAnnouncesTheAddressCyclesTabAndBlocksReaderShortcuts()
    {
        string links = File.ReadAllText(Path.Combine(RepositoryRoot(), "Desktop", "Views", "Archive", "ArchivePage.ExternalLinks.cs"));
        Assert.Contains("KeyboardNavigation.SetTabNavigation(ReaderLinkDialog, KeyboardNavigationMode.Cycle)", links);
        Assert.Contains("AutomationProperties.SetName(ReaderLinkDialog,", links);
        Assert.Contains("AutomationProperties.SetName(ReaderLinkUrl, _externalLinkAddress)", links);
        Assert.Contains("AutomationProperties.SetHelpText(ReaderLinkCancel,", links);
        string reader = File.ReadAllText(Path.Combine(RepositoryRoot(), "Desktop", "Views", "Archive", "ArchivePage.Reader.cs"));
        Assert.Contains("internal bool IsReaderDialogOpen => IsExternalLinkDialogOpen ||", reader);
        // Its Escape must run before the dialog guard that would otherwise swallow it.
        int escape = reader.IndexOf("IsExternalLinkDialogOpen)", reader.IndexOf("private async void OnReaderKeyDown", StringComparison.Ordinal), StringComparison.Ordinal);
        int guard = reader.IndexOf("if (!_largeReader || IsReaderDialogOpen) return;", StringComparison.Ordinal);
        Assert.True(escape > 0 && guard > escape, "The link dialog's Escape must precede the IsReaderDialogOpen guard.");
    }

    [Theory]
    [InlineData("reader.link.openNamed")]
    [InlineData("reader.link.dialogName")]
    public void TheAnnouncementTextsExistInAllFivePacksWithTheAddressPlaceholder(string key)
    {
        Assert.Contains(ReaderCardsCatalog.Entries, entry => entry.Key == key && entry.English.Contains("{0}") && entry.German.Contains("{0}"));
        foreach (string language in new[] { "es", "fr", "ru" })
        {
            string pack = File.ReadAllText(Path.Combine(RepositoryRoot(), "Desktop", "Localization", "Packs", language + ".json"));
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(pack, "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\":").Count);
            System.Text.RegularExpressions.Match line = System.Text.RegularExpressions.Regex.Match(pack, "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\": \"([^\"]*)\"");
            Assert.True(line.Success && line.Groups[1].Value.Contains("{0}"), language + " " + key);
        }
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("Repository root not found.");
    }
}
