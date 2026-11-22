using System.Runtime.CompilerServices;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

/// <summary>
/// Presentation-only contracts for the source-tag-to-Booru handoff. These tests never create a
/// host, provider, profile or network request: the host supplies the live gate and the dialog must
/// consult it before it offers or invokes the query action.
/// </summary>
public sealed class BooruPresentationActionGateTests
{
    [Fact]
    public void AvailabilityIsLiveAndARevokedGateCannotInvokeTheHandoff()
    {
        bool available = true;
        int handoffs = 0;
        Func<bool> gate = () => available;
        Func<string, (bool Opened, int? Limit)> handoff = _ => { handoffs++; return (true, null); };

        Assert.True(BooruMediaDetailsDialog.IsSearchActionAvailable(gate, handoff));
        Assert.True(BooruMediaDetailsDialog.TryUseAsSearch(gate, handoff, "cat").Opened);
        Assert.Equal(1, handoffs);

        available = false;
        Assert.False(BooruMediaDetailsDialog.IsSearchActionAvailable(gate, handoff));
        Assert.False(BooruMediaDetailsDialog.TryUseAsSearch(gate, handoff, "new_composition").Opened);
        Assert.Equal(1, handoffs);
    }

    [Fact]
    public void AReplacedServiceIdentityRevokesAnOldDialogHandoff()
    {
        object openedServices = new();
        object currentServices = openedServices;
        int handoffs = 0;
        Func<bool> gate = () => ReferenceEquals(openedServices, currentServices);
        Func<string, (bool Opened, int? Limit)> handoff = _ => { handoffs++; return (true, null); };

        Assert.True(BooruMediaDetailsDialog.TryUseAsSearch(gate, handoff, "cat").Opened);
        currentServices = new object();

        Assert.False(BooruMediaDetailsDialog.IsSearchActionAvailable(gate, handoff));
        Assert.False(BooruMediaDetailsDialog.TryUseAsSearch(gate, handoff, "must_not_reach_new_page").Opened);
        Assert.Equal(1, handoffs);
    }

    [Fact]
    public void MissingHostHandoffDoesNotOfferAQueryAction()
    {
        Assert.False(BooruMediaDetailsDialog.IsSearchActionAvailable(() => true, null));
        Assert.False(BooruMediaDetailsDialog.TryUseAsSearch(() => true, null, "cat").Opened);
    }

    [Fact]
    public void GalleryDetailsBindsTheLiveGateToTheOpenedComposition()
    {
        string gallery = Read("Desktop/Shell/MainWindow.GalleryActions.cs");
        string dialog = Read("Desktop/Views/Media/BooruMediaDetailsDialog.cs");

        Assert.Contains("BooruWorkbenchServices? openedServices = CanOpenBooru ? _session.BooruServices : null;", gallery, StringComparison.Ordinal);
        Assert.Contains("openedServices is null ? null : () => IsBooruSearchAvailable(openedServices)", gallery, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_session.BooruServices, openedServices)", gallery, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(page.Services, openedServices)", gallery, StringComparison.Ordinal);
        Assert.DoesNotContain("            UseAsBooruSearch);", gallery, StringComparison.Ordinal);
        Assert.Contains("if (IsSearchActionAvailable())", dialog, StringComparison.Ordinal);
        Assert.Contains("if (!IsSearchActionAvailable())", dialog, StringComparison.Ordinal);
        Assert.Contains("TryUseAsSearch(_isSearchAvailable, _useAsSearch, query)", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void PresentationGateDoesNotRemoveLocalDetailsOrPrivateTagBoundaries()
    {
        string dialog = Read("Desktop/Views/Media/BooruMediaDetailsDialog.cs");

        Assert.Contains("foreach (string line in fileLines)", dialog, StringComparison.Ordinal);
        Assert.Contains("body.Children.Add(OwnTags());", dialog, StringComparison.Ordinal);
        Assert.Contains("SourceTags(details)", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadPostAsync", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchAsync", dialog, StringComparison.Ordinal);
    }

    private static string Read(string relative, [CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative)));
}
