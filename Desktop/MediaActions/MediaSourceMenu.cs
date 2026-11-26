using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Desktop.MediaActions;

/// <summary>The original link and the tags of the same frozen media target.</summary>
internal sealed record MediaSourceMenuData(string? Url, IEnumerable<string>? Tags)
{
    internal static MediaSourceMenuData ForProvider(string postId, BooruPostClaim? post, string? savedOriginal = null)
    {
        bool withdrawn = post is { IsDeleted: true } or { IsBanned: true };
        string? original = withdrawn ? null : post?.Media.FirstOrDefault(media => media.Role == BooruPostClaimMediaRole.Original)?.Url;
        return new(MediaSourceMenu.PublicUrl(original) ?? MediaSourceMenu.PublicUrl(withdrawn ? null : savedOriginal)
            ?? BooruMediaIdentity.PostUrl(postId, post?.PostUrl)?.AbsoluteUri,
            post?.Fields.HasFlag(BooruPostClaimPresence.Tags) == true ? post.Tags.Select(tag => tag.Name) : null);
    }
}

internal static class MediaSourceMenu
{
    internal const string SearchId = "media.menu.reverseSearch";
    internal const string CopyId = "media.copy-booru-tags";

    // Service names are trademarks. These are browser URLs and are only passed to the launcher on invocation.
    private static readonly (string Id, string Name, string Address)[] Providers =
    [
        (SearchId + ".saucenao", "SauceNAO", "https://saucenao.com/search.php?url="),
        (SearchId + ".iqdb", "IQDB", "https://iqdb.org/?url="),
        (SearchId + ".lens", "Google Lens", "https://lens.google.com/uploadbyurl?url="),
    ];

    internal static void AddTo(ContextMenuBuilder menu, MediaSourceMenuData data, Func<bool> current,
        Func<string, Task> copy, Func<string, Task>? open = null, Func<Task<string?>>? readOriginal = null, string? blocked = null)
    {
        string? source = blocked == null ? PublicUrl(data.Url) : null;
        string label = Texts.T(SearchId);
        string tip = Texts.T("media.menu.reverseSearch.hint");
        string unavailable = Texts.T("media.menu.reverseSearch.noUrl");
        bool pending = source == null && blocked == null && readOriginal != null;
        ContextMenuModel Services(string? url)
        {
            ContextMenuBuilder items = new();
            if (url != null)
            {
                string encoded = Uri.EscapeDataString(url);
                foreach (var provider in Providers)
                {
                    string destination = provider.Address + encoded;
                    items.Item(provider.Id, provider.Name, () => Run(() => (open ?? ExternalLauncher.OpenAsync)(destination)),
                        availableWhen: current, icon: "external-link", tooltip: tip);
                }
            }
            return items.ToModel();
        }
        void Run(Func<Task> action)
        {
            if (current()) OpenActions.Start(action, "media.sourceMenu");
        }
        async Task ResolveAsync(MenuItem item)
        {
            string? resolved = PublicUrl(await readOriginal!());
            if (!current()) return;
            item.ItemsSource = ContextMenuBuilder.BuildItems(Services(resolved));
            item.IsEnabled = resolved != null;
            ToolTip.SetTip(item, resolved != null ? tip : label + " · " + unavailable);
            AutomationProperties.SetHelpText(item, resolved != null ? tip : unavailable);
        }

        menu.Submenu(SearchId, label, Services(source), source != null,
            reason: blocked ?? (pending ? Texts.T("reader.sidebar.loading") : unavailable), icon: "search", tooltip: tip,
            configure: pending ? item => OpenActions.Start(() => ResolveAsync(item), "media.sourceMenu.original") : null);
        string tags = TagText(data.Tags);
        menu.Command(CopyId, () => Run(() => copy(tags)), available: blocked == null && tags.Length > 0,
            reason: blocked ?? Texts.T("media.menu.copyBooruTags.empty"), showShortcut: false,
            availableWhen: () => blocked == null && tags.Length > 0 && current());
    }

    internal static IReadOnlyList<Control> Build(MediaSourceMenuData data, Func<bool> current, Control owner,
        Action<string>? copyFailed = null, Func<Task<string?>>? readOriginal = null, string? blocked = null)
    {
        ContextMenuBuilder menu = new();
        AddTo(menu, data, current, text => MenuClipboard.CopyAsync(owner, text, copyFailed), readOriginal: readOriginal, blocked: blocked);
        return menu.ComposeItems();
    }

    internal static string TagText(IEnumerable<string>? tags) => tags == null ? string.Empty : string.Join(" ",
        tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => string.Concat(tag.Trim().Select(character => char.IsWhiteSpace(character) ? '_' : character))));

    internal static IEnumerable<string>? OwnTags(PrivateTagStoreSnapshot? snapshot, MediaTagSubject? target) =>
        target == null ? null : snapshot?.TagsOf(target).Select(tag => tag.Name.Display);

    internal static string? Original(MediaAssetReference? asset) => asset?.Variant == "original" ? asset.SourceUrl : null;

    internal static string? Original(CapturedAttachment? attachment) => attachment?.Variant == "original" ? attachment.Url : null;

    internal static BooruPostClaim? ReadProvider(string postId, Func<BooruCatalogSnapshot?>? read)
    {
        try { return read?.Invoke()?.TryGetPost(BooruMediaIdentity.Source, postId)?.Post; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return null;
        }
    }

    internal static async Task<string?> ReadOriginalAsync(MediaLibraryEntry entry,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest, CancellationToken cancellationToken)
    {
        if (entry is not { Directory: { } directory, AssetId: not null, Variant: "original" } || entry.FolderItemId != null) return null;
        try
        {
            MediaAssetSnapshot manifest = await openManifest(directory, cancellationToken).ConfigureAwait(false);
            return Original(MediaTileThumbnails.Matching(manifest, entry));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Accept source metadata without resolving DNS and refuse local addresses and URL credentials.</summary>
    internal static string? PublicUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? url) || url.Scheme is not ("http" or "https")
            || url.UserInfo.Length != 0 || url.IsLoopback || !Uri.IsWellFormedUriString(value, UriKind.Absolute)) return null;
        string host = url.IdnHost.TrimEnd('.');
        if (IPAddress.TryParse(host, out IPAddress? address)) return IsPublicAddress(address) ? url.AbsoluteUri : null;
        if (url.HostNameType != UriHostNameType.Dns || !host.Contains('.')) return null;
        string suffix = host[(host.LastIndexOf('.') + 1)..].ToLowerInvariant();
        return suffix is "localhost" or "local" or "internal" or "lan" or "home" or "corp" or "test" or "invalid" or "example" or "onion"
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase) || host.Equals("home.arpa", StringComparison.OrdinalIgnoreCase)
            ? null : url.AbsoluteUri;
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        byte[] bytes = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] is > 0 and < 224 and not (10 or 127)
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2))
                && !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100))
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }
        return (bytes[0] & 0xe0) == 0x20 && address.ScopeId == 0
            && !(bytes[0] == 0x20 && (bytes[1] == 2 || bytes[1] == 1 && (bytes[2] < 2 || bytes[2] == 0x0d && bytes[3] == 0xb8)))
            && !(bytes[0] == 0x3f && bytes[1] == 0xff);
    }
}
