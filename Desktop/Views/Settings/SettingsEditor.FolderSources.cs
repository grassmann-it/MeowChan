using System.Collections.Generic;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// PR2/PR3 (read-only folder sources): their limits in "Storage and limits", group "Media folder limits" (the list is the group above it). Every limit is a setting with default, minimum and
// maximum (Config.FolderSource*); the catalog keeps fixed ceilings of 2,000,000 items and 1,024 characters per path.
public partial class SettingsEditor
{
    public string FolderSourcesGroupLabel => Texts.T("budgets.group.folderSources");
    private bool _folderSourceBackgroundHashing = true;
    public string FolderSourceBackgroundHashingLabel => Texts.T("budgets.folderSourceBackground");
    public string FolderSourceBackgroundHashingHint => Texts.T("budgets.folderSourceBackgroundHint");
    public bool FolderSourceBackgroundHashing { get => _folderSourceBackgroundHashing; set => SetField(ref _folderSourceBackgroundHashing, value, nameof(FolderSourceBackgroundHashing)); }
    private decimal? _folderSourceMaxFiles;
    public string FolderSourceMaxFilesLabel => Texts.T("budgets.folderSourceFiles");
    public string FolderSourceMaxFilesHint => Texts.T("budgets.folderSourceFilesHint");
    public decimal? FolderSourceMaxFiles { get => _folderSourceMaxFiles; set => SetField(ref _folderSourceMaxFiles, value, nameof(FolderSourceMaxFiles)); }
    public string FolderSourceMaxFilesHelp => FieldHelp(nameof(FolderSourceMaxFilesInput), Texts.T("settings.unitCount") + ". " + FolderSourceMaxFilesHint);
    private decimal? _folderSourceMaxRoots;
    public string FolderSourceMaxRootsLabel => Texts.T("budgets.folderSourceRoots");
    public string FolderSourceMaxRootsHint => Texts.T("budgets.folderSourceRootsHint");
    public decimal? FolderSourceMaxRoots { get => _folderSourceMaxRoots; set => SetField(ref _folderSourceMaxRoots, value, nameof(FolderSourceMaxRoots)); }
    public string FolderSourceMaxRootsHelp => FieldHelp(nameof(FolderSourceMaxRootsInput), Texts.T("settings.unitCount") + ". " + FolderSourceMaxRootsHint);
    private decimal? _folderSourceHashParallelism;
    public string FolderSourceHashParallelismLabel => Texts.T("budgets.folderSourceParallelism");
    public string FolderSourceHashParallelismHint => Texts.T("budgets.folderSourceParallelismHint");
    public decimal? FolderSourceHashParallelism { get => _folderSourceHashParallelism; set => SetField(ref _folderSourceHashParallelism, value, nameof(FolderSourceHashParallelism)); }
    public string FolderSourceHashParallelismHelp => FieldHelp(nameof(FolderSourceHashParallelismInput), Texts.T("settings.unitCount") + ". " + FolderSourceHashParallelismHint);
    private decimal? _folderSourceHashMaxFileGiB;
    public string FolderSourceHashMaxFileGiBLabel => Texts.T("budgets.folderSourceHashMaxFile");
    public string FolderSourceHashMaxFileGiBHint => Texts.T("budgets.folderSourceHashMaxFileHint");
    public decimal? FolderSourceHashMaxFileGiB { get => _folderSourceHashMaxFileGiB; set => SetField(ref _folderSourceHashMaxFileGiB, value, nameof(FolderSourceHashMaxFileGiB)); }
    public string FolderSourceHashMaxFileGiBHelp => FieldHelp(nameof(FolderSourceHashMaxFileGiBInput), Texts.T("settings.unitGiB") + ". " + FolderSourceHashMaxFileGiBHint);
    private decimal? _folderSourceRescanMinutes;
    public string FolderSourceRescanMinutesLabel => Texts.T("budgets.folderSourceRescan");
    public string FolderSourceRescanMinutesHint => Texts.T("budgets.folderSourceRescanHint");
    public decimal? FolderSourceRescanMinutes { get => _folderSourceRescanMinutes; set => SetField(ref _folderSourceRescanMinutes, value, nameof(FolderSourceRescanMinutes)); }
    public string FolderSourceRescanMinutesHelp => FieldHelp(nameof(FolderSourceRescanMinutesInput), Texts.T("settings.unitMinutes") + ". " + FolderSourceRescanMinutesHint);
    private decimal? _folderSourceCatalogLimitMiB;
    public string FolderSourceCatalogLimitMiBLabel => Texts.T("budgets.folderSourceCatalog");
    public string FolderSourceCatalogLimitMiBHint => Texts.T("budgets.folderSourceCatalogHint");
    public decimal? FolderSourceCatalogLimitMiB { get => _folderSourceCatalogLimitMiB; set => SetField(ref _folderSourceCatalogLimitMiB, value, nameof(FolderSourceCatalogLimitMiB)); }
    public string FolderSourceCatalogLimitMiBHelp => FieldHelp(nameof(FolderSourceCatalogLimitMiBInput), Texts.T("settings.unitMiB") + ". " + FolderSourceCatalogLimitMiBHint);
    private decimal? _folderSourceGalleryHashWindow;
    public string FolderSourceGalleryHashWindowLabel => Texts.T("budgets.folderSourceGalleryWindow");
    public string FolderSourceGalleryHashWindowHint => Texts.T("budgets.folderSourceGalleryWindowHint");
    public decimal? FolderSourceGalleryHashWindow { get => _folderSourceGalleryHashWindow; set => SetField(ref _folderSourceGalleryHashWindow, value, nameof(FolderSourceGalleryHashWindow)); }
    public string FolderSourceGalleryHashWindowHelp => FieldHelp(nameof(FolderSourceGalleryHashWindowInput), Texts.T("settings.unitCount") + ". " + FolderSourceGalleryHashWindowHint);

    /// <summary>Every text of the folder source rows, for a language change.</summary>
    private static IEnumerable<string> FolderSourceTextProperties()
    {
        yield return nameof(FolderSourcesGroupLabel);
        yield return nameof(MediaFoldersLabel); yield return nameof(MediaFoldersHelp); yield return nameof(MediaFoldersLimitsHint);
        yield return nameof(FolderSourceBackgroundHashingLabel);
        yield return nameof(FolderSourceBackgroundHashingHint);
        yield return nameof(FolderSourceMaxFilesLabel); yield return nameof(FolderSourceMaxFilesHint); yield return nameof(FolderSourceMaxFilesHelp);
        yield return nameof(FolderSourceMaxRootsLabel); yield return nameof(FolderSourceMaxRootsHint); yield return nameof(FolderSourceMaxRootsHelp);
        yield return nameof(FolderSourceHashParallelismLabel); yield return nameof(FolderSourceHashParallelismHint); yield return nameof(FolderSourceHashParallelismHelp);
        yield return nameof(FolderSourceHashMaxFileGiBLabel); yield return nameof(FolderSourceHashMaxFileGiBHint); yield return nameof(FolderSourceHashMaxFileGiBHelp);
        yield return nameof(FolderSourceRescanMinutesLabel); yield return nameof(FolderSourceRescanMinutesHint); yield return nameof(FolderSourceRescanMinutesHelp);
        yield return nameof(FolderSourceCatalogLimitMiBLabel); yield return nameof(FolderSourceCatalogLimitMiBHint); yield return nameof(FolderSourceCatalogLimitMiBHelp);
        yield return nameof(FolderSourceGalleryHashWindowLabel); yield return nameof(FolderSourceGalleryHashWindowHint); yield return nameof(FolderSourceGalleryHashWindowHelp);
    }
}
