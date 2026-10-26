using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    private ReaderGroupStore ReaderGroups => _readerGroupStore ??= new(ProfileDirectory, _profileLease);
    private ReaderGroupStore? _readerGroupStore; // Access only while holding _commands; shares existing owner.
    internal Task<ReaderGroupsLoadResult> LoadReaderGroupsAsync(CancellationToken token = default)
        => RunInteractiveAsync(nameof(LoadReaderGroupsAsync), "profile", () => LoadReaderGroupsAdmittedCoreAsync(token), captureAfter: false);

    private Task<ReaderGroupsLoadResult> LoadReaderGroupsAdmittedCoreAsync(CancellationToken token = default) =>
        ReaderGroupsCommandAsync(ct => ReaderGroups.LoadAsync(ct), token);
    internal Task<ReaderGroupsSnapshot> SaveReaderGroupsAsync(ReaderGroupsSnapshot draft, long expectedRevision, CancellationToken token = default)
    {
        ReaderGroupsSnapshot frozen = ReaderGroupsValidation.Copy(draft, normalizeNames: true);
        return RunInteractiveAsync(nameof(SaveReaderGroupsAsync), "profile", () => SaveReaderGroupsAdmittedCoreAsync(frozen, expectedRevision, token));
    }

    private Task<ReaderGroupsSnapshot> SaveReaderGroupsAdmittedCoreAsync(ReaderGroupsSnapshot draft, long expectedRevision, CancellationToken token = default)
    {
        ReaderGroupsSnapshot frozen = ReaderGroupsValidation.Copy(draft, normalizeNames: true);
        return ReaderGroupsCommandAsync(ct => ReaderGroups.SaveAsync(frozen, expectedRevision, ct), token);
    }
    internal Task<ReaderGroupsSnapshot> RestoreReaderGroupsBackupAsync(string recoveryToken, CancellationToken token = default)
        => RunInteractiveAsync(nameof(RestoreReaderGroupsBackupAsync), "profile", () => RestoreReaderGroupsBackupAdmittedCoreAsync(recoveryToken, token));

    private Task<ReaderGroupsSnapshot> RestoreReaderGroupsBackupAdmittedCoreAsync(string recoveryToken, CancellationToken token = default) =>
        ReaderGroupsCommandAsync(ct => ReaderGroups.RestoreBackupAsync(recoveryToken, ct), token);

    private async Task<T> ReaderGroupsCommandAsync<T>(Func<CancellationToken, Task<T>> command, CancellationToken token)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _startupCancellation.Token);
        await _commands.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try { EnsureReady(); return await command(lifetime.Token).ConfigureAwait(false); }
        finally { _commands.Release(); }
    }

    internal Task<ReaderGroupCandidatePage> FindReaderGroupCandidatesAsync(string text, int limit = 50, CancellationToken cancellationToken = default)
        => RunInteractiveAsync(nameof(FindReaderGroupCandidatesAsync), "profile", () => FindReaderGroupCandidatesAdmittedCoreAsync(text, limit, cancellationToken), captureAfter: false);

    private Task<ReaderGroupCandidatePage> FindReaderGroupCandidatesAdmittedCoreAsync(string text, int limit = 50, CancellationToken cancellationToken = default) =>
        ReaderGroupsCommandAsync(async token =>
        {
            if (text.Length > 512 || text.Contains('\0')) throw new ArgumentException("Search is limited to 512 characters.", nameof(text));
            if (limit < 1 || limit > 50) throw new ArgumentOutOfRangeException(nameof(limit));
            ReaderGroupCandidatePage page;
            try
            {
                await using ArchiveReader reader = await OpenArchiveReaderAsync(token).ConfigureAwait(false);
                page = await reader.FindReaderGroupCandidatesAsync(text, limit, token).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { page = new(Array.Empty<ReaderGroupCandidate>(), false); }
            Dictionary<ResourceId, ReaderGroupCandidate> items = page.Items.ToDictionary(item => item.Id);
            foreach (DesktopThreadEntry watch in GetView().Threads)
            {
                ThreadSnapshot snapshot = watch.Snapshot;
                ResourceId id = snapshot.Id;
                if (!(snapshot.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || Identity(id).Contains(text, StringComparison.OrdinalIgnoreCase))) continue;
                items[id] = items.TryGetValue(id, out ReaderGroupCandidate? archived)
                    ? archived with { Watched = true } : new(id, snapshot.Title, false, true);
            }
            ReaderGroupCandidate[] ordered = items.Values.OrderByDescending(item => Identity(item.Id) == text)
                .ThenBy(item => item.Id.Source, StringComparer.Ordinal).ThenBy(item => item.Id.Board, StringComparer.Ordinal)
                .ThenBy(item => item.Id.Thread, StringComparer.Ordinal).ToArray();
            ReaderGroupCandidate[] bounded = ordered.Take(limit).ToArray();
            // A watch may have fallen outside the archive page; resolve its actual local status without posts.
            try
            {
                await using ArchiveReader reader = await OpenArchiveReaderAsync(token).ConfigureAwait(false);
                var resolved = (await reader.ResolveReaderGroupMembersAsync(bounded.Select(item => item.Id).ToArray(), token).ConfigureAwait(false)).ToDictionary(item => item.Id);
                bounded = bounded.Select(item => resolved.TryGetValue(item.Id, out ReaderGroupCandidate? local)
                    ? item with { HasLocalPosts = local.HasLocalPosts, Title = string.IsNullOrEmpty(local.Title) ? item.Title : local.Title } : item).ToArray();
            }
            catch (FileNotFoundException) { }
            return new ReaderGroupCandidatePage(Array.AsReadOnly(bounded), page.HasMore || ordered.Length > limit);
        }, cancellationToken);

    internal Task<IReadOnlyList<ReaderGroupCandidate>> ResolveReaderGroupMembersAsync(IReadOnlyList<ResourceId> ids, CancellationToken cancellationToken = default)
    {
        ResourceId[] frozen = ids.ToArray();
        return RunInteractiveAsync(nameof(ResolveReaderGroupMembersAsync), "profile", () => ResolveReaderGroupMembersAdmittedCoreAsync(frozen, cancellationToken), captureAfter: false);
    }

    private Task<IReadOnlyList<ReaderGroupCandidate>> ResolveReaderGroupMembersAdmittedCoreAsync(IReadOnlyList<ResourceId> ids, CancellationToken cancellationToken = default)
    {
        ResourceId[] frozen = ids.ToArray();
        return ReaderGroupsCommandAsync<IReadOnlyList<ReaderGroupCandidate>>(async token =>
        {
            IReadOnlyList<ReaderGroupCandidate> resolved;
            try
            {
                await using ArchiveReader reader = await OpenArchiveReaderAsync(token).ConfigureAwait(false);
                resolved = await reader.ResolveReaderGroupMembersAsync(frozen, token).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { resolved = frozen.Select(id => new ReaderGroupCandidate(id, "", false)).ToArray(); }
            var watches = GetView().Threads.GroupBy(item => item.Snapshot.Id).ToDictionary(group => group.Key, group => group.First().Snapshot);
            return Array.AsReadOnly(resolved.Select(item => watches.TryGetValue(item.Id, out ThreadSnapshot? watch)
                ? item with { Watched = true, Title = string.IsNullOrEmpty(item.Title) ? watch.Title : item.Title } : item).ToArray());
        }, cancellationToken);
    }
    private static string Identity(ResourceId id) => $"{id.Source}/{id.Board}/{id.Thread}";
}
