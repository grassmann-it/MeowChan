using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan;

internal sealed record ReaderGroupsDialogOrigin(Guid? GroupId, ResourceId? MemberId, Vector Scroll, string? FocusName,
    Vector GroupsScroll = default, Vector MembersScroll = default, string? Notice = null);
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
    private readonly Button _reload = new(), _recover = new(), _close = new();
    private sealed record GroupRow(Guid Id, string Display) { public override string ToString() => Display; }
    private sealed record MemberRow(ReaderGroupCandidate Item, string Display) { public override string ToString() => Display; }
    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;
    private ReaderGroup? SelectedGroup => _draft?.Groups.FirstOrDefault(group => group.Id == _selected);
    private ResourceId? SelectedMember => (C<ListBox>("GroupMembers").SelectedItem as MemberRow)?.Item.Id;
    private static string L(string en, string de) => Texts.Language == "de" ? de : en;

    internal ReaderGroupsDialog(DesktopSessionHost host, ReaderGroupsDialogOrigin? origin = null)
    {
        _host = host; _origin = origin;
        AvaloniaXamlLoader.Load(this);
        C<ListBox>("Groups").ItemTemplate = new FuncDataTemplate<GroupRow>((row, _) => new TextBlock
            { Text = row?.Display, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(4, 8) });
        foreach (string list in new[] { "GroupMembers", "GroupCandidates" })
            C<ListBox>(list).ItemTemplate = new FuncDataTemplate<MemberRow>((row, _) => new TextBlock
                { Text = row?.Display, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(4, 8) });
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
        C<WrapPanel>("GroupFooter").Children.Add(_reload);
        C<WrapPanel>("GroupFooter").Children.Add(_recover);
        C<WrapPanel>("GroupFooter").Children.Add(_close);
        C<ListBox>("Groups").SelectionChanged += async (_, _) => await ChangeGroupAsync();
        C<TextBox>("GroupName").TextChanged += (_, _) =>
        {
            if (_rendering || !_selected.HasValue) return;
            _draft?.Rename(_selected.Value, C<TextBox>("GroupName").Text ?? ""); RefreshActions();
        };
        C<ListBox>("GroupMembers").SelectionChanged += (_, _) => RefreshActions();
        C<ListBox>("GroupCandidates").SelectionChanged += (_, _) =>
        {
            ResourceId[] selected = C<ListBox>("GroupCandidates").SelectedItems?.Cast<MemberRow>().Select(row => row.Item.Id).ToArray() ?? Array.Empty<ResourceId>();
            _candidateSelectionOrder.RemoveAll(id => !selected.Contains(id));
            foreach (ResourceId id in selected) if (!_candidateSelectionOrder.Contains(id)) _candidateSelectionOrder.Add(id);
            RefreshActions();
        };
        C<TextBox>("GroupSearch").TextChanged += async (_, _) => await SearchAsync();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_router.Route(e)) return;
            // Fixed dismissal only; all configurable app actions use the central router.
            if (!e.Handled && e.Key == Key.Escape)
            {
                e.Handled = true;
                if (C<ListBox>("GroupCandidates").IsVisible && C<ListBox>("GroupCandidates").ItemCount > 0)
                { C<ListBox>("GroupCandidates").IsVisible = false; C<TextBox>("GroupSearch").Focus(); }
                else _ = RequestCloseAsync();
            }
        }, RoutingStrategies.Bubble);
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Dispatcher.UIThread.Post(async () => await RequestCloseAsync());
        };
        Opened += async (_, _) =>
        {
            if (Owner != null) { MaxWidth = Math.Max(480, Owner.Bounds.Width); MaxHeight = Math.Max(400, Owner.Bounds.Height); }
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
        workspace.RowDefinitions = new(stacked ? "*,2*" : "*");
        ScrollViewer editor = C<ScrollViewer>("GroupEditorScroll");
        Grid.SetColumn(editor, stacked ? 0 : 1); Grid.SetRow(editor, stacked ? 1 : 0);
        C<ListBox>("Groups").Height = double.NaN;
    }

    private void HostChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && _host.State != DesktopSessionState.Ready) { _allowClose = true; Close(); }
    });
    private void LanguageChanged(object? sender, EventArgs e) => Localize();
    private void Localize()
    {
        Title = L("Reader groups", "Readergruppen");
        C<TextBlock>("GroupNameLabel").Text = L("Name (1–120 characters)", "Name (1–120 Zeichen)");
        C<TextBlock>("GroupMembersLabel").Text = L("Members · reading order", "Mitglieder · Lesereihenfolge");
        C<TextBlock>("GroupSearchLabel").Text = L("Add local threads: title or source/board/thread", "Lokale Threads hinzufügen: Titel oder Quelle/Board/Thread");
        foreach (var pair in _buttons) CommandBindings.Apply(pair.Value, pair.Key);
        _reload.Content = L("Reload", "Neu laden"); _recover.Content = L("Restore backup", "Sicherung wiederherstellen"); _close.Content = L("Close", "Schließen");
        foreach ((string name, string label) in new[] { ("Groups", Title), ("GroupName", L("Group name", "Gruppenname")),
            ("GroupMembers", L("Members in reading order", "Mitglieder in Lesereihenfolge")), ("GroupSearch", L("Find local threads", "Lokale Threads suchen")),
            ("GroupCandidates", L("Matching threads; select and add", "Passende Threads; auswählen und hinzufügen")),
            ("GroupSearchStatus", L("Search result status", "Suchergebnisstatus")), ("GroupMessage", L("Group status", "Gruppenstatus")) })
            AutomationProperties.SetName(C<Control>(name), label);
        if (_draft != null) Render();
    }

    internal bool CanExecute(string id)
    {
        if (_busy || _confirming || _closed || _conflict || _host.State != DesktopSessionState.Ready || _draft == null || _loaded?.CanWrite != true) return false;
        ReaderGroup? group = SelectedGroup;
        int index = group == null || SelectedMember == null ? -1 : group.Members.ToList().IndexOf(SelectedMember!);
        return id switch
        {
            "reader-groups.create" => _draft.Groups.Count < ReaderGroupsValidation.MaximumGroups,
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
                if (!await ConfirmAsync(L("Delete this group? Only its memberships are removed. Threads and downloads remain unchanged.",
                    "Gruppe löschen? Nur ihre Zuordnungen werden entfernt. Threads und Downloads bleiben erhalten."), L("Delete group", "Gruppe löschen"))) return;
                _draft!.Groups.RemoveAll(item => item.Id == group!.Id); _selected = null; Render(); C<ListBox>("Groups").Focus(); break;
            case "reader-groups.add-members":
                ResourceId[] additions = _candidateSelectionOrder.ToArray();
                int added = additions.Except(group!.Members).Count(); _draft!.Add(group.Id, additions);
                ShowMessage(added == 0 ? L("These threads already belong to the group; nothing changed.", "Diese Threads gehören bereits zur Gruppe; nichts geändert.")
                    : L($"{added} threads added to the draft. Save to keep the membership.", $"{added} Threads zum Entwurf hinzugefügt. Zuordnung mit Speichern übernehmen."));
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
            ShowMessage(loaded.CanWrite ? _origin?.Notice ?? L("Groups only organise local reading. Changes require Save.", "Gruppen ordnen nur lokales Lesen. Änderungen mit Speichern übernehmen.")
                : LoadError(loaded));
            Render(_origin?.MemberId);
            _pendingMemberOrigin = _origin;
            _ = SearchAsync();
        }
        catch (OperationCanceledException) when (_closed || _lifetime.IsCancellationRequested) { }
        catch (Exception exception) { ShowMessage(L("Cannot load groups: ", "Gruppen konnten nicht geladen werden: ") + exception.Message); }
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
    private string LoadError(ReaderGroupsLoadResult loaded) => (loaded.State switch
    {
        ReaderGroupsLoadState.FutureVersion => L("This groups file is from a newer app version and cannot be edited.", "Diese Gruppendatei stammt aus einer neueren Appversion und kann nicht bearbeitet werden."),
        ReaderGroupsLoadState.RecoveryAvailable => L("The groups file is damaged or missing. A valid backup can be explicitly restored.", "Die Gruppendatei ist beschädigt oder fehlt. Eine gültige Sicherung kann ausdrücklich wiederhergestellt werden."),
        _ => L("Groups are unavailable. Correct the file error and reload.", "Gruppen sind nicht verfügbar. Dateifehler beheben und neu laden.")
    }) + "\n" + loaded.FilePath + "\n" + loaded.Error;

    private async Task SaveAsync()
    {
        bool failed = false;
        _busy = true; RefreshActions();
        try
        {
            ReaderGroupsSnapshot saved = await _host.SaveReaderGroupsAsync(_draft!.Snapshot, _draft.Revision, _lifetime.Token);
            if (_closed) return;
            _draft.Accept(saved); Render(); ShowMessage(L("Groups saved.", "Gruppen gespeichert."));
        }
        catch (ReaderGroupsConflictException)
        {
            _conflict = true;
            try { _loaded = await _host.LoadReaderGroupsAsync(_lifetime.Token); } catch (Exception) { /* Retain draft even if reload fails. */ }
            ShowMessage(L("Groups changed elsewhere. Your draft is retained. Reload to review the saved state before editing again.",
                "Gruppen wurden zwischenzeitlich geändert. Dein Entwurf bleibt erhalten. Neu laden zeigt nach Rückfrage den gespeicherten Stand."));
        }
        catch (OperationCanceledException) when (_closed || _lifetime.IsCancellationRequested) { }
        catch (Exception exception) { failed = true; ShowMessage(L("Save failed; the draft is retained. Retry Save. ", "Speichern fehlgeschlagen; der Entwurf bleibt erhalten. Speichern wiederholen. ") + exception.Message); C<TextBox>("GroupName").Focus(); }
        finally { _busy = false; RefreshActions(); if (failed && !_closed) C<TextBox>("GroupName").Focus(); }
    }
    private async Task RecoverAsync()
    {
        if (_busy || _loaded?.RecoveryToken is not string token || !await ConfirmAsync(
            L("Restore the backup? The damaged original is preserved.", "Sicherung wiederherstellen? Das beschädigte Original bleibt erhalten."), L("Restore", "Wiederherstellen"))) return;
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
            if (available.Length == 0) { ShowMessage(L("No locally readable posts in this group.\n", "Keine lokal lesbaren Posts in dieser Gruppe.\n") + detail); return; }
            if (missing.Length > 0 && !await ConfirmAsync(L($"{missing.Length} members have no local posts:\n", $"{missing.Length} Mitglieder haben keine lokalen Posts:\n") + detail,
                L("Read available", "Verfügbare lesen"))) return;
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
        if (!await ConfirmAsync(L("Discard unsaved changes? Choose Continue editing to keep the complete draft.", "Ungespeicherte Änderungen verwerfen? Weiter bearbeiten erhält den vollständigen Entwurf."),
            L("Discard changes", "Änderungen verwerfen"), L("Continue editing", "Weiter bearbeiten"))) return false;
        _draft.Discard(); Render(); return true;
    }
    private async Task RequestCloseAsync()
    {
        if (_busy || _confirming || _closed || !await DiscardAsync()) return;
        _allowClose = true; Close();
    }
    private async Task<bool> ConfirmAsync(string message, string confirm, string? cancel = null)
    {
        if (_confirming || _closed) return false;
        _confirming = true; RefreshActions();
        try { return await ReaderGroupsPrompt.ShowAsync(this, message, confirm, cancel ?? L("Cancel", "Abbrechen")); }
        finally { _confirming = false; RefreshActions(); }
    }

    private void Render(ResourceId? member = null)
    {
        member ??= SelectedMember;
        _rendering = true;
        try
        {
            var rows = _draft?.Groups.Select(group => new GroupRow(group.Id,
                $"{(group.Name.Length == 0 ? L("New group", "Neue Gruppe") : group.Name)} · {group.Members.Count} · {group.Id.ToString("N")[..8]}")).ToArray() ?? Array.Empty<GroupRow>();
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
            ? L("Enter a group name.", "Bitte einen Gruppennamen eingeben.")
            : name.EnumerateRunes().Count() > 120 || name.Any(char.IsControl)
                ? L("Use at most 120 characters, without control characters.", "Höchstens 120 Zeichen ohne Steuerzeichen verwenden.") : null;
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
        _candidateSelectionOrder.Clear(); RefreshActions();
        using CancellationTokenSource search = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _searchCancellation = search;
        string text = C<TextBox>("GroupSearch").Text ?? "";
        try
        {
            await Task.Delay(180, search.Token);
            ReaderGroupCandidatePage page = await _host.FindReaderGroupCandidatesAsync(text, 50, search.Token);
            if (_closed || request != _searchRequest) return;
            _candidateSelectionOrder.Clear();
            C<ListBox>("GroupCandidates").ItemsSource = page.Items.Select(Member).ToArray();
            C<ListBox>("GroupCandidates").IsVisible = true;
            C<TextBlock>("GroupSearchStatus").Text = L($"{page.Items.Count} local matches", $"{page.Items.Count} lokale Treffer")
                + (page.HasMore || text.Length == 0 ? L(" · Narrow the search to see other threads.", " · Suche eingrenzen, um weitere Threads zu finden.") : "");
            RefreshActions();
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested) { }
        catch (Exception exception) { if (!_closed && request == _searchRequest) C<TextBlock>("GroupSearchStatus").Text = exception.Message; }
        finally { if (ReferenceEquals(_searchCancellation, search)) _searchCancellation = null; }
    }
    private static string Identity(ResourceId id) => $"{id.Source}/{id.Board}/{id.Thread}";
    private static MemberRow Member(ReaderGroupCandidate item) => new(item,
        $"{Identity(item.Id)}{(string.IsNullOrWhiteSpace(item.Title) ? "" : " · " + item.Title)} · " +
        (item.HasLocalPosts ? L("Local posts", "Lokale Posts") : item.Watched ? L("Watched, no local posts yet", "Beobachtet, noch keine lokalen Posts") : L("Not available locally", "Lokal nicht verfügbar")));
    private void ShowMessage(string text) { if (!_closed) C<TextBlock>("GroupMessage").Text = text; }
}

internal static class ReaderGroupsPrompt
{
    internal static async Task<bool> ShowAsync(Window owner, string message, string accept, string cancel)
    {
        Window prompt = new() { Title = owner.Title, Width = 500, MaxWidth = Math.Max(400, owner.Bounds.Width), Height = 300,
            MinHeight = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        Button yes = new() { Content = accept }, no = new() { Content = cancel };
        yes.Click += (_, _) => prompt.Close(true); no.Click += (_, _) => prompt.Close(false);
        WrapPanel actions = new() { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(no); actions.Children.Add(yes);
        Grid content = new() { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 12 };
        content.Children.Add(new ScrollViewer { Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap } });
        Border footer = new() { Child = actions, Margin = new Thickness(-16, 0, -16, -16) };
        footer.Classes.Add("workspace-dialog-footer");
        Grid.SetRow(footer, 1); content.Children.Add(footer); prompt.Content = content;
        prompt.Opened += (_, _) => no.Focus();
        // Fixed dialog dismissal, independent of all freely assigned application gestures.
        prompt.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; prompt.Close(false); } };
        return await prompt.ShowDialog<bool>(owner);
    }
}
