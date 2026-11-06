using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    private SavedArchiveSearchStore? _savedArchiveSearchStore;
    internal Action<ProfileAdmissionToken>? BeforeSavedArchiveSearchAdmittedMutationForTests { get; set; }
    internal Action<ProfileAdmissionToken>? AfterSavedArchiveSearchAdmittedMutationForTests { get; set; }
    // Lazy construction and loading keep this optional store's failures feature-local.
    private SavedArchiveSearchStore SavedArchiveSearches => _savedArchiveSearchStore ??=
        new SavedArchiveSearchStore(ProfileDirectory, _profileLease);

    internal Task<SavedArchiveSearchSnapshot> LoadSavedArchiveSearchesAsync(CancellationToken token = default)
        => RunInteractiveAsync(nameof(LoadSavedArchiveSearchesAsync), "profile", () => LoadSavedArchiveSearchesAdmittedCoreAsync(token), captureAfter: false);

    private Task<SavedArchiveSearchSnapshot> LoadSavedArchiveSearchesAdmittedCoreAsync(CancellationToken token = default) =>
        SavedSearchCommandAsync(() => SavedArchiveSearches.Load(), token);
    internal Task<SavedArchiveSearchSnapshot> RecoverSavedArchiveSearchesAsync(long revision, CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(RecoverSavedArchiveSearchesAsync), "profile", admission => RecoverSavedArchiveSearchesAdmittedCoreAsync(revision, admission, token));

    private Task<SavedArchiveSearchSnapshot> RecoverSavedArchiveSearchesAdmittedCoreAsync(long revision, ProfileAdmissionToken admission, CancellationToken token = default) =>
        SavedSearchCommandAsync(() => SavedArchiveSearches.RecoverAdmitted(revision, admission, token), token, admission);
    internal Task<SavedArchiveSearchSnapshot> CreateSavedArchiveSearchAsync(Guid id, string name, ArchiveSearchSpec definition, long revision, CancellationToken token = default)
    {
        ArchiveSearchSpec frozen = ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(definition));
        return RunInteractiveWithAdmissionAsync(nameof(CreateSavedArchiveSearchAsync), id.ToString(), admission => CreateSavedArchiveSearchAdmittedCoreAsync(id, name, frozen, revision, admission, token));
    }

    private Task<SavedArchiveSearchSnapshot> CreateSavedArchiveSearchAdmittedCoreAsync(Guid id, string name, ArchiveSearchSpec definition, long revision, ProfileAdmissionToken admission, CancellationToken token = default)
    {
        string frozenName = SavedArchiveSearchStore.ValidateName(name);
        ArchiveSearchSpec frozen = ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(definition));
        return SavedSearchCommandAsync(() => SavedArchiveSearches.CreateAdmitted(id, frozenName, frozen, revision, admission, token), token, admission);
    }
    internal Task<SavedArchiveSearchSnapshot> RenameSavedArchiveSearchAsync(Guid id, string name, long revision, CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(RenameSavedArchiveSearchAsync), id.ToString(), admission => RenameSavedArchiveSearchAdmittedCoreAsync(id, name, revision, admission, token));

    private Task<SavedArchiveSearchSnapshot> RenameSavedArchiveSearchAdmittedCoreAsync(Guid id, string name, long revision, ProfileAdmissionToken admission, CancellationToken token = default)
    {
        string frozen = SavedArchiveSearchStore.ValidateName(name);
        return SavedSearchCommandAsync(() => SavedArchiveSearches.RenameAdmitted(id, frozen, revision, admission, token), token, admission);
    }
    internal Task<SavedArchiveSearchSnapshot> ReplaceSavedArchiveSearchAsync(Guid id, ArchiveSearchSpec definition, long revision, CancellationToken token = default)
    {
        ArchiveSearchSpec frozen = ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(definition));
        return RunInteractiveWithAdmissionAsync(nameof(ReplaceSavedArchiveSearchAsync), id.ToString(), admission => ReplaceSavedArchiveSearchAdmittedCoreAsync(id, frozen, revision, admission, token));
    }

    private Task<SavedArchiveSearchSnapshot> ReplaceSavedArchiveSearchAdmittedCoreAsync(Guid id, ArchiveSearchSpec definition, long revision, ProfileAdmissionToken admission, CancellationToken token = default)
    {
        ArchiveSearchSpec frozen = ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(definition));
        return SavedSearchCommandAsync(() => SavedArchiveSearches.ReplaceAdmitted(id, frozen, revision, admission, token), token, admission);
    }
    internal Task<SavedArchiveSearchSnapshot> DeleteSavedArchiveSearchAsync(Guid id, long revision, CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(DeleteSavedArchiveSearchAsync), id.ToString(), admission => DeleteSavedArchiveSearchAdmittedCoreAsync(id, revision, admission, token));

    private Task<SavedArchiveSearchSnapshot> DeleteSavedArchiveSearchAdmittedCoreAsync(Guid id, long revision, ProfileAdmissionToken admission, CancellationToken token = default) =>
        SavedSearchCommandAsync(() => SavedArchiveSearches.DeleteAdmitted(id, revision, admission, token), token, admission);

    private async Task<SavedArchiveSearchSnapshot> SavedSearchCommandAsync(Func<SavedArchiveSearchSnapshot> action, CancellationToken token, ProfileAdmissionToken? admission = null)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (admission != null)
            {
                SavedArchiveSearches.BeforeAdmittedMutationForTests = BeforeSavedArchiveSearchAdmittedMutationForTests;
                SavedArchiveSearches.AfterAdmittedMutationForTests = AfterSavedArchiveSearchAdmittedMutationForTests;
            }
            // The outer command retains its exact owner through this actual worker
            // return, SignalChanged, and the runner's subsequent projection.
            SavedArchiveSearchSnapshot result = await Task.Run(action, token).ConfigureAwait(false);
            SignalChanged();
            return result;
        }
        finally { _commands.Release(); }
    }
}
