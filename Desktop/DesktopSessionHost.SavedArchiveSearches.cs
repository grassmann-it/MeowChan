using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    private SavedArchiveSearchStore? _savedArchiveSearchStore;
    // Lazy construction and loading keep this optional store's failures feature-local.
    private SavedArchiveSearchStore SavedArchiveSearches => _savedArchiveSearchStore ??=
        new SavedArchiveSearchStore(ProfileDirectory, _profileLease);

    internal Task<SavedArchiveSearchSnapshot> LoadSavedArchiveSearchesAsync(CancellationToken token = default)
        => RunInteractiveAsync(nameof(LoadSavedArchiveSearchesAsync), "profile", () => LoadSavedArchiveSearchesAdmittedCoreAsync(token), captureAfter: false);

    private Task<SavedArchiveSearchSnapshot> LoadSavedArchiveSearchesAdmittedCoreAsync(CancellationToken token = default) =>
        SavedSearchCommandAsync(() => SavedArchiveSearches.Load(), token);
    internal Task<SavedArchiveSearchSnapshot> RecoverSavedArchiveSearchesAsync(long revision, CancellationToken token = default)
        => RunInteractiveAsync(nameof(RecoverSavedArchiveSearchesAsync), "profile", () => RecoverSavedArchiveSearchesAdmittedCoreAsync(revision, token));

    private Task<SavedArchiveSearchSnapshot> RecoverSavedArchiveSearchesAdmittedCoreAsync(long revision, CancellationToken token = default) =>
        SavedSearchCommandAsync(() => SavedArchiveSearches.Recover(revision, token), token);
    internal Task<SavedArchiveSearchSnapshot> CreateSavedArchiveSearchAsync(Guid id, string name, ArchiveSearchSpec definition, long revision, CancellationToken token = default)
    {
        ArchiveSearchSpec frozen = ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(definition));
        return RunInteractiveAsync(nameof(CreateSavedArchiveSearchAsync), id.ToString(), () => CreateSavedArchiveSearchAdmittedCoreAsync(id, name, frozen, revision, token));
    }

    private Task<SavedArchiveSearchSnapshot> CreateSavedArchiveSearchAdmittedCoreAsync(Guid id, string name, ArchiveSearchSpec definition, long revision, CancellationToken token = default)
    {
        string frozenName = SavedArchiveSearchStore.ValidateName(name);
        ArchiveSearchSpec frozen = ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(definition));
        return SavedSearchCommandAsync(() => SavedArchiveSearches.Create(id, frozenName, frozen, revision, token), token);
    }
    internal Task<SavedArchiveSearchSnapshot> RenameSavedArchiveSearchAsync(Guid id, string name, long revision, CancellationToken token = default)
        => RunInteractiveAsync(nameof(RenameSavedArchiveSearchAsync), id.ToString(), () => RenameSavedArchiveSearchAdmittedCoreAsync(id, name, revision, token));

    private Task<SavedArchiveSearchSnapshot> RenameSavedArchiveSearchAdmittedCoreAsync(Guid id, string name, long revision, CancellationToken token = default)
    {
        string frozen = SavedArchiveSearchStore.ValidateName(name);
        return SavedSearchCommandAsync(() => SavedArchiveSearches.Rename(id, frozen, revision, token), token);
    }
    internal Task<SavedArchiveSearchSnapshot> ReplaceSavedArchiveSearchAsync(Guid id, ArchiveSearchSpec definition, long revision, CancellationToken token = default)
    {
        ArchiveSearchSpec frozen = ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(definition));
        return RunInteractiveAsync(nameof(ReplaceSavedArchiveSearchAsync), id.ToString(), () => ReplaceSavedArchiveSearchAdmittedCoreAsync(id, frozen, revision, token));
    }

    private Task<SavedArchiveSearchSnapshot> ReplaceSavedArchiveSearchAdmittedCoreAsync(Guid id, ArchiveSearchSpec definition, long revision, CancellationToken token = default)
    {
        ArchiveSearchSpec frozen = ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(definition));
        return SavedSearchCommandAsync(() => SavedArchiveSearches.Replace(id, frozen, revision, token), token);
    }
    internal Task<SavedArchiveSearchSnapshot> DeleteSavedArchiveSearchAsync(Guid id, long revision, CancellationToken token = default)
        => RunInteractiveAsync(nameof(DeleteSavedArchiveSearchAsync), id.ToString(), () => DeleteSavedArchiveSearchAdmittedCoreAsync(id, revision, token));

    private Task<SavedArchiveSearchSnapshot> DeleteSavedArchiveSearchAdmittedCoreAsync(Guid id, long revision, CancellationToken token = default) =>
        SavedSearchCommandAsync(() => SavedArchiveSearches.Delete(id, revision, token), token);

    private async Task<SavedArchiveSearchSnapshot> SavedSearchCommandAsync(Func<SavedArchiveSearchSnapshot> action, CancellationToken token)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            SavedArchiveSearchSnapshot result = await Task.Run(action, token).ConfigureAwait(false);
            SignalChanged();
            return result;
        }
        finally { _commands.Release(); }
    }
}
