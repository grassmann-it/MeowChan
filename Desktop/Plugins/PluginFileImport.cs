using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Installation;

namespace MeowChan.Desktop.Plugins;

internal enum PluginFileImportStatus
{
    /// <summary>The file was copied into the plugin folder.</summary>
    Imported,
    /// <summary>The chosen file already is that file of the plugin folder; nothing was copied.</summary>
    AlreadyInFolder,
    Unsupported,
    Empty,
    TooLarge,
    NotRegularFile,
    NameTaken,
    /// <summary>The file or the folder could not be read or written; the detail is the system's text.</summary>
    Failed
}

/// <param name="Path">The file in the plugin folder, for Imported and AlreadyInFolder.</param>
/// <param name="CandidateId">The inventory identity the Plugins catalog will show for that file.</param>
internal sealed record PluginFileImportResult(PluginFileImportStatus Status, string FileName, string? Path = null,
    string? CandidateId = null, string? Detail = null)
{
    internal bool Succeeded => Status is PluginFileImportStatus.Imported or PluginFileImportStatus.AlreadyInFolder;
}

/// <summary>
/// "Add plugin…": copies one plugin source file into the plugin folder and nothing more. It is not a second way in. The file
/// is judged by the inventory's own per-file inspection (<see cref="PluginInstallationInventory.InspectAsync"/>) before the
/// copy and again on the copy, so it is accepted or rejected exactly as if it had been placed in the folder by hand. Building,
/// describing, registration, enabling, consent and integrity checks still happen where they always did: at the next start and
/// in the Plugins page's own actions. Nothing is compiled, loaded or started here, and an existing file is never replaced.
/// </summary>
internal static class PluginFileImport
{
    /// <summary>Only a single source file can be added: a bundle manifest alone is useless without its assembly and resources.</summary>
    internal const string SourceExtension = ".cs";

    internal static async Task<PluginFileImportResult> ImportAsync(string sourcePath, string pluginDirectory, CancellationToken cancellationToken = default)
    {
        string fileName = Path.GetFileName(sourcePath);
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrEmpty(fileName)) return new(PluginFileImportStatus.Unsupported, fileName);
            string source = Path.GetFullPath(sourcePath);
            string directory = Path.GetFullPath(pluginDirectory);
            string destination = Path.Combine(directory, fileName);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            // Judge the chosen file first, so a rejected file leaves the folder untouched and the reason names the file.
            PluginInstallationCandidate chosen = await PluginInstallationInventory.InspectAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (Rejection(chosen) is { } rejected) return rejected;
            if (string.Equals(Path.GetDirectoryName(source), Path.TrimEndingDirectorySeparator(directory), comparison))
                return new(PluginFileImportStatus.AlreadyInFolder, fileName, source, chosen.CandidateId);
            if (!Directory.Exists(directory)) return new(PluginFileImportStatus.Failed, fileName, Detail: "The plugin directory does not exist: " + directory);
            // Never replace a plugin file (a supplied example, an edited copy): a name clash is the user's decision.
            if (File.Exists(destination) || Directory.Exists(destination) || new FileInfo(destination).LinkTarget != null)
                return new(PluginFileImportStatus.NameTaken, fileName);

            // Copy the bounded bytes to a staging file beside the plugin folder (same volume), then rename it into place:
            // the folder never shows a half-written file to the inventory scan, and the rename refuses to overwrite.
            byte[] bytes = await CompilerWire.ReadBoundedAsync(source, PluginInstallationInventory.DefaultMaximumSourceBytes, cancellationToken).ConfigureAwait(false);
            string staging = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)) ?? directory, ".plugin-import-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (FileStream output = new(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(staging, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
            { TryDelete(staging); return new(PluginFileImportStatus.NameTaken, fileName); }
            catch { TryDelete(staging); throw; }

            // The copy is what will be built: inspect it the way the inventory will.
            PluginInstallationCandidate copy = await PluginInstallationInventory.InspectAsync(destination, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            if (Rejection(copy) is { } copyRejected) { TryDelete(destination); return copyRejected; }
            return new(PluginFileImportStatus.Imported, fileName, destination, copy.CandidateId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        { return new(PluginFileImportStatus.Failed, fileName, Detail: exception.Message); }

        PluginFileImportResult? Rejection(PluginInstallationCandidate candidate)
        {
            if (candidate.Diagnostics.Count > 0)
            {
                PluginBuildDiagnostic first = candidate.Diagnostics[0];
                return first.Code switch
                {
                    "MCENTRY" => new(PluginFileImportStatus.NotRegularFile, fileName),
                    "MCEMPTY" => new(PluginFileImportStatus.Empty, fileName),
                    "MCLIMIT" => new(PluginFileImportStatus.TooLarge, fileName),
                    "MCUNSUPPORTED" => new(PluginFileImportStatus.Unsupported, fileName),
                    _ => new(PluginFileImportStatus.Failed, fileName, Detail: first.Message)
                };
            }
            return candidate.Kind == PluginCandidateKind.Source ? null : new(PluginFileImportStatus.Unsupported, fileName);
        }
    }

    /// <summary>The size limit in MiB the inventory applies to a source file (for the "too large" sentence).</summary>
    internal static int SourceLimitMiB => PluginInstallationInventory.DefaultMaximumSourceBytes / (1024 * 1024);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
