using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace MeowChan.Desktop;

public enum LocalFolderPickStatus { Selected, Cancelled, Unavailable, NonLocal, Failed }
public sealed record LocalFolderPickResult(LocalFolderPickStatus Status, string? Path = null, string? Error = null);

/// <summary>The settings draft owns the result; storage objects never escape this boundary.</summary>
public static class LocalFolderPicker
{
    public static async Task<LocalFolderPickResult> PickAsync(IStorageProvider? provider, string title)
    {
        try
        {
            if (provider?.CanPickFolder != true)
                return new(LocalFolderPickStatus.Unavailable);
            IReadOnlyList<IStorageFolder> folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title, AllowMultiple = false
            });
            try
            {
                if (folders.Count == 0) return new(LocalFolderPickStatus.Cancelled);
                string? path = folders[0].TryGetLocalPath();
                return string.IsNullOrWhiteSpace(path)
                    ? new(LocalFolderPickStatus.NonLocal)
                    : new(LocalFolderPickStatus.Selected, path);
            }
            finally
            {
                // Dispose every result even if a provider ignores AllowMultiple or one disposal fails.
                Exception? failure = null;
                foreach (IStorageFolder folder in folders)
                    try { folder.Dispose(); } catch (Exception error) { failure ??= error; }
                if (failure != null) throw failure;
            }
        }
        catch (Exception error) { return new(LocalFolderPickStatus.Failed, Error: error.Message); }
    }
}
