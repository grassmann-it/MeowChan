using System.ComponentModel;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal sealed partial record ArchiveThreadRow
{
    // This is a mutable row view model, selected by reference inside UI collections.
    // Projection/favorite updates must not change its equality or hash-set membership.
    public bool Equals(ArchiveThreadRow? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    private ArchiveThreadMediaCount? _mediaCount;
    private ThreadWorkSnapshot? _mediaCountWork = Work;
    public string CompactMedia => _mediaCount?.SavedVersions is int count ? count.ToString(Texts.Culture) : "—";
    public int SavedMediaCount => _mediaCount?.SavedVersions ?? -1;
    public string MediaStatus
    {
        get
        {
            string status = _mediaCount == null ? Texts.T("archive.media.countLoading")
                : _mediaCount.SavedVersions is int count ? Texts.T("archive.media.savedVersions", count)
                : Texts.T("archive.media.countUnavailable");
            status += "\n" + Texts.T("archive.media.countHint");
            if (_mediaCount?.MissingOrChangedFiles > 0)
                status += "\n" + Texts.T("archive.media.missingVersions", _mediaCount.MissingOrChangedFiles);
            if (_mediaCount?.Error is { Length: > 0 } error) status += "\n" + error;
            if (_mediaCountWork is { Media.Count: > 0 } work)
                status += "\n" + Texts.T("archive.media.currentProgress", WorkStatusFormatter.Format(work).Progress);
            return status;
        }
    }
    internal ArchiveThreadMediaCount? MediaCount => _mediaCount;
    internal void CopyMediaCountFrom(ArchiveThreadRow row) => SetMediaCount(row._mediaCount, row._mediaCountWork);
    internal void SetMediaCount(ArchiveThreadMediaCount? count, ThreadWorkSnapshot? work)
    {
        bool countChanged = _mediaCount != count;
        bool workChanged = _mediaCountWork != work;
        if (!countChanged && !workChanged) return;
        _mediaCount = count; _mediaCountWork = work;
        if (countChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompactMedia)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SavedMediaCount)));
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaStatus)));
    }
}
