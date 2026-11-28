using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Gallery;

namespace MeowChan
{
    public sealed partial class MediaPage
    {
        private BooruContentProfileSnapshot? _contentProfile;
        private int _boundaryHidden;

        internal Func<BooruContentProfileSnapshot>? ReadContentProfile { get; set; }
        internal Func<MediaLibraryTile, CancellationToken, Task<Bitmap?>>? TileThumbnailDecoder { get; set; }

        internal void RefreshContentBoundary(bool localRatingsChanged = false)
        {
            if (_lifetime.IsCancellationRequested || ReadContentProfile == null) return;
            BooruContentProfileSnapshot profile = ReadContentProfile();
            bool active = MediaContentBoundary.RatingFilter(profile) != null;
            if (MediaContentBoundary.SameCeiling(_contentProfile, profile) && !(active && localRatingsChanged)) return;
            _contentProfile = profile;
            _presentIndex = null;
            if (_providerEntries > 0)
                Present(keepView: true, boundaryChanged: active);
        }

        private MediaContentBoundary? ContentBoundaryForPresentation() =>
            _providerEntries > 0 && MediaContentBoundary.RatingFilter(_contentProfile) is { } filter
                ? new(filter, ReadBooruCatalog)
                : null;

        private bool AllowsThumbnail(MediaLibraryTile tile)
        {
            if (_lifetime.IsCancellationRequested) return false;
            if (!tile.Occurrences.Any(entry => entry.BooruPostId != null)
                || MediaContentBoundary.RatingFilter(ReadContentProfile?.Invoke()) is not { } filter) return true;
            MediaContentBoundary boundary = new(filter, ReadBooruCatalog);
            return tile.Occurrences.All(entry => boundary.Allows(entry.Thread));
        }
    }
}

namespace MeowChan.Desktop
{
    internal sealed partial class DesktopSessionHost
    {
        internal Booru.BooruWorkspaceContent LocalMediaContent => Booru.BooruWorkspaceContent.ForProfile(_profileLease);
        internal BooruCatalogStore? LocalMediaCatalog => _registry.BooruCatalog;
        internal BooruContentProfileSnapshot ReadLocalMediaContentProfile() => new BooruContentProfileStore(_profileLease).Load();
    }
}
