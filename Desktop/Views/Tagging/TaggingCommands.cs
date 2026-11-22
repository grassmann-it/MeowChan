using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Import;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>The catalog commands the tagging page's selection (list and grid) answers, and the order the menu lists them in.</summary>
internal static class TaggingCommands
{
    internal const string ContinueHere = "tagging.continue-here";
    // The commands that already exist for the media page and the gallery keep their id, label, icon and key here (the list reads the
    // user's binding of the same command); only what has no counterpart is new in the "tagging" family.
    internal const string Tag = "media.tags.edit";
    internal const string TagTogether = "tagging.tag-together";
    internal const string ApplyStamp = "tags.quick.stamp-apply";
    internal const string MarkDone = "tagging.mark-done";
    internal const string MarkOpen = "tagging.mark-open";
    internal const string MarkSkipped = "tagging.mark-skipped";
    internal const string OpenGallery = "gallery.open";
    internal const string ShowInThread = "gallery.show-in-thread";
    internal const string ShowInMedia = "tagging.show-in-media";
    internal const string ShowInFolder = "media.show-in-folder";
    internal const string CopyPath = "media.copy-path";
    internal const string MoveUp = "tagging.move-up";
    internal const string MoveDown = "tagging.move-down";
    internal const string MoveFirst = "tagging.move-first";
    internal const string MoveLast = "tagging.move-last";
    internal const string Remove = "tagging.remove";
    internal const string GroupNew = "tagging.group.new";
    internal const string GroupRemove = "tagging.group.remove";
    internal const string GroupPrefix = "tagging.group.assign:";
    internal const string GroupMenu = "tagging.group.menu";
    // The group entries are no catalog commands; they take the icons of the matching reader-group commands (create, remove a member, the list).
    internal const string GroupNewIcon = "plus";
    internal const string GroupRemoveIcon = "minus";
    internal const string GroupMenuIcon = "list";

    /// <summary>The menu's sections (a separator between two): continue, state, look, order and the destructive one last.</summary>
    internal static readonly IReadOnlyList<IReadOnlyList<string>> Sections =
    [
        [ContinueHere, Tag, TagTogether, ApplyStamp],
        [MarkDone, MarkOpen, MarkSkipped],
        [OpenGallery, ShowInThread, ShowInMedia, ShowInFolder, CopyPath],
        [MoveUp, MoveDown, MoveFirst, MoveLast],
        [Remove]
    ];

    /// <summary>Every command of the selection, in menu order; the list routes the keys of the ones that have a shortcut.</summary>
    internal static IReadOnlyList<string> All { get; } = Sections.SelectMany(section => section).ToArray();
}

/// <summary>What the host can do for the page right now (the shell wires these; a page without the shell has none of the optional ones).</summary>
internal sealed record TaggingCapabilities(bool CanChange, bool CanContinue, bool CanTag, bool StampHasTags, bool CanOpenGallery,
    bool CanShowInThread, bool CanShowInMedia, bool CanShowInFolder, bool CanCopy)
{
    internal static TaggingCapabilities None { get; } = new(false, false, false, false, false, false, false, false, false);
}

/// <summary>Everything the availability of a selection command depends on, as plain data (no control, no host).</summary>
internal sealed record TaggingCommandContext(TagQueueSnapshot Queue, TaggingQueueFilter Filter, IReadOnlyList<TaggingQueueRow> Selected,
    TaggingQueueRow? Lead, TaggingCapabilities Capabilities, Func<TagQueueItem, bool>? Scope = null);

/// <summary>Whether a command applies, and the localized reason when it does not (shown as the disabled entry's tooltip).</summary>
internal readonly record struct TaggingCommandState(bool Available, string? Reason)
{
    internal static TaggingCommandState Yes { get; } = new(true, null);
    internal static TaggingCommandState No(string reasonKey) => new(false, Texts.T(reasonKey));
}

/// <summary>
/// The rules of the tagging page's commands over the selection (list and grid alike): which entries a command acts on and why one is
/// off. A command that opens something for one medium (thread, folder, "Tag…") acts on the lead entry, one that changes states,
/// tags, the stamp or the queue acts on every selected entry, and an order command needs exactly one entry (the host moves one
/// entry per write). Pure: the page and the tests read the same rules.
/// </summary>
internal static class TaggingRowCommands
{
    /// <summary>The selected entries a tag action can reach: a provider (Danbooru) medium has no own-tag target.</summary>
    internal static IReadOnlyList<TaggingQueueRow> Taggable(IEnumerable<TaggingQueueRow> rows) =>
        rows.Where(row => row.Item.BooruPost == null).ToArray();

    /// <summary>True for an entry that comes from a chan thread with a post (not an own import, a provider medium or a folder file).</summary>
    internal static bool HasThread(TagQueueItem item) =>
        item.PostId != null && !ManualImportIdentity.IsImportThread(item.Thread) && !BooruMediaIdentity.IsBooruThread(item.Thread);

    internal static TaggingCommandState Evaluate(string id, TaggingCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        TaggingCapabilities can = context.Capabilities;
        IReadOnlyList<TaggingQueueRow> selected = context.Selected;
        if (context.Lead is not { } lead || selected.Count == 0) return TaggingCommandState.No("tagging.menu.reason.none");
        TagQueueSnapshot queue = context.Queue;
        switch (id)
        {
            case TaggingCommands.ContinueHere:
                if (!can.CanContinue || !can.CanChange) return TaggingCommandState.No("tagging.menu.reason.busy");
                return lead.Item.State == TagQueueState.Open ? TaggingCommandState.Yes : TaggingCommandState.No("tagging.page.continue.notOpen");
            case TaggingCommands.Tag:
                if (selected.Count > 1) return TaggingCommandState.No("tagging.menu.reason.useTogether");
                if (!can.CanTag) return TaggingCommandState.No("tagging.menu.reason.noTagging");
                return lead.Item.BooruPost == null ? TaggingCommandState.Yes : TaggingCommandState.No("booru.media.tags.unavailable");
            case TaggingCommands.TagTogether:
                if (selected.Count < 2) return TaggingCommandState.No("tagging.menu.reason.needTwo");
                if (!can.CanTag) return TaggingCommandState.No("tagging.menu.reason.noTagging");
                return Taggable(selected).Count > 0 ? TaggingCommandState.Yes : TaggingCommandState.No("booru.media.tags.unavailable");
            case TaggingCommands.ApplyStamp:
                if (!can.CanTag) return TaggingCommandState.No("tagging.menu.reason.noTagging");
                if (!can.StampHasTags) return TaggingCommandState.No("tagging.menu.reason.stampEmpty");
                return Taggable(selected).Count > 0 ? TaggingCommandState.Yes : TaggingCommandState.No("booru.media.tags.unavailable");
            case TaggingCommands.MarkDone: return Mark(can, selected, TagQueueState.Done);
            case TaggingCommands.MarkOpen: return Mark(can, selected, TagQueueState.Open);
            case TaggingCommands.MarkSkipped: return Mark(can, selected, TagQueueState.Skipped);
            case TaggingCommands.OpenGallery:
                return can.CanOpenGallery ? TaggingCommandState.Yes : TaggingCommandState.No("tagging.menu.reason.notReady");
            case TaggingCommands.ShowInThread:
                if (selected.Count != 1) return TaggingCommandState.No("tagging.menu.reason.needOne");
                if (!HasThread(lead.Item)) return TaggingCommandState.No("tagging.menu.reason.noThread");
                return can.CanShowInThread ? TaggingCommandState.Yes : TaggingCommandState.No("tagging.menu.reason.notReady");
            case TaggingCommands.ShowInMedia:
                if (selected.Count != 1) return TaggingCommandState.No("tagging.menu.reason.needOne");
                return can.CanShowInMedia ? TaggingCommandState.Yes : TaggingCommandState.No("tagging.menu.reason.notReady");
            case TaggingCommands.ShowInFolder:
                if (selected.Count != 1) return TaggingCommandState.No("tagging.menu.reason.needOne");
                return can.CanShowInFolder ? TaggingCommandState.Yes : TaggingCommandState.No("tagging.menu.reason.notReady");
            case TaggingCommands.CopyPath:
                if (selected.Count != 1) return TaggingCommandState.No("tagging.menu.reason.needOne");
                return can.CanCopy ? TaggingCommandState.Yes : TaggingCommandState.No("tagging.menu.reason.notReady");
            case TaggingCommands.MoveUp:
            case TaggingCommands.MoveDown:
            {
                if (!can.CanChange) return TaggingCommandState.No("tagging.menu.reason.busy");
                if (selected.Count != 1) return TaggingCommandState.No("tagging.menu.reason.needOneMove");
                int direction = id == TaggingCommands.MoveUp ? -1 : 1;
                return TaggingQueueView.MoveTarget(queue, context.Filter, lead.Key, direction, context.Scope) != null
                    ? TaggingCommandState.Yes : TaggingCommandState.No("tagging.menu.reason.edge");
            }
            case TaggingCommands.MoveFirst:
            case TaggingCommands.MoveLast:
            {
                if (!can.CanChange) return TaggingCommandState.No("tagging.menu.reason.busy");
                if (selected.Count != 1) return TaggingCommandState.No("tagging.menu.reason.needOneMove");
                return TaggingQueueView.EdgeTarget(queue, lead.Key, id == TaggingCommands.MoveLast) != null
                    ? TaggingCommandState.Yes : TaggingCommandState.No("tagging.menu.reason.edge");
            }
            case TaggingCommands.Remove:
                return can.CanChange ? TaggingCommandState.Yes : TaggingCommandState.No("tagging.menu.reason.busy");
            default:
                return TaggingCommandState.No("tagging.menu.reason.notReady");
        }
    }

    private static TaggingCommandState Mark(TaggingCapabilities can, IReadOnlyList<TaggingQueueRow> selected, TagQueueState state)
    {
        if (!can.CanChange) return TaggingCommandState.No("tagging.menu.reason.busy");
        return selected.Any(row => row.Item.State != state) ? TaggingCommandState.Yes : TaggingCommandState.No("tagging.menu.reason.same");
    }
}

/// <summary>
/// The context menu of the tagging page's entries (TG1), the same in the list and in the grid: continue (here, tag, tag together,
/// stamp), state, look (gallery, thread, media page, folder, file name), order and, last, removal. Built with the shared
/// <see cref="ContextMenuBuilder"/> so label, icon, shortcut and the reason of a disabled entry come with the catalog command.
/// </summary>
internal static class TaggingQueueMenu
{
    /// <param name="context">The selection and what is possible now.</param>
    /// <param name="action">What the entry of a command does (called when it is clicked).</param>
    internal static ContextMenuBuilder Compose(TaggingCommandContext context, Func<string, Action> action)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(action);
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(Texts.T("tagging.page.listName"));
        for (int section = 0; section < TaggingCommands.Sections.Count; section++)
        {
            if (section > 0) menu.Section();
            foreach (string id in TaggingCommands.Sections[section])
            {
                TaggingCommandState state = TaggingRowCommands.Evaluate(id, context);
                menu.Command(id, action(id), state.Available, state.Reason, keyContext: "tagging");
            }
            // Groups sit between decisions and navigation in the row menu.
            if (section == 1) AddGroups(menu.Section(), context, action);
        }
        return menu;
    }

    internal static ContextMenuBuilder ComposeSelection(TaggingCommandContext context, Func<string, Action> action)
    {
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(Texts.T("tagging.page.selection.name"));
        void Add(string id, string? label = null, bool dangerous = false)
        {
            TaggingCommandState state = TaggingRowCommands.Evaluate(id, context);
            menu.Command(id, action(id), state.Available, state.Reason, label: label, dangerous: dangerous, keyContext: "tagging");
        }
        Add(TaggingCommands.ApplyStamp, Texts.T("tagging.page.selection.applyStamp"));
        Add(TaggingCommands.MarkSkipped, Texts.T("tagging.page.selection.skip"));
        Add(TaggingCommands.MarkOpen, Texts.T("tagging.page.selection.reopen"));
        menu.Section();
        Add(TaggingCommands.ContinueHere);
        AddGroups(menu, context, action);
        menu.Section();
        Add(TaggingCommands.Remove, dangerous: true);
        return menu;
    }

    private static void AddGroups(ContextMenuBuilder menu, TaggingCommandContext context, Func<string, Action> action)
    {
        bool available = context.Capabilities.CanChange;
        string label = Texts.T("tagging.page.group.assign");
        ContextMenuBuilder groups = new ContextMenuBuilder().Named(label);
        groups.Item(TaggingCommands.GroupNew, Texts.T("tagging.page.group.newMenu"), action(TaggingCommands.GroupNew),
            available, icon: TaggingCommands.GroupNewIcon);
        foreach (TagQueueGroup group in context.Queue.Groups)
        {
            string groupId = TaggingCommands.GroupPrefix + group.Id;
            bool assigned = context.Selected.Count > 0 && context.Selected.All(row => row.Item.GroupId == group.Id);
            groups.Radio("tagging-groups", groupId, group.Name, assigned, action(groupId), available);
        }
        groups.Item(TaggingCommands.GroupRemove, Texts.T("tagging.page.group.remove"), action(TaggingCommands.GroupRemove),
            available && context.Selected.Any(row => row.Item.GroupId != null), icon: TaggingCommands.GroupRemoveIcon);
        menu.Submenu(TaggingCommands.GroupMenu, label, groups.ToModel(), available,
            Texts.T("tagging.menu.reason.busy"), TaggingCommands.GroupMenuIcon);
    }
}
