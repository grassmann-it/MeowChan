using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan;

internal enum ReaderGroupsNoticeReason { FirstMemberUnavailable }
internal sealed record ReaderGroupsDialogOrigin(Guid? GroupId, ResourceId? MemberId, Vector Scroll, string? FocusName,
    Vector GroupsScroll = default, Vector MembersScroll = default, string? Notice = null,
    ReaderGroupsNoticeReason? NoticeReason = null);
internal sealed record ReaderGroupReadRequest(Guid GroupId, IReadOnlyList<ResourceId> Threads, ReaderGroupsDialogOrigin Origin);

public partial class ReaderGroupsDialog : Window
{
    private readonly DesktopSessionHost _host;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _searchCancellation;
    private readonly CommandRouter _router;
    private readonly Dictionary<string, Button> _buttons = new();
    private ReaderGroupsLoadResult? _loaded;
    private ReaderGroupsDraft? _draft;
    private Guid? _selected;
    private bool _rendering, _busy, _closed, _allowClose, _confirming, _conflict;
    private int _searchRequest, _membersRequest;
    private ReaderGroupsDialogOrigin? _origin;
    private ReaderGroupsDialogOrigin? _pendingMemberOrigin;
    private readonly Dictionary<ResourceId, ReaderGroupCandidate> _memberInfo = new();
    private readonly List<ResourceId> _candidateSelectionOrder = new();
    private IReadOnlyList<ReaderGroupCandidate> _candidateItems = Array.Empty<ReaderGroupCandidate>();
    private readonly Button _reload = new(), _recover = new(), _close = new();
    internal abstract class DisplayRow : INotifyPropertyChanged
    {
        private string _display = "";
        public string Display
        {
            get => _display;
            protected set
            {
                if (_display == value) return;
                _display = value; PropertyChanged?.Invoke(this, new(nameof(Display)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => Display;
    }
    internal sealed class GroupRow(ReaderGroup group) : DisplayRow
    {
        private ReaderGroup _group = group;
        public Guid Id => _group.Id;
        internal void Refresh(ReaderGroup? current = null)
        {
            if (current != null) _group = current;
            Display = $"{(_group.Name.Length == 0 ? Texts.T("readerGroups.newGroup") : _group.Name)} · {_group.Members.Count} · {_group.Id.ToString("N")[..8]}";
        }
    }
    internal sealed class MemberRow(ReaderGroupCandidate item) : DisplayRow
    {
        public ReaderGroupCandidate Item { get; } = item;
        internal void Refresh() => Display = $"{Identity(Item.Id)}{(string.IsNullOrWhiteSpace(Item.Title) ? "" : " · " + Item.Title)} · " +
            Texts.T(Item.HasLocalPosts ? "readerGroups.localPosts" : Item.Watched ? "readerGroups.watchedNoPosts" : "readerGroups.unavailableLocally");
    }
    // Provenance is explicit. Raw diagnostics/return notices must never be reverse-mapped.
    internal sealed class PresentationText
    {
        private readonly string? _key;
        private readonly string? _raw;
        private readonly object?[] _arguments;
        private readonly long? _count;
        private PresentationText(string? key, string? raw, object?[] arguments, long? count = null)
        { _key = key; _raw = raw; _arguments = (object?[])arguments.Clone(); _count = count; }
        internal static PresentationText Localized(string key, params object?[] arguments) => new(key, null, arguments);
        /// <summary>A text whose plural form follows <paramref name="count"/>; {0} is the count unless arguments are given.</summary>
        internal static PresentationText Counted(string key, long count, params object?[] arguments) => new(key, null, arguments, count);
        internal static PresentationText Raw(string raw) => new(null, raw, Array.Empty<object?>());
        internal string Render() => _key == null ? _raw! : _count is { } count ? Texts.Count(_key, count, _arguments) : Texts.T(_key, _arguments);
    }
    // Only explicit host provenance is localized; legacy/raw text is never inferred from its contents.
    internal static PresentationText OriginNoticePresentation(ReaderGroupsDialogOrigin? origin) =>
        origin?.NoticeReason == ReaderGroupsNoticeReason.FirstMemberUnavailable
            ? PresentationText.Localized("readerGroups.firstMemberUnavailable")
            : origin?.Notice is string notice ? PresentationText.Raw(notice)
            : PresentationText.Localized("readerGroups.hint");

    private PresentationText? _message, _searchStatus;
    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;
    private ReaderGroup? SelectedGroup => _draft?.Groups.FirstOrDefault(group => group.Id == _selected);
    private ResourceId? SelectedMember => (C<ListBox>("GroupMembers").SelectedItem as MemberRow)?.Item.Id;

    internal ReaderGroupsDialog(DesktopSessionHost host, ReaderGroupsDialogOrigin? origin = null)
    {
        _host = host; _origin = origin;
        AvaloniaXamlLoader.Load(this);
        // Modals plan MD3: a modal dialog on the L step (size, centring, screen fit, no taskbar entry, no minimize).
        DialogWindow.Configure(this, DialogSize.L, modal: true);
        DialogWindow.Wrap(this, modal: true);
        C<ListBox>("Groups").ItemTemplate = new FuncDataTemplate<GroupRow>((row, _) => RowText(row));
        foreach (string list in new[] { "GroupMembers", "GroupCandidates" })
            C<ListBox>(list).ItemTemplate = new FuncDataTemplate<MemberRow>((row, _) => RowText(row));
        _router = new CommandRouter(this);
        foreach ((string action, string panel) in new[] { ("create", "GroupActions"), ("rename", "GroupActions"), ("delete", "GroupActions"),
            ("remove-member", "MemberActions"), ("move-up", "MemberActions"), ("move-down", "MemberActions"),
            ("add-members", "CandidateActions"), ("save", "GroupFooter"), ("read", "GroupFooter") })
        {
            string id = "reader-groups." + action;
            Button button = CommandBindings.CreateButton(id, () => _ = ExecuteCommandAsync(id), () => CanExecute(id));
            button.Name = "GroupCommand_" + action.Replace('-', '_');
            if (action == "save") button.Classes.Add("primary");
            if (action == "delete" || action == "remove-member") button.Classes.Add("danger");
            _buttons[id] = button;
            C<WrapPanel>(panel).Children.Add(button);
            _router.Register(new CommandRegistration(id, this, () => IsVisible && !_closed && !_confirming,
                () => CanExecute(id), () => _ = ExecuteCommandAsync(id), Priority: 300));
        }
        _reload.Click += async (_, _) => { if (await DiscardAsync()) await LoadAsync(); };
        _recover.Click += async (_, _) => await RecoverAsync();
        _close.Click += async (_, _) => await RequestCloseAsync();
        // Save is the primary action right-most; Close stays left of it, the others before.
        DialogFooter.Fill(C<Border>("DialogFrameFooter"), _buttons["reader-groups.save"],
            new Control[] { _reload, _recover, _buttons["reader-groups.read"], _close });
        C<ListBox>("Groups").SelectionChanged += async (_, _) => await ChangeGroupAsync();
        C<TextBox>("GroupName").TextChanged += (_, _) =>
        {
            if (_rendering || !_selected.HasValue) return;
            _draft?.Rename(_selected.Value, C<TextBox>("GroupName").Text ?? ""); RefreshActions();
        };
        C<ListBox>("GroupMembers").SelectionChanged += (_, _) => RefreshActions();
        C<ListBox>("GroupCandidates").SelectionChanged += (_, _) =>
        {
            if (_rendering) return;
            ResourceId[] selected = C<ListBox>("GroupCandidates").SelectedItems?.Cast<MemberRow>().Select(row => row.Item.Id).ToArray() ?? Array.Empty<ResourceId>();
            _candidateSelectionOrder.RemoveAll(id => !selected.Contains(id));
            foreach (ResourceId id in selected) if (!_candidateSelectionOrder.Contains(id)) _candidateSelectionOrder.Add(id);
            RefreshActions();
        };
        C<TextBox>("GroupSearch").TextChanged += async (_, _) => await SearchAsync();
        AddHandler(KeyDownEvent, (_, e) => _router.Route(e), RoutingStrategies.Bubble);
        // Fixed dismissal only, independent of all freely assigned application gestures (the dialog policy): the open
        // candidate list is dismissed first, then the dialog asks to close.
        DialogEscape.Attach(this, tryCancelLocal: () =>
        {
            if (!C<ListBox>("GroupCandidates").IsVisible || C<ListBox>("GroupCandidates").ItemCount == 0) return false;
            C<ListBox>("GroupCandidates").IsVisible = false; C<TextBox>("GroupSearch").Focus();
            return true;
        }, close: () => _ = RequestCloseAsync());
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Dispatcher.UIThread.Post(async () => await RequestCloseAsync());
        };
        Opened += async (_, _) =>
        {
            if (Owner != null) { MaxWidth = Math.Max(480, Owner.Bounds.Width); MaxHeight = Math.Max(480, Owner.Bounds.Height); }
            await LoadAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); _searchCancellation?.Cancel(); _router.Dispose();
            _host.StateChanged -= HostChanged; Texts.Changed -= LanguageChanged;
            _lifetime.Dispose();
        };
        _host.StateChanged += HostChanged; Texts.Changed += LanguageChanged;
        SizeChanged += (_, _) => ArrangeWorkspace();
        Localize(); ArrangeWorkspace();
    }

    // Keep the group list beside its editor when it fits; minimum windows retain
    // the same controls, names, draft and return offsets in a stacked workspace.
    private void ArrangeWorkspace()
    {
        bool stacked = ClientSize.Width < 680;
        Grid workspace = C<Grid>("GroupWorkspace");
        workspace.ColumnDefinitions = new(stacked ? "*" : "220,*");
        // Stacked: the group list takes only what a few rows need so the editor, with its member actions, keeps the rest.
        workspace.RowDefinitions = new(stacked ? "Auto,*" : "*");
        ScrollViewer editor = C<ScrollViewer>("GroupEditorScroll");
        Grid.SetColumn(editor, stacked ? 0 : 1); Grid.SetRow(editor, stacked ? 1 : 0);
        C<ListBox>("Groups").Height = double.NaN;
        C<ListBox>("Groups").MaxHeight = stacked ? 104 : double.PositiveInfinity;
    }

    private void HostChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && _host.State != DesktopSessionState.Ready) { _allowClose = true; Close(); }
    });
    internal static TextBlock RowText(DisplayRow? row)
    {
        TextBlock text = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(4, 8) };
        // Templates are also built without an item; a null Source would log a binding error each time.
        if (row != null) text.Bind(TextBlock.TextProperty, new Binding(nameof(DisplayRow.Display)) { Source = row });
        return text;
    }
    private void LanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_closed) return;
        // Relabel only: do not recreate controls, replace ItemsSource, search or resolve.
        // Keeping row identity also preserves multi-selection insertion order.
        var scrolls = this.GetVisualDescendants().OfType<ScrollViewer>()
            .Select(scroll => (Scroll: scroll, Offset: scroll.Offset)).ToArray();
        Localize();
        UpdateLayout();
        foreach (var saved in scrolls) saved.Scroll.Offset = saved.Offset;
    });
    private void Localize()
    {
        Title = Texts.T("readerGroups.title");
        C<TextBlock>("GroupNameLabel").Text = Texts.T("readerGroups.nameLabel");
        C<TextBlock>("GroupMembersLabel").Text = Texts.T("readerGroups.membersLabel");
        C<TextBlock>("GroupSearchLabel").Text = Texts.T("readerGroups.searchLabel");
        foreach (var pair in _buttons) CommandBindings.Apply(pair.Value, pair.Key);
        _reload.Content = Texts.T("readerGroups.reload"); _recover.Content = Texts.T("readerGroups.recover"); _close.Content = Texts.T("readerGroups.close");
        foreach ((string name, string label) in new[] { ("Groups", Title), ("GroupName", Texts.T("readerGroups.nameAccessible")),
            ("GroupMembers", Texts.T("readerGroups.membersAccessible")), ("GroupSearch", Texts.T("readerGroups.searchAccessible")),
            ("GroupCandidates", Texts.T("readerGroups.candidatesAccessible")),
            ("GroupSearchStatus", Texts.T("readerGroups.searchStatusAccessible")), ("GroupMessage", Texts.T("readerGroups.messageAccessible")) })
            AutomationProperties.SetName(C<Control>(name), label);
        RefreshRows(C<ListBox>("Groups").Items.OfType<GroupRow>(),
            new[] { "GroupMembers", "GroupCandidates" }.SelectMany(list => C<ListBox>(list).Items.OfType<MemberRow>()), _draft);
        RenderPresentation();
        RefreshActions();
    }

    internal static void RefreshRows(IEnumerable<GroupRow> groups, IEnumerable<MemberRow> members, ReaderGroupsDraft? draft)
    {
        foreach (GroupRow row in groups) row.Refresh(draft?.Groups.FirstOrDefault(group => group.Id == row.Id));
        foreach (MemberRow row in members) row.Refresh();
    }

    internal bool CanExecute(string id)
    {
        if (_busy || _confirming || _closed || _conflict || _host.State != DesktopSessionState.Ready || _draft == null || _loaded?.CanWrite != true) return false;
        ReaderGroup? group = SelectedGroup;
        int index = group == null || SelectedMember == null ? -1 : group.Members.ToList().IndexOf(SelectedMember!);
        return id switch
        {
            "reader-groups.create" => _draft.Groups.Count < _host.ReaderGroupLimit,
            "reader-groups.rename" or "reader-groups.delete" => group != null,
            "reader-groups.add-members" => group != null && _candidateSelectionOrder.Count > 0
                && group.Members.Concat(_candidateSelectionOrder).Distinct().Count() <= ReaderGroupsValidation.MaximumMembers
                && _draft.Groups.Sum(item => item.Members.Count) + _candidateSelectionOrder.Except(group.Members).Count() <= ReaderGroupsValidation.MaximumMemberships,
            "reader-groups.remove-member" => index >= 0,
            "reader-groups.move-up" => index > 0,
            "reader-groups.move-down" => index >= 0 && index + 1 < group!.Members.Count,
            "reader-groups.save" => !_conflict && _draft.IsDirty && DraftValid(),
            "reader-groups.read" => !_conflict && group?.Members.Count > 0,
            _ => false
        };
    }
    private bool DraftValid()
    {
        try { ReaderGroupsValidation.Copy(_draft!.Snapshot, normalizeNames: true); return true; }
        catch (Exception exception) when (exception is ArgumentException or System.IO.InvalidDataException) { return false; }
    }

    internal async Task ExecuteCommandAsync(string id)
    {
        if (!CanExecute(id)) return;
        ReaderGroup? group = SelectedGroup;
        switch (id)
        {
            case "reader-groups.create":
                if (!await DiscardAsync()) return;
                _selected = _draft!.Create().Id; Render(); C<TextBox>("GroupName").Focus(); break;
            case "reader-groups.rename": C<TextBox>("GroupName").Focus(); C<TextBox>("GroupName").SelectAll(); break;
            case "reader-groups.delete":
                if (!await ConfirmAsync(PresentationText.Localized("readerGroups.deletePrompt"), PresentationText.Localized("readerGroups.delete"))) return;
                _draft!.Groups.RemoveAll(item => item.Id == group!.Id); _selected = null; Render(); C<ListBox>("Groups").Focus(); break;
            case "reader-groups.add-members":
                ResourceId[] additions = _candidateSelectionOrder.ToArray();
                int added = additions.Except(group!.Members).Count(); _draft!.Add(group.Id, additions);
                ShowMessage(added == 0 ? PresentationText.Localized("readerGroups.alreadyMembers")
                    : PresentationText.Counted("readerGroups.added", added));
                Render(additions.LastOrDefault()); C<ListBox>("GroupMembers").Focus(); break;
            case "reader-groups.remove-member":
                int index = group!.Members.ToList().IndexOf(SelectedMember!); _draft!.Remove(group.Id, SelectedMember!);
                ResourceId? next = SelectedGroup!.Members.ElementAtOrDefault(Math.Min(index, SelectedGroup.Members.Count - 1));
                Render(next); C<ListBox>("GroupMembers").Focus(); break;
            case "reader-groups.move-up": case "reader-groups.move-down":
                ResourceId? member = SelectedMember; _draft!.Move(group!.Id, group.Members.ToList().IndexOf(member!), id.EndsWith("move-up", StringComparison.Ordinal) ? -1 : 1);
                Render(member); C<ListBox>("GroupMembers").Focus(); break;
            case "reader-groups.save": await SaveAsync(); break;
            case "reader-groups.read": await ReadAsync(); break;
        }
    }

    private async Task LoadAsync()
    {
        if (_busy || _closed) return;
        _busy = true; RefreshActions();
        try
        {
            ReaderGroupsLoadResult loaded = await _host.LoadReaderGroupsAsync(_lifetime.Token);
            if (_closed) return;
            _loaded = loaded; _conflict = false;
            _draft = loaded.Snapshot == null ? null : new(loaded.Snapshot);
            _selected = _origin?.GroupId ?? _selected;
            if (_draft?.Groups.Any(group => group.Id == _selected) != true) _selected = null;
            ShowMessage(loaded.CanWrite ? OriginNoticePresentation(_origin) : LoadError(loaded));
            Render(_origin?.MemberId);
            _pendingMemberOrigin = _origin;
            _ = SearchAsync();
        }
        catch (OperationCanceledException) when (_closed || _lifetime.IsCancellationRequested) { }
        catch (Exception exception) { ShowMessage(PresentationText.Localized("readerGroups.loadFailed", exception.Message)); }
        finally
        {
            _busy = false; RefreshActions();
            if (!_closed)
            {
                ReaderGroupsDialogOrigin? origin = _origin; _origin = null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_closed) return;
                    if (origin != null)
                    {
                        C<ScrollViewer>("GroupEditorScroll").Offset = origin.Scroll;
                        RestoreListOffset("Groups", origin.GroupsScroll);
                        Control? focus = _selected.HasValue
                            ? _buttons.Values.FirstOrDefault(button => button.Name == origin.FocusName)
                                ?? this.FindControl<Control>(origin.FocusName ?? "Groups") : C<ListBox>("Groups");
                        (focus ?? C<ListBox>("Groups")).Focus();
                    }
                    else if (_draft?.Groups.Count > 0) C<ListBox>("Groups").Focus();
                    else if (_loaded?.State == ReaderGroupsLoadState.RecoveryAvailable) _recover.Focus();
                    else if (_loaded?.CanWrite == true) _buttons["reader-groups.create"].Focus();
                    else _reload.Focus();
                }, DispatcherPriority.Loaded);
            }
        }
    }
    private static PresentationText LoadError(ReaderGroupsLoadResult loaded) => PresentationText.Localized(loaded.State switch
    {
        ReaderGroupsLoadState.FutureVersion => "readerGroups.futureVersion",
        ReaderGroupsLoadState.RecoveryAvailable => "readerGroups.recoveryAvailable",
        _ => "readerGroups.unavailable"
    }, loaded.FilePath, loaded.Error);

    private async Task SaveAsync()
    {
        bool failed = false;
        _busy = true; RefreshActions();
        try
        {
            ReaderGroupsSnapshot saved = await _host.SaveReaderGroupsAsync(_draft!.Snapshot, _draft.Revision, _lifetime.Token);
            if (_closed) return;
            _draft.Accept(saved); Render(); ShowMessage(PresentationText.Localized("readerGroups.saved"));
        }
        catch (ReaderGroupsConflictException)
        {
            _conflict = true;
            try { _loaded = await _host.LoadReaderGroupsAsync(_lifetime.Token); } catch (Exception) { /* Retain draft even if reload fails. */ }
            ShowMessage(PresentationText.Localized("readerGroups.conflict"));
        }
        catch (OperationCanceledException) when (_closed || _lifetime.IsCancellationRequested) { }
        catch (ProfileBackupBlockedException blocked) { failed = true; ShowMessage(PresentationText.Localized("readerGroups.saveFailed", StoreBackupBlockedText.For(blocked))); C<TextBox>("GroupName").Focus(); }
        catch (Exception exception) { failed = true; ShowMessage(PresentationText.Localized("readerGroups.saveFailed", exception.Message)); C<TextBox>("GroupName").Focus(); }
        finally { _busy = false; RefreshActions(); if (failed && !_closed) C<TextBox>("GroupName").Focus(); }
    }
    private async Task RecoverAsync()
    {
        if (_busy || _loaded?.RecoveryToken is not string token || !await ConfirmAsync(
            PresentationText.Localized("readerGroups.recoverPrompt"), PresentationText.Localized("readerGroups.restore"))) return;
        _busy = true; RefreshActions();
        try { await _host.RestoreReaderGroupsBackupAsync(token, _lifetime.Token); }
        catch (Exception exception) { ShowMessage(exception.Message); return; }
        finally { _busy = false; RefreshActions(); }
        await LoadAsync();
    }
    private async Task ReadAsync()
    {
        if (_draft!.IsDirty && !await DiscardAsync()) return;
        ReaderGroup? group = SelectedGroup;
        if (group == null || group.Members.Count == 0) { Render(); return; }
        _busy = true; RefreshActions();
        try
        {
            IReadOnlyList<ReaderGroupCandidate> members = await _host.ResolveReaderGroupMembersAsync(group.Members, _lifetime.Token);
            if (_closed) return;
            ReaderGroupCandidate[] missing = members.Where(item => !item.HasLocalPosts).ToArray();
            ResourceId[] available = members.Where(item => item.HasLocalPosts).Select(item => item.Id).ToArray();
            string detail = string.Join("\n", missing.Select(item => Identity(item.Id)));
            if (available.Length == 0) { ShowMessage(PresentationText.Localized("readerGroups.noReadable", detail)); return; }
            if (missing.Length > 0 && !await ConfirmAsync(PresentationText.Counted("readerGroups.missingMembers", missing.Length, missing.Length, detail),
                PresentationText.Localized("readerGroups.readAvailable"))) return;
            if (_closed) return;
            ReaderGroupsDialogOrigin origin = new(group.Id, SelectedMember, C<ScrollViewer>("GroupEditorScroll").Offset,
                (FocusManager?.GetFocusedElement() as Control)?.Name, ListOffset("Groups"), ListOffset("GroupMembers"));
            _allowClose = true; Close(new ReaderGroupReadRequest(group.Id, Array.AsReadOnly(available), origin));
        }
        catch (OperationCanceledException) when (_closed || _lifetime.IsCancellationRequested) { }
        catch (Exception exception) { ShowMessage(exception.Message); }
        finally { _busy = false; RefreshActions(); }
    }

    private async Task ChangeGroupAsync()
    {
        if (_rendering || _closed) return;
        Guid? desired = (C<ListBox>("Groups").SelectedItem as GroupRow)?.Id;
        if (desired == _selected) return;
        // Restore selection while the asynchronous discard decision is pending.
        _rendering = true; C<ListBox>("Groups").SelectedItem = C<ListBox>("Groups").Items.OfType<GroupRow>().FirstOrDefault(row => row.Id == _selected); _rendering = false;
        if (_busy || _confirming || !await DiscardAsync()) return;
        _selected = desired; Render();
    }
    private async Task<bool> DiscardAsync()
    {
        if (_draft?.IsDirty != true) return true;
        if (!await ConfirmAsync(PresentationText.Localized("readerGroups.discardPrompt"),
            PresentationText.Localized("readerGroups.discard"), PresentationText.Localized("readerGroups.keepEditing"))) return false;
        _draft.Discard(); Render(); return true;
    }
    private async Task RequestCloseAsync()
    {
        if (_busy || _confirming || _closed || !await DiscardAsync()) return;
        _allowClose = true; Close();
    }
    private async Task<bool> ConfirmAsync(PresentationText message, PresentationText confirm, PresentationText? cancel = null)
    {
        if (_confirming || _closed) return false;
        _confirming = true; RefreshActions();
        try { return await ReaderGroupsPrompt.ShowAsync(this, message, confirm, cancel ?? PresentationText.Localized("readerGroups.cancel")); }
        finally { _confirming = false; RefreshActions(); }
    }

    private void Render(ResourceId? member = null)
    {
        member ??= SelectedMember;
        _rendering = true;
        try
        {
            var rows = _draft?.Groups.Select(group => { var row = new GroupRow(group); row.Refresh(); return row; }).ToArray() ?? Array.Empty<GroupRow>();
            C<ListBox>("Groups").ItemsSource = rows; C<ListBox>("Groups").SelectedItem = rows.FirstOrDefault(row => row.Id == _selected);
            C<TextBox>("GroupName").Text = SelectedGroup?.Name ?? "";
            var members = SelectedGroup?.Members.Select(id => Member(_memberInfo.GetValueOrDefault(id) ?? new(id, "", false))).ToArray() ?? Array.Empty<MemberRow>();
            C<ListBox>("GroupMembers").ItemsSource = members;
            C<ListBox>("GroupMembers").SelectedItem = members.FirstOrDefault(row => row.Item.Id == member);
        }
        finally { _rendering = false; }
        RefreshActions(); _ = ResolveMembersAsync();
    }
    private Vector ListOffset(string name) => C<ListBox>(name).GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset ?? default;
    private void RestoreListOffset(string name, Vector offset)
    {
        if (C<ListBox>(name).GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is ScrollViewer scroll) scroll.Offset = offset;
    }
    private void RefreshActions()
    {
        string name = SelectedGroup?.Name.Trim() ?? "";
        string? nameError = SelectedGroup == null ? null : name.Length == 0
            ? Texts.T("readerGroups.nameRequired")
            : name.EnumerateRunes().Count() > 120 || name.Any(char.IsControl)
                ? Texts.T("readerGroups.nameInvalid") : null;
        C<TextBlock>("GroupNameError").Text = nameError;
        C<TextBlock>("GroupNameError").IsVisible = nameError != null;
        AutomationProperties.SetHelpText(C<TextBox>("GroupName"), nameError ?? C<TextBlock>("GroupNameLabel").Text);

        foreach (Button button in _buttons.Values) CommandBindings.Refresh(button);
        bool editable = !_busy && !_confirming && _loaded?.CanWrite == true && !_conflict;
        C<TextBox>("GroupName").IsEnabled = editable && SelectedGroup != null;
        C<TextBox>("GroupSearch").IsEnabled = editable && SelectedGroup != null;
        C<ListBox>("GroupCandidates").IsEnabled = editable && SelectedGroup != null;
        C<ListBox>("Groups").IsEnabled = !_busy && !_confirming;
        _reload.IsEnabled = !_busy && !_confirming; _close.IsEnabled = !_busy && !_confirming;
        _recover.IsVisible = _loaded?.State == ReaderGroupsLoadState.RecoveryAvailable;
        _recover.IsEnabled = !_busy && !_confirming;
    }
    private async Task ResolveMembersAsync()
    {
        ReaderGroup? group = SelectedGroup;
        int request = ++_membersRequest;
        if (group == null || _closed) return;
        try
        {
            var members = await _host.ResolveReaderGroupMembersAsync(group.Members, _lifetime.Token);
            if (_closed || request != _membersRequest || SelectedGroup?.Id != group.Id) return;
            foreach (var item in members) _memberInfo[item.Id] = item;
            ResourceId? selected = SelectedMember;
            var rows = members.Select(Member).ToArray(); C<ListBox>("GroupMembers").ItemsSource = rows;
            C<ListBox>("GroupMembers").SelectedItem = rows.FirstOrDefault(row => row.Item.Id == selected);
            if (_pendingMemberOrigin is { } origin)
            {
                _pendingMemberOrigin = null;
                await Dispatcher.UIThread.InvokeAsync(() => { if (!_closed) RestoreListOffset("GroupMembers", origin.MembersScroll); }, DispatcherPriority.Loaded);
            }
        }
        catch (OperationCanceledException) when (_closed || _lifetime.IsCancellationRequested) { }
        catch (Exception exception) { if (!_closed && request == _membersRequest) ShowMessage(exception.Message); }
    }
    private async Task SearchAsync()
    {
        int request = ++_searchRequest;
        _searchCancellation?.Cancel();
        if (_closed) return;
        C<ListBox>("GroupCandidates").ItemsSource = Array.Empty<MemberRow>();
        _candidateItems = Array.Empty<ReaderGroupCandidate>();
        _candidateSelectionOrder.Clear();
        _searchStatus = null; RenderPresentation(); RefreshActions();
        using CancellationTokenSource search = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _searchCancellation = search;
        string text = C<TextBox>("GroupSearch").Text ?? "";
        try
        {
            await Task.Delay(180, search.Token);
            ReaderGroupCandidatePage page = await _host.FindReaderGroupCandidatesAsync(text, 50, search.Token);
            if (_closed || request != _searchRequest) return;
            _candidateSelectionOrder.Clear();
            _candidateItems = page.Items;
            C<ListBox>("GroupCandidates").ItemsSource = _candidateItems.Select(Member).ToArray();
            C<ListBox>("GroupCandidates").IsVisible = true;
            _searchStatus = PresentationText.Counted(page.HasMore || text.Length == 0
                ? "readerGroups.matchesNarrow" : "readerGroups.matches", page.Items.Count);
            RenderPresentation();
            RefreshActions();
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_closed && request == _searchRequest)
            { _searchStatus = PresentationText.Raw(exception.Message); RenderPresentation(); }
        }
        finally { if (ReferenceEquals(_searchCancellation, search)) _searchCancellation = null; }
    }
    private static string Identity(ResourceId id) => $"{id.Source}/{id.Board}/{id.Thread}";
    private static MemberRow Member(ReaderGroupCandidate item)
    { var row = new MemberRow(item); row.Refresh(); return row; }
    private void ShowMessage(string text) => ShowMessage(PresentationText.Raw(text));
    private void ShowMessage(PresentationText text)
    { if (!_closed) { _message = text; RenderPresentation(); } }
    private void RenderPresentation()
    {
        if (_closed) return;
        C<TextBlock>("GroupMessage").Text = _message?.Render();
        C<TextBlock>("GroupSearchStatus").Text = _searchStatus?.Render();
    }
}

internal static class ReaderGroupsPrompt
{
    internal static async Task<bool> ShowAsync(Window owner, ReaderGroupsDialog.PresentationText message,
        ReaderGroupsDialog.PresentationText accept, ReaderGroupsDialog.PresentationText cancel)
    {
        // Modals plan MD1: a small modal confirmation whose height follows the message.
        Window prompt = DialogWindow.Create(Texts.T("readerGroups.title"), DialogSize.S, modal: true, heightToContent: true);
        bool closed = false;
        // Invalidate before dispatch: a queued click cannot approve old-language prose.
        using LanguageLifetime language = new(() =>
            Dispatcher.UIThread.Post(() => { if (!closed) prompt.Close(false); }));
        prompt.Title = Texts.T("readerGroups.title");
        Button yes = new() { Content = accept.Render() }, no = new() { Content = cancel.Render() };
        yes.Click += (_, _) => prompt.Close(language.IsCurrent);
        no.Click += (_, _) => prompt.Close(false);
        prompt.Closed += (_, _) => closed = true;
        TextBlock text = new() { Text = message.Render(), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 16) };
        // The title stands in the title bar only; accept is the primary action right-most, cancel stays the default focus.
        DialogWindow.Frame(prompt, DialogFrame.Build(header: null, text, DialogFooter.Build(yes, new Control[] { no })), modal: true);
        prompt.Opened += (_, _) => no.Focus();
        // Fixed dialog dismissal, independent of all freely assigned application gestures.
        DialogEscape.Attach(prompt, close: () => prompt.Close(false));
        try
        {
            if (!language.IsCurrent) return false;
            bool accepted = await prompt.ShowDialog<bool>(owner);
            return accepted && language.IsCurrent;
        }
        finally { closed = true; }
    }

    internal sealed class LanguageLifetime : IDisposable
    {
        private readonly Action _dismiss;
        private int _invalidated;
        internal LanguageLifetime(Action dismiss) { _dismiss = dismiss; Texts.Changed += Changed; }
        internal bool IsCurrent => Volatile.Read(ref _invalidated) == 0;
        private void Changed(object? sender, EventArgs e)
        {
            Interlocked.Exchange(ref _invalidated, 1);
            _dismiss();
        }
        public void Dispose() => Texts.Changed -= Changed;
    }
}
