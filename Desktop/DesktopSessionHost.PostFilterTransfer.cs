using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Desktop;

/// <summary>
/// Host-owned profile transfer/backup entry points for post filters. They reuse the
/// post-filter store's command serializer, lease, revision and admission boundaries;
/// no UI is added here and no imported value can activate a source or path.
/// </summary>
internal sealed partial class DesktopSessionHost
{
    internal Task<PostFilterTransferExport> ExportPostFiltersAsync(CancellationToken token = default) =>
        RunInteractiveAsync(nameof(ExportPostFiltersAsync), "profile", () =>
            PostFilterTransferReadCommandAsync(store => PostFilterProfileTransfer.Export(store, token), token),
            captureAfter: false);

    internal Task<PostFilterTransferExport> BackupPostFiltersAsync(CancellationToken token = default) =>
        RunInteractiveAsync(nameof(BackupPostFiltersAsync), "profile", () =>
            PostFilterTransferReadCommandAsync(store => PostFilterProfileBackup.Capture(store, token), token),
            captureAfter: false);

    internal Task<PostFilterSnapshot> ImportPostFiltersAsync(byte[] bytes, long expectedRevision,
        CancellationToken token = default)
    {
        byte[] frozen = (bytes ?? throw new ArgumentNullException(nameof(bytes))).ToArray();
        return RunInteractiveWithAdmissionAsync(nameof(ImportPostFiltersAsync), "profile", admission =>
            PostFilterTransferWriteCommandAsync(store =>
            {
                PostFilterTransferImport imported = PostFilterProfileTransfer.ImportAdmitted(store, frozen, expectedRevision, admission, token);
                if (!imported.IsImported)
                    throw new InvalidDataException("The post filter profile transfer could not be applied.");
                return imported.Snapshot!;
            }, token, admission));
    }

    internal Task<PostFilterSnapshot> RestorePostFiltersBackupAsync(byte[] bytes, long expectedRevision,
        CancellationToken token = default)
    {
        byte[] frozen = (bytes ?? throw new ArgumentNullException(nameof(bytes))).ToArray();
        return RunInteractiveWithAdmissionAsync(nameof(RestorePostFiltersBackupAsync), "profile", admission =>
            PostFilterTransferWriteCommandAsync(store =>
            {
                PostFilterTransferImport restored = PostFilterProfileBackup.RestoreAdmitted(store, frozen, expectedRevision, admission, token);
                if (!restored.IsImported)
                    throw new InvalidDataException("The post filter backup could not be restored.");
                return restored.Snapshot!;
            }, token, admission));
    }

    private async Task<T> PostFilterTransferReadCommandAsync<T>(Func<PostFilterStore, T> action,
        CancellationToken token)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            return await Task.Run(() => action(PostFilters), token).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }

    private async Task<T> PostFilterTransferWriteCommandAsync<T>(Func<PostFilterStore, T> action,
        CancellationToken token, ProfileAdmissionToken admission)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            PostFilterStore store = PostFilters;
            store.BeforeAdmittedMutationForTests = BeforePostFilterAdmittedMutationForTests;
            store.AfterAdmittedMutationForTests = AfterPostFilterAdmittedMutationForTests;
            T result = await Task.Run(() => action(store), token).ConfigureAwait(false);
            SignalChanged();
            return result;
        }
        finally { _commands.Release(); }
    }
}
