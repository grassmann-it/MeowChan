using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
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

        internal void RefreshContentBoundary()
        {
            if (_lifetime.IsCancellationRequested || ReadContentProfile == null) return;
            BooruContentProfileSnapshot profile = ReadContentProfile();
            if (MediaContentBoundary.SameCeiling(_contentProfile, profile)) return;
            _contentProfile = profile;
            _presentIndex = null;
            if (_providerEntries > 0)
                Present(keepView: true, boundaryChanged: MediaContentBoundary.RatingFilter(profile) != null);
        }

        private MediaContentBoundary? ContentBoundaryForPresentation() =>
            _providerEntries > 0 && MediaContentBoundary.RatingFilter(_contentProfile) is { } filter
                ? new(filter, ReadBooruCatalog)
                : null;
    }
}

namespace MeowChan.Desktop
{
    internal sealed partial class DesktopSessionHost
    {
        internal BooruContentProfileSnapshot ReadLocalMediaContentProfile() => new BooruContentProfileStore(_profileLease).Load();
    }
}
