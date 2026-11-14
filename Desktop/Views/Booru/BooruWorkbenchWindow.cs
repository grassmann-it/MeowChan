using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
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

    private readonly BooruWorkbenchView _view;
    private readonly Button _close = new();
    private bool _disposed;

    internal BooruWorkbenchWindow(BooruWorkbenchServices services) : this(new BooruWorkbenchModel(services), ownsModel: true) { }

    internal BooruWorkbenchWindow(BooruWorkbenchModel model, bool ownsModel)
    {
        Name = "BooruWorkbenchWindow";
        // Modals plan MD3: a tool window, not a dialog: modeless on the XL step, so it keeps minimize and its taskbar entry.
        // The title stands in the title bar only (owner decision E1).
        MeowChan.Desktop.Controls.DialogWindow.Configure(this, DialogSize.XL, modal: false);

        _view = new BooruWorkbenchView(model, ownsModel, (header, body, footer) =>
            DialogFrame.Build(header, new Border { Child = body, Classes = { "workspace-dialog-body" } },
                BuildFooter(footer), DialogFrame.ErrorStrip(new TextBlock())));
        MeowChan.Desktop.Controls.DialogWindow.Frame(this, _view, modal: false);
        _view.ChromeChanged += OnChromeChanged;
        OnChromeChanged();
        _close.Click += (_, _) => Close();
        Opened += async (_, _) => await _view.ActivateAsync();
        Closed += (_, _) => Dispose();
        // Escape empties the typed query first (SearchBox), then answers an open removal question, then closes (MD3 DialogEscape).
        MeowChan.Desktop.Controls.DialogEscape.Attach(this, tryCancelLocal: CancelEscapeLocally);
    }

    internal BooruWorkbenchModel Model => _view.Model;
    internal Func<string, Task<bool>>? ShowInMedia { get => _view.ShowInMedia; set => _view.ShowInMedia = value; }
    internal Func<string, bool>? IsPostSavedInMedia { get => _view.IsPostSavedInMedia; set => _view.IsPostSavedInMedia = value; }
    internal void ApplyWorkView(WorkView view, bool viewChanged) => _view.ApplyWorkView(view, viewChanged);
    internal void ShowQuery(string query) => _view.ShowQuery(query);
    internal void AddTagToSearch(string tag) => _view.AddTagToSearch(tag);

    private Control BuildFooter(Control notes)
    {
        // The notes stand left (status), Close is the way out right-most.
        return MeowChan.Desktop.Controls.DialogFooter.Build(_close, status: notes);
    }

    private void OnChromeChanged()
    {
        Title = _view.TitleText;
        _close.Content = Texts.T("action.close");
    }

    // Escape does nothing here while a shortcut is recorded or the mode drop-down is open (they use it themselves); the SearchBox
    // empties a typed query itself; an open removal question is answered "Cancel" first; only then does Escape close the window.
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
