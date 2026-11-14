using System.Reflection;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// WV12 host contracts. The model/filter assertions use detached Avalonia controls only; no Window, native GUI,
/// profile, database or network is started. The source assertions pin the protected Settings route because that
/// route cannot be exercised without the forbidden GUI smoke.
/// </summary>
[Collection("Localization")]
public sealed class WorkViewDetailsHostTests
{
    [Fact]
    public void DetailsRouteKeepsThePersistentSettingsDraftAndUsesTheCurrentView()
    {
        string host = File.ReadAllText(Root("Desktop/Shell/MainWindow.WorkViewDetails.cs"));
        Assert.Contains("NavigateWorkspace(WorkspacePage.Settings)", host, StringComparison.Ordinal);
        Assert.Contains("SettingsEditor editor = SettingsEditor", host, StringComparison.Ordinal);
        Assert.Contains("editor.ApplyWorkView(_workViews.Current, viewChanged: false)", host, StringComparison.Ordinal);
        Assert.Contains("editor.ShowWorkViewOverview(diff)", host, StringComparison.Ordinal);
        Assert.Contains("editor.FocusWorkViewOverview()", host, StringComparison.Ordinal);
        Assert.DoesNotContain("CancelChanges", host, StringComparison.Ordinal);
        Assert.DoesNotContain("new SettingsEditor", host, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalSettingsEntryClearsThePreviousDetailsAndLiteralFilters()
    {
        string sections = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.Sections.cs"));
        string overview = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.WorkViewOverview.cs"));
        Assert.Contains("_workViewOverview?.SetDifferenceFilter(null);", sections, StringComparison.Ordinal);
        Assert.Contains("overview.SetDifferenceFilter(null);", overview, StringComparison.Ordinal);
        Assert.Contains("overview.SetPageFilter(page);", overview, StringComparison.Ordinal);
        Assert.Contains("overview.SetTextFilter(text);", overview, StringComparison.Ordinal);
        Assert.Contains("overview.EnsureLoaded();", overview, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailsFilterAndActiveViewRemainARealDetachedOverviewState()
        => OnOwnedDispatcher(() =>
        {
            using var overview = new MeowChan.Desktop.Views.Settings.WorkViewOverviewControl();
            DiffSummary diff = WorkViewPolicy.Diff(WorkView.Advanced, WorkView.Barebones);
            overview.SetDifferenceFilter(diff);
            overview.SetActiveView(WorkView.Simple);
            overview.EnsureLoaded();

            Assert.Equal(WorkView.Simple, overview.ActiveView);
            Assert.Equal(diff.Changes.Select(change => change.Entry.Id).Order(StringComparer.Ordinal),
                Ids(overview).Order(StringComparer.Ordinal));

            WorkViewEntry first = diff.Changes[0].Entry;
            overview.SetPageFilter(first.Page);
            overview.SetTextFilter(first.Id);
            Assert.All(Ids(overview), id => Assert.Equal(first.Id, id));

            overview.SetDifferenceFilter(null);
            Assert.Null(overview.PageFilter);
            Assert.Equal(string.Empty, overview.TextFilter);
            Assert.Equal(WorkViewPolicy.Entries.Select(entry => entry.Id).Order(StringComparer.Ordinal),
                Ids(overview).Order(StringComparer.Ordinal));
        });

    [Fact]
    public void ZeroDisablesTheNoticeAndItsActionsBeforeAnyHostIsAsked()
    {
        WorkViewSwitchNoticeState state = new();
        state.BeginSwitch();
        Assert.Null(state.Applied(WorkView.Barebones, WorkView.Simple, 0, informationAllowed: true));
    }

    [Fact]
    public void SettingsNoticeFieldHasTheCompleteDraftApplyResetValidationSearchAndHintWiring()
    {
        string notice = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.WorkViewNotice.cs"));
        string editor = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.axaml.cs"));
        string validation = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.Validation.cs"));
        string reset = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.Reset.cs"));
        string search = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.Search.cs"));
        string markup = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.axaml"));

        Assert.Contains("LoadWorkViewSwitchNoticeDraft", editor, StringComparison.Ordinal);
        Assert.Contains("ApplyWorkViewSwitchNoticeDraft(candidate)", editor, StringComparison.Ordinal);
        Assert.Contains("ResetNotificationsDraft", reset, StringComparison.Ordinal);
        Assert.Contains("WorkViewSwitchNoticeSeconds = defaults.WorkViewSwitchNoticeSeconds", reset, StringComparison.Ordinal);
        Assert.Contains("WorkViewNoticeNumericFields", validation, StringComparison.Ordinal);
        Assert.Contains("WorkViewSwitchNoticeSecondsHelp", notice, StringComparison.Ordinal);
        Assert.Contains("if (id == \"settings.workviews\") continue;", search, StringComparison.Ordinal);
        Assert.Contains("WorkViewSwitchNoticeSecondsInput", markup, StringComparison.Ordinal);
        Assert.Contains("WorkViewSwitchNoticeSecondsHint", notice, StringComparison.Ordinal);
        Assert.Contains("WorkViewSwitchNoticeSecondsMinimum", notice, StringComparison.Ordinal);
        Assert.Contains("WorkViewSwitchNoticeSecondsMaximum", notice, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Ids(MeowChan.Desktop.Views.Settings.WorkViewOverviewControl overview)
    {
        DataGrid table = Field<DataGrid>(overview, "_table");
        DataGridCollectionView view = Assert.IsType<DataGridCollectionView>(table.ItemsSource);
        return view.Cast<MeowChan.Desktop.WorkViews.WorkViewOverview.Row>().Select(row => row.Id).ToArray();
    }

    private static T Field<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing overview test seam: " + name)).GetValue(owner)!;

    private static string Root(string relative, [System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative));

    private static void OnOwnedDispatcher(Action test)
    {
        FieldInfo primarySlot = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Avalonia primary dispatcher slot is missing.");
        object? previousPrimary = primarySlot.GetValue(null);
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        string previousLanguage = MeowChan.Desktop.Localization.Texts.Language;
        try
        {
            primarySlot.SetValue(null, owner);
            owner.Invoke(() =>
            {
                owner.VerifyAccess();
                try
                {
                    MeowChan.Desktop.Localization.Texts.Initialize();
                    MeowChan.Desktop.Localization.Texts.SetLanguage("en");
                    test();
                }
                finally
                {
                    MeowChan.Desktop.Localization.Texts.Initialize();
                    MeowChan.Desktop.Localization.Texts.SetLanguage(previousLanguage);
                }
            }, DispatcherPriority.Send);
        }
        finally { primarySlot.SetValue(null, previousPrimary); }
    }
}
