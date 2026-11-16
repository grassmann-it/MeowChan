using System.Text;
using MeowChan.Persistence;
using MeowChan.Tests.PersistenceCorruption;

namespace MeowChan.Tests;

/// <summary>
/// A dangling symbolic link in place of a store's primary file (an unmounted target, a moved profile) must not make every save
/// fail, and the user's link text must survive as a sibling link while the primary becomes a regular file again.
/// </summary>
public sealed class AtomicJsonFileDanglingLinkTests
{
    private const string FileName = "store.json";
    private static readonly byte[] BackupBytes = Encoding.UTF8.GetBytes("{\"generation\":\"backup\"}");

    [SymbolicLinkTheory]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void ADanglingPrimaryIsReplacedAndItsLinkIsKeptAsASidecar(bool backupCurrentFile, bool relativeTarget, bool withBackup)
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "profile");
        string outside = Path.Combine(temp.Path, "outside");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(outside);
        string primary = Path.Combine(directory, FileName);
        string backup = primary + ".bak";
        string linkText = relativeTarget ? Path.Combine("..", "outside", "missing.json") : Path.Combine(outside, "missing.json");
        string resolvedTarget = Path.Combine(outside, "missing.json");
        File.CreateSymbolicLink(primary, linkText);
        if (withBackup) File.WriteAllBytes(backup, BackupBytes);
        Assert.Equal(linkText, new FileInfo(primary).LinkTarget);

        if (!AtomicJsonFile.CanAtomicallyReplaceSymbolicLink)
        {
            // Windows cannot replace a link atomically; the save is refused and the link stays as it is.
            Assert.Throws<IOException>(() => AtomicJsonFile.WriteAllText(primary, "{\"generation\":1}", backupCurrentFile));
            Assert.Equal(linkText, new FileInfo(primary).LinkTarget);
            Assert.Empty(Sidecars(directory));
            return;
        }

        AtomicJsonFile.WriteAllText(primary, "{\"generation\":1}\r\n", backupCurrentFile);

        FileInfo written = new(primary);
        Assert.Null(written.LinkTarget);
        Assert.True(written.Exists);
        Assert.Equal("{\"generation\":1}\n", File.ReadAllText(primary));
        string sidecar = Assert.Single(Sidecars(directory));
        Assert.Equal(linkText, new FileInfo(sidecar).LinkTarget);
        if (withBackup) Assert.Equal(BackupBytes, File.ReadAllBytes(backup));
        else Assert.False(File.Exists(backup) || new FileInfo(backup).LinkTarget != null, "A backup was created from a dangling link.");
        Assert.False(File.Exists(resolvedTarget), "The save wrote through the link.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory, "*.tmp"));

        // The primary is a regular file now: the next save takes the ordinary path and adds no sidecar.
        AtomicJsonFile.WriteAllText(primary, "{\"generation\":2}", backupCurrentFile);
        Assert.Equal("{\"generation\":2}", File.ReadAllText(primary));
        Assert.Equal(sidecar, Assert.Single(Sidecars(directory)));
        Assert.Equal(linkText, new FileInfo(sidecar).LinkTarget);
        Assert.False(File.Exists(resolvedTarget));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory, "*.tmp"));
    }

    [SymbolicLinkTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservingADanglingLinkKeepsTheLinkInsteadOfFailing(bool relativeTarget)
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, FileName + ".bak");
        string linkText = relativeTarget ? "missing.json" : Path.Combine(temp.Path, "missing.json");
        File.CreateSymbolicLink(path, linkText);

        string preserved = AtomicJsonFile.PreserveFile(path);

        Assert.Equal(linkText, new FileInfo(preserved).LinkTarget);
        Assert.Equal(linkText, new FileInfo(path).LinkTarget);
        Assert.Equal(preserved, Assert.Single(Directory.EnumerateFileSystemEntries(temp.Path, FileName + ".bak.dangling-link-*")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "missing.json")));
    }

    private static string[] Sidecars(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, FileName + ".dangling-link-*").ToArray();
}
