using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

// WP desktop migration: parsed declarations and wiring, plus the passive source projection. These are not rendered
// NameScope, focus, confirmation-dialog or live-region proofs; those still require the parent's native safety runs.
[Collection("Localization")]
public sealed class SourcesPluginsDesktopContractTests
{
    private const string Sources = "Desktop/Views/Sources/SourcesPage.axaml";
    private const string Management = "Desktop/Views/Plugins/PluginsPage.axaml";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Wv = "using:MeowChan.Desktop.WorkViews";

    [Fact]
    public void TheSourcePayloadAndManagementControlsKeepSeparateDeclarationOwners()
    {
        XDocument source = Markup(Sources), management = Markup(Management);
        Assert.Equal("MeowChan.SourcesPage", (string?)source.Root!.Attribute(X + "Class"));
        Assert.Equal("MeowChan.PluginsPage", (string?)management.Root!.Attribute(X + "Class"));
        foreach (XDocument document in new[] { source, management })
            Assert.All(document.Descendants().Attributes(X + "Name").GroupBy(name => name.Value), group => Assert.Single(group));
        XElement host = Named(source, "SourcePluginManagement", "PluginsPage");
        Assert.Same(source.Root, host.Parent);
        XElement slot = Assert.Single(host.Elements());
        Assert.Equal(host.Name.Namespace + "PluginsPage.SourceContent", slot.Name);
        Assert.Same(slot, Named(source, "SourcesScroll", "ScrollViewer").Parent);
        Assert.DoesNotContain(source.Descendants(), e => (string?)e.Attribute(X + "Name") == "PluginApproveSdkTransition");
        Assert.DoesNotContain(management.Descendants(), e => (string?)e.Attribute(X + "Name") == "SourceAllowProviderMismatch");
        XElement contentHost = Named(management, "SourceContentHost", "ContentControl");
        Assert.Same(Named(management, "PluginSplit", "Grid"), contentHost.Parent);
        Assert.Equal("2", (string?)contentHost.Attribute("Grid.ColumnSpan"));
        Assert.Contains("internal PluginsPage PluginManagement => SourcePluginManagement;", Read("Desktop/Views/Sources/SourcesPage.Plugins.cs"));
        Assert.Contains("_pluginsPage = _sourcesPage!.PluginManagement;", Read("Desktop/Shell/MainWindow.Plugins.cs"));
        Assert.Contains("SourcesPageHost.Content = _sourcesPage;", Read("Desktop/Shell/MainWindow.Navigation.cs"));
    }

    [Theory]
    [InlineData(Sources, "SourceAllowProviderMismatch", "CheckBox", "OnSourceMediaPermissionClick")]
    [InlineData(Sources, "SourcePluginEnabled", "Button", "OnSourcePluginEnabledClick")]
    [InlineData(Sources, "SourcePluginCandidate", "Button", "OnSourcePluginCandidateClick")]
    [InlineData(Sources, "SourcePluginSdk", "Button", "OnSourcePluginSdkClick")]
    [InlineData(Sources, "SourcePluginButton", "Button", "OnPluginClick")]
    [InlineData(Sources, "SourceBackButton", "Button", "OnBackClick")]
    [InlineData(Management, "PluginsReviewRepairs", "Button", "OnReviewBundledRepairsClick")]
    [InlineData(Management, "PluginApproveSdkTransition", "Button", "OnApproveSdkTransitionClick")]
    [InlineData(Management, "PluginSave", "Button", "OnSaveClick")]
    [InlineData(Management, "PluginDiscard", "Button", "OnDiscardClick")]
    [InlineData(Management, "PluginBack", "Button", "OnBackClick")]
    public void SafetyAndNavigationControlsKeepTheirTypedNamesAndOriginalOwnerHandlers(string file, string name, string type, string handler)
    {
        XElement control = Named(Markup(file), name, type);
        Assert.Equal(handler, (string?)control.Attribute("Click"));
        string partials = string.Join("\n", Directory.EnumerateFiles(Root(Path.GetDirectoryName(file)!), "*.cs").Select(File.ReadAllText));
        Assert.Matches(@"\b(?:void|Task)\s+" + Regex.Escape(handler) + @"\s*\(", partials);
    }

    [Fact]
    public void CompatibilityNavigationAndDeepLinksUseTheMergedLayersAndRespectOpenDecisions()
    {
        string shortcuts = Read("Desktop/Shell/MainWindow.Fullscreen.cs");
        Match compatibility = Regex.Match(shortcuts,
            @"if \(CommandBindings.Matches\(e, ""navigation\.plugins""\)\)\s*\{(?<body>[\s\S]*?)return;\s*\}");
        Assert.True(compatibility.Success, "The saved plugin-shortcut route is missing.");
        Assert.Contains("NavigateWorkspace(WorkspacePage.Sources)", compatibility.Groups["body"].Value);
        Assert.Contains("ActivateAsync(SourcesPluginsFilter.AllPlugins)", compatibility.Groups["body"].Value);
        string source = Read("Desktop/Views/Sources/SourcesPage.axaml.cs");
        Assert.Contains("IsBusy || SourcePluginManagement.IsDiscardConfirmationOpen", source);
        Assert.Contains("SourcePluginManagement.SetFilter(SourcesPluginsFilter.Sources);", source);
        Assert.Contains("SourcePluginManagement.SelectSourcePlugin(key, details: true);", source);
        string sourceLayers = Read("Desktop/Views/Sources/SourcesPage.Plugins.cs");
        Assert.Contains("SourcePluginManagement.SourceOverviewRequested +=", sourceLayers);
        Assert.Contains("SourcePluginManagement.OpenSourceWatches = SelectSource;", sourceLayers);
        Assert.Contains("await SourcePluginManagement.ActivateMergedAsync();", sourceLayers);
        Assert.Contains("SelectSource(sourceId);", sourceLayers);
        Assert.Contains("OnPluginClick(this, new RoutedEventArgs());", sourceLayers);
        string management = Read("Desktop/Views/Plugins/PluginsPage.Sources.cs");
        Assert.Contains("if (_ended || _busy || IsDiscardConfirmationOpen) return;", management);
        Assert.Contains("if (_busy || IsDiscardConfirmationOpen) return;", management);
        Assert.Contains("SourceOverviewRequested?.Invoke(this, EventArgs.Empty);", management);
        XElement cards = Named(Markup(Sources), "SourceCards", "PluginSelectionList");
        Assert.Equal("OnSourceSelectionChanged", (string?)cards.Attribute("SelectionChanged"));
        Assert.Equal("OnSourceListKeyDown", (string?)cards.Attribute("KeyDown"));
        Assert.Contains("SelectionListItem selected", sourceLayers); // no stale StackPanel/Button list assumption
    }

    [Fact]
    public void ResponsiveSourceFocusHandoffUsesBoundedLayoutPasses()
    {
        string source = Read("Desktop/Views/Sources/SourcesPage.axaml.cs");
        Assert.Contains("private const int SourceFocusMaxPasses = 3;", source);
        Assert.Contains("request != _focusRequest", source);
        Assert.Contains("GetFocusedElement()", source);
        Assert.Contains("++_focusRequest", source);
        Assert.Contains("if (!_applyingSourceFocus)", source);
        Assert.Contains("if (pass + 1 < SourceFocusMaxPasses) return false;", source);
        // The final pass without focus falls back to the first reachable control instead of giving up silently.
        Assert.Contains("FocusFirstReachableControl(target, pass + 1);", source);
        Assert.Contains("Dispatcher.UIThread.Post(() => FocusWhenReady(pass + 1), DispatcherPriority.Background);", source);
        Assert.DoesNotContain("Dispatcher.UIThread.Post(FocusWhenReady, DispatcherPriority.Background);", source);
    }

    [Fact]
    public void SourceSdkAndActivationProxiesUseTheOriginalCapturedActionsAndConfirmation()
    {
        string source = Read("Desktop/Views/Sources/SourcesPage.Plugins.cs");
        Assert.Contains("SourcePluginManagement.ExecuteSourcePluginActionAsync(action)", source);
        Assert.Contains("if (!IsBusy) SourcePluginManagement.ApproveSourceSdk();", source);
        string proxy = Read("Desktop/Views/Plugins/PluginsPage.Sources.cs");
        Assert.Contains("target.IsVisible = original.IsVisible;", proxy);
        Assert.Contains("target.IsEnabled = original.IsEnabled;", proxy);
        Assert.Contains("target.Tag = original.Tag;", proxy);
        Assert.Contains("ExecutePluginActionAsync(invocation, action)", proxy);
        Assert.Contains("Selected is not { } entry || _busy || IsDiscardConfirmationOpen", proxy);
        Assert.Contains("SelectSourcePlugin(entry.Key, details: true);", proxy);
        Assert.Contains("Selected?.Key == entry.Key && ShowingSourcePluginDetails", proxy);
        Assert.Contains("OnApproveSdkTransitionClick(C<Button>(\"PluginApproveSdkTransition\")", proxy);
        string sdk = Read("Desktop/Views/Plugins/PluginsPage.SdkTransition.cs");
        foreach (string guard in new[] { "_sdkTransitionConfirmation.OpenAsync(", "_catalog?.Profile.Revision == capturedRevision",
                     "current.PluginId == capturedPluginId", "currentTransition.ExecutionRevision == capturedExecution",
                     "navigationGeneration == _operationGeneration", "IsEffectivelyVisible" })
            Assert.Contains(guard, sdk);
        Assert.Contains("ExplicitlyApproveSdkTransitionAsync(capturedPluginId, capturedExecution, revision, token)", sdk);
        Assert.DoesNotContain("ProfileStore", source + proxy); // no second authorisation path in the source proxies
    }

    [Fact]
    public void RepairAndDraftControlsStayInTheEmbeddedOwnerWithTheirRevisionAndLifetimeGuards()
    {
        XDocument management = Markup(Management);
        Assert.Equal("plugins.header.review-repairs", (string?)Named(management, "PluginsReviewRepairs", "Button").Attribute(Wv + "WorkViewElement.Id"));
        XElement footer = Named(management, "PluginDraftFooter", "Border");
        Assert.Equal("plugins.detail.draft-footer", (string?)footer.Attribute(Wv + "WorkViewElement.Id"));
        foreach (string name in new[] { "PluginSave", "PluginDiscard", "PluginDraftReasons", "PluginDraftStatus" })
            Assert.Contains(footer, Named(management, name).Ancestors());
        string repair = Read("Desktop/Views/Plugins/PluginsPage.Migration.cs");
        foreach (string guard in new[] { "_drafts.Values.Any(draft => draft.HasDiscardableChanges)", "plugins.repair.finishDraft",
                     "preview.ProfileRevision == selections.ProfileRevision", "Interlocked.Read(ref _migrationCatalogEpoch) == catalogEpoch",
                     "ReferenceEquals(_services, services)", "CommandRouter.IsRoutingContextActive(window, window)" })
            Assert.Contains(guard, repair);
        string discard = Read("Desktop/Views/Plugins/PluginsPage.Discard.cs");
        foreach (string guard in new[] { "_discardConfirmation.OpenAsync(", "ReferenceEquals(draft, current)",
                     "draft.EditGeneration == generation", "draft.HasDiscardableChanges", "navigationGeneration == _operationGeneration" })
            Assert.Contains(guard, discard);
        string save = Read("Desktop/Views/Plugins/PluginsPage.Testing.cs");
        Assert.Contains("errors = await ValidateInWorkerAsync(worker, configuration, token);", save);
        Assert.Contains("SaveConfigurationAsync(entry.PluginId, draft.CandidateId, configuration, draft.ExpectedRevision, token)", save);
        Assert.Contains("if (!Current(generation)) return;", save);
        Assert.Contains("SetAction(\"PluginSave\", stale ? \"plugins.stale\" : \"plugins.reason.unchanged\", draft?.Dirty == true && !stale)", Read("Desktop/Views/Plugins/PluginsPage.axaml.cs"));
        string shortcuts = Read("Desktop/Shell/MainWindow.Shortcuts.cs");
        Assert.Contains("_selectedPage == WorkspacePage.Sources && _pluginsPage?.IsDiscardConfirmationOpen == true", shortcuts);
        Assert.Contains("|| HasBlockingPageDecision()", shortcuts);
        string escape = Read("Desktop/Shell/MainWindow.Fullscreen.cs");
        Assert.Contains("{ _pluginsPage.CancelDiscardConfirmation(); e.Handled = true; return; }", escape);
    }

    [Fact]
    public void SourceConsentKeepsItsAuthorityStatusAndErrorAnnouncementsInTheSourcePayload()
    {
        XDocument source = Markup(Sources);
        XElement panel = Named(source, "SourceMediaIntegrityPanel", "StackPanel");
        Assert.Equal("sources.detail.media-integrity", (string?)panel.Attribute(Wv + "WorkViewElement.Id"));
        Assert.Contains(panel, Named(source, "SourceAllowProviderMismatch", "CheckBox").Ancestors());
        Assert.Equal("Polite", (string?)Named(source, "SourceMediaIntegrityStatus", "TextBlock").Attribute("AutomationProperties.LiveSetting"));
        Assert.Equal("Assertive", (string?)Named(source, "SourceMediaIntegrityError", "TextBlock").Attribute("AutomationProperties.LiveSetting"));
        string code = Read("Desktop/Views/Sources/SourcesPage.axaml.cs");
        foreach (string guard in new[] { "string ownerId = policy.Snapshot.PluginOwnerId;", "string sourceId = policy.Snapshot.SourceId;",
                     "long revision = policy.Snapshot.Revision;", "await save(ownerId, sourceId, allow, revision)",
                     "permission.IsChecked = policy.IsGranted;", "policy.IsActive || policy.IsGranted",
                     "Selected.MediaIntegrityPolicy?.Snapshot.PluginOwnerId == ownerId" })
            Assert.Contains(guard, code);
        foreach (string status in new[] { "sources.providerMismatchInactive", "sources.providerMismatchAllowed", "sources.providerMismatchStrict" })
            Assert.Contains(status, code);
    }

    [Fact]
    public void CommonOperationStatusRemainsPoliteAndRepresentsBothSourceAndPluginWork()
    {
        XDocument management = Markup(Management);
        XElement status = Named(management, "PluginTestStatus", "TextBlock");
        Assert.Equal("Polite", (string?)status.Attribute("AutomationProperties.LiveSetting"));
        Assert.Same(Named(management, "PluginStatusRow", "StackPanel"), status.Parent);
        Assert.Null(status.Parent!.Attribute("IsVisible")); // keep the live region attached, even when its height is zero
        Assert.Equal("Assertive", (string?)Named(management, "PluginPageError", "TextBlock").Attribute("AutomationProperties.LiveSetting"));
        Assert.Contains("RenderOperationFeedback() => SourcePluginManagement.SetSourceOperationBusy(_busy)", Read("Desktop/Views/Sources/SourcesPage.Feedback.cs"));
        string combined = Read("Desktop/Views/Plugins/PluginsPage.Sources.cs");
        Assert.Contains("_sourceOperationBusy = busy;", combined);
        Assert.Contains("UpdateTestButtons();", combined);
        string testing = Read("Desktop/Views/Plugins/PluginsPage.Testing.cs");
        // The bundled repair review owns its operation while open: no page spinner/Cancel behind the modal, and
        // "Working…" is cleared once nothing runs (it used to stay behind after every finished operation).
        Assert.Contains("bool reviewOpen = _busy && _migrationWindow is not null;", testing);
        Assert.Contains("bool working = (_busy && !reviewOpen) || _sourceOperationBusy;", testing);
        Assert.Contains("IsVisible = working;", testing);
        Assert.Contains("if (_testStatusKey == null)", testing);
        Assert.Contains("working ? Texts.T(\"feedback.working\") : reviewOpen ? Texts.T(\"plugins.repair.reviewOpen\") : string.Empty;", testing);
        Assert.Contains("C<Button>(\"PluginCancel\").IsVisible = _busy && !reviewOpen;", testing);
        Assert.Contains("if (!busy && !_busy && _testStatusKey == null)", combined); // do not erase a plugin operation's result
        string source = Read("Desktop/Views/Sources/SourcesPage.axaml.cs");
        Assert.Contains("internal bool IsBusy => _busy || SourcePluginManagement.IsBusy;", source);
        Assert.Contains("this.FindControl<TextBlock>(\"SourceWatchSummary\")!.Text = row.Summary;", source);
        Assert.Contains("row.Descriptor.CaptureAvailable", source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergedRowsRetainExactWatchFailuresTargetsAndConsentRatherThanInferringProviderStatus(bool granted)
    {
        var target = new DiagnosticTarget(DiagnosticTargetKind.Thread, 42);
        var watch = new SourceWatchRow(target, "https://fixture.invalid/thread/42", "Queued: 2", "Thread missing · Disk full · Cooldown: 25s");
        var policy = new SourceMediaIntegrityPolicyView(new("profile", "owner", "source", granted, 4), granted, true, SourceMaturity.Unspecified);
        var registered = new SourceRow(new("source", "Host name", new Uri("https://fixture.invalid/"), ProviderCapabilities.ThreadWatch,
            [], "1", true), "Threads: 1 · Queued: 2", [watch]) { MediaIntegrityPolicy = policy };
        SourceDescription description = new("source", "Self description", new Uri("https://other.invalid/"), SourceCapabilities.ThreadRead, [], [], new("1", []));
        PluginCatalogEntry plugin = new("candidate", "owner", "candidate", "/fixture/plugin.cs",
            new("owner", "Fixture plugin", "1.0", SdkContract.Version, "Description", [description], [], new("1", []), []), null, null, []);
        SourceListRow row = Assert.Single(SourceListRows.Merge([registered], [plugin]));
        Assert.Same(plugin, row.Plugin);
        Assert.Same(registered, row.Source);
        Assert.Equal("Host name", row.Name);
        Assert.Equal("Threads: 1 · Queued: 2", row.Summary);
        SourceWatchRow retained = Assert.Single(row.Watches);
        Assert.Same(watch, retained);
        Assert.Equal(target, retained.Target);
        Assert.Equal("Queued: 2 · Thread missing · Disk full · Cooldown: 25s", retained.Status);
        Assert.Same(policy, row.MediaIntegrityPolicy);
        Assert.Equal(granted, row.MediaIntegrityPolicy!.IsGranted);
        Assert.Equal(4, row.MediaIntegrityPolicy.Snapshot.Revision);
        Assert.True(row.IsActive);
        Assert.True(row.Descriptor.CaptureAvailable);
    }

    private static XElement Named(XDocument document, string name, string? type = null)
    {
        XElement element = Assert.Single(document.Descendants(), e => (string?)e.Attribute(X + "Name") == name);
        if (type is not null) Assert.Equal(type, element.Name.LocalName);
        return element;
    }
    private static XDocument Markup(string path) => XDocument.Parse(Read(path));
    private static string Read(string path) => File.ReadAllText(Root(path));
    private static string Root(string path, [CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../..", path));
}
