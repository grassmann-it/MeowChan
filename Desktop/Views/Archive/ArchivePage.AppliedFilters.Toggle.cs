using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan;

// Applied own-tag chips switch between include and exclude with the same toggle the draft chips and the media page carry;
// unlike a draft chip, a toggle on an applied chip searches again at once (like the media page, whose filter is live).
public partial class ArchivePage
{
    /// <summary>One applied chip: an own-tag chip carries its include/exclude toggle, every other chip is a plain removable value.</summary>
    private ClosableTag CreateAppliedChip((string Field, string? Value, string Label) item, int index)
    {
        if (item.Field is "tagInclude" or "tagExclude" && Guid.TryParse(item.Value, out Guid id))
            return CreateTagFilterChip(item.Label, TagFilterLabel(id), id, item.Field == "tagExclude",
                async () => await RemoveAppliedFilter(item.Field, item.Value, index), async () => await ToggleAppliedTag(id),
                copyName: () => _ = CopyChipTextAsync(this, TagFilterLabel(id)));
        return CreateFilterTag(item.Label, async () => await RemoveAppliedFilter(item.Field, item.Value, index), item.Field == "tagExclude");
    }

    /// <summary>
    /// The tag filter with this tag moved to the other list (the match mode is kept), or the localized reason it cannot move: a
    /// list holds at most <see cref="ArchiveTagFilter.MaximumTagsPerList"/> tags, the same limit the draft chips obey. A tag the
    /// filter does not hold changes nothing.
    /// </summary>
    internal static (ArchiveTagFilter? Filter, string? Refusal) ToggledTagFilter(ArchiveTagFilter tags, Guid id)
    {
        bool included = tags.Include.Contains(id);
        if (!included && !tags.Exclude.Contains(id)) return (tags, null);
        if ((included ? tags.Exclude : tags.Include).Count >= ArchiveTagFilter.MaximumTagsPerList)
            return (null, Texts.T("tags.filter.error.tooMany", ArchiveTagFilter.MaximumTagsPerList));
        try
        {
            return (ArchiveTagFilter.OrNull(included ? tags.Include.Where(tag => tag != id) : tags.Include.Append(id),
                included ? tags.Exclude.Append(id) : tags.Exclude.Where(tag => tag != id), tags.Match), null);
        }
        catch (ArgumentException exception) { return (null, exception.Message); }
    }

    /// <summary>
    /// The toggle of an applied tag chip: moves the tag to the other list and searches again. Null when the search ran, else the
    /// reason nothing happened (explained on the page's error line when it is a refused definition or limit).
    /// </summary>
    internal async Task<string?> ToggleAppliedTag(Guid id)
    {
        if (_busy || _ended || !CanRead || _appliedSearch?.Spec is not { Tags: { } tags } applied) return "unavailable";
        (ArchiveTagFilter? filter, string? refusal) = ToggledTagFilter(tags, id);
        if (filter == null) { ShowTagFilterError(refusal ?? "unavailable"); return refusal; }
        if (filter.Equals(tags)) return null;
        bool focused = ArchiveAppliedFilters.GetVisualDescendants().OfType<Control>().Any(control => control.IsKeyboardFocusWithin && control.IsFocused);
        if (!await SearchFromAppliedChip(applied.WithTags(filter), "tags")) return "not searched";
        // The chips were rebuilt (the tag moved behind the other list); a keyboard user stays on the toggle of the same tag.
        if (focused)
        {
            ClosableTag? chip = ArchiveAppliedFilters.Children.OfType<ClosableTag>().FirstOrDefault(tag => tag.Tag is Guid tagId && tagId == id);
            if (chip?.Content is StackPanel { Children: [Control toggle, ..] }) toggle.Focus(NavigationMethod.Tab);
        }
        return null;
    }

    /// <summary>Test seam of the applied chip's toggle: switches the applied own tag with this display name.</summary>
    internal Task<string?> ToggleAppliedTagByName(string name) =>
        _privateTags?.Tags.FirstOrDefault(tag => tag.Name.Display == name) is { } found
            ? ToggleAppliedTag(found.Id.Value) : Task.FromResult<string?>("Unknown tag: " + name);
}
