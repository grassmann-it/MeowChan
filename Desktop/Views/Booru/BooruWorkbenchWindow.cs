using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.Desktop.Booru;

/// <summary>Dialog host for the reusable workbench. No model, request, timer or bitmap ownership lives here.</summary>
internal sealed class BooruWorkbenchWindow : Window, IDisposable
{
    // Compatibility aliases: existing callers and smoke selectors keep their exact API and control names.
    internal const string TabsName = BooruWorkbenchView.TabsName;
    internal const string ModeName = BooruWorkbenchView.ModeName;
    internal const string InputName = BooruWorkbenchView.InputName;
    internal const string SubmitName = BooruWorkbenchView.SubmitName;
    internal const string CancelName = BooruWorkbenchView.CancelName;
    internal const string RetryName = BooruWorkbenchView.RetryName;
    internal const string QueryName = BooruWorkbenchView.QueryName;
    internal const string StatusName = BooruWorkbenchView.StatusName;
    internal const string PreviousName = BooruWorkbenchView.PreviousName;
    internal const string NextName = BooruWorkbenchView.NextName;
    internal const string CreateWatcherName = BooruWorkbenchView.CreateWatcherName;
    internal const string ResultsName = BooruWorkbenchView.ResultsName;
    internal const string WatchersName = BooruWorkbenchView.WatchersName;
    internal const string RemoveNoteName = BooruWorkbenchView.RemoveNoteName;
    internal const string NoFilterNoteName = BooruWorkbenchView.NoFilterNoteName;
    internal const string LoadPrefix = BooruWorkbenchView.LoadPrefix;
    internal const string CancelLoadPrefix = BooruWorkbenchView.CancelLoadPrefix;
    internal const string RetryLoadPrefix = BooruWorkbenchView.RetryLoadPrefix;
    internal const string CheckPrefix = BooruWorkbenchView.CheckPrefix;
    internal const string PausePrefix = BooruWorkbenchView.PausePrefix;
    internal const string ResumePrefix = BooruWorkbenchView.ResumePrefix;
    internal const string RemovePrefix = BooruWorkbenchView.RemovePrefix;
    internal const string RestartPrefix = BooruWorkbenchView.RestartPrefix;
    internal const string RemoveConfirmPrefix = BooruWorkbenchView.RemoveConfirmPrefix;
    internal const string RemoveCancelPrefix = BooruWorkbenchView.RemoveCancelPrefix;
    internal const string OpenPostPrefix = BooruWorkbenchView.OpenPostPrefix;
    internal const string CopyLinkPrefix = BooruWorkbenchView.CopyLinkPrefix;
    internal const string WatcherOpenPrefix = BooruWorkbenchView.WatcherOpenPrefix;
    internal const string WatcherCopyPrefix = BooruWorkbenchView.WatcherCopyPrefix;
    internal const string LinkNoteName = BooruWorkbenchView.LinkNoteName;
    internal const string InputIssueName = BooruWorkbenchView.InputIssueName;
    internal const string NoticeName = BooruWorkbenchView.NoticeName;
    internal const string WatcherNoticeName = BooruWorkbenchView.WatcherNoticeName;
    internal const string ResetCooldownName = BooruWorkbenchView.ResetCooldownName;
    internal const string ResultTagPrefix = BooruWorkbenchView.ResultTagPrefix;
    internal const string DetailsPrefix = BooruWorkbenchView.DetailsPrefix;
    internal const string MoreTagsPrefix = BooruWorkbenchView.MoreTagsPrefix;
    internal const string ShowInMediaPrefix = BooruWorkbenchView.ShowInMediaPrefix;
    internal const string WatcherShowInMediaPrefix = BooruWorkbenchView.WatcherShowInMediaPrefix;
    internal const string CatalogStorageName = BooruWorkbenchView.CatalogStorageName;
    // The context menu entry ids of the hit, watcher and tag menus (BooruWorkbenchView.CardMenus.cs).
    internal const string MenuLoad = BooruWorkbenchView.MenuLoad;
    internal const string MenuShowInMedia = BooruWorkbenchView.MenuShowInMedia;
    internal const string MenuOpenPost = BooruWorkbenchView.MenuOpenPost;
    internal const string MenuCopyLink = BooruWorkbenchView.MenuCopyLink;
    internal const string MenuCheck = BooruWorkbenchView.MenuCheck;
    internal const string MenuPause = BooruWorkbenchView.MenuPause;
    internal const string MenuResume = BooruWorkbenchView.MenuResume;
    internal const string MenuRestart = BooruWorkbenchView.MenuRestart;
    internal const string MenuRemove = BooruWorkbenchView.MenuRemove;
    internal const string MenuTagAdd = BooruWorkbenchView.MenuTagAdd;
    internal const string MenuTagExclude = BooruWorkbenchView.MenuTagExclude;

    private readonly BooruWorkbenchView _view;
    private readonly Button _close = new();
    private bool _disposed;

    internal BooruWorkbenchWindow(BooruWorkbenchServices services) : this(new BooruWorkbenchModel(services), ownsModel: true) { }

    internal BooruWorkbenchWindow(BooruWorkbenchModel model, bool ownsModel)
    {
        Name = "BooruWorkbenchWindow";
        // MD3: the workbench is a modeless XL tool window, so it keeps its minimize button and taskbar entry.
        // DialogWindow owns the size step, owner centring and first-open screen fit; the title remains in the caption.
        DialogWindow.Configure(this, DialogSize.XL, modal: false);

        _view = new BooruWorkbenchView(model, ownsModel, (header, body, footer) =>
            DialogFrame.Build(header, new Border { Child = body, Classes = { "workspace-dialog-body" } },
                BuildFooter(footer), DialogFrame.ErrorStrip(new TextBlock())));
        DialogWindow.Frame(this, _view, modal: false);
        _view.ChromeChanged += OnChromeChanged;
        OnChromeChanged();
        _close.Click += (_, _) => Close();
        Opened += async (_, _) => await _view.ActivateAsync();
        Closed += (_, _) => Dispose();
        // SearchBox consumes Escape while a draft has text; this policy handles the empty-field/window levels.
        DialogEscape.Attach(this, tryCancelLocal: CancelEscapeLocally);
    }

    internal BooruWorkbenchModel Model => _view.Model;
    internal Func<string, Task<bool>>? ShowInMedia { get => _view.ShowInMedia; set => _view.ShowInMedia = value; }
    internal Func<string, bool>? IsPostSavedInMedia { get => _view.IsPostSavedInMedia; set => _view.IsPostSavedInMedia = value; }
    internal void ApplyWorkView(WorkView view, bool viewChanged) => _view.ApplyWorkView(view, viewChanged);
    internal void ShowQuery(string query) => _view.ShowQuery(query);
    internal void AddTagToSearch(string tag) => _view.AddTagToSearch(tag);

    private Control BuildFooter(Control notes) => DialogFooter.Build(_close, status: notes);

    private void OnChromeChanged()
    {
        Title = _view.TitleText;
        _close.Content = Texts.T("action.close");
    }

    // Escape levels: shortcut capture and the mode list stay in control, then an open removal question is cancelled;
    // when none claims it, DialogEscape closes the modeless window. SearchBox clears a non-empty draft before this runs.
    private bool CancelEscapeLocally() =>
        MeowChan.Desktop.Commands.ShortcutService.IsCapturing || _view.IsModeDropDownOpen || _view.TryCancelRemoval();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.ChromeChanged -= OnChromeChanged;
        _view.Dispose();
    }
}
