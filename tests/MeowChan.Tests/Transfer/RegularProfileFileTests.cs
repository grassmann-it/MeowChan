using System.Runtime.InteropServices;
using MeowChan.Persistence;

namespace MeowChan.Tests.Transfer;

public sealed class RegularProfileFileTests
{
    [Fact]
    public void MissingIsDistinctFromAZeroLengthRegularFile()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "private-marker.json");
        FileNotFoundException error = Assert.Throws<FileNotFoundException>(() => RegularProfileFile.OpenRead(path));
        Assert.DoesNotContain(directory.Path, error.ToString());
        Assert.DoesNotContain("private-marker", error.ToString());
        File.WriteAllBytes(path, []);
        using FileStream file = RegularProfileFile.OpenRead(path);
        Assert.Equal(0, file.Length);
        Assert.Equal(-1, file.ReadByte());
    }


    [Fact]
    public void MissingParentIsNotAMissingProfileFile()
    {
        using TempDirectory directory = new();
        string missingParent = Path.Combine(directory.Path, "renamed-profile");
        string path = Path.Combine(missingParent, "media-favorites.json");
        IOException error = Assert.Throws<IOException>(() => RegularProfileFile.OpenRead(path));
        Assert.DoesNotContain(directory.Path, error.ToString());
        Assert.DoesNotContain("renamed-profile", error.ToString());
    }

    [Fact]
    public void DirectoryIsNotAnEmptyStore()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "private-marker.json");
        Directory.CreateDirectory(path);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => RegularProfileFile.OpenRead(path));
        Assert.DoesNotContain(directory.Path, error.ToString());
        Assert.DoesNotContain("private-marker", error.ToString());
    }

    [Fact]
    public void OpenHandleReadsOriginalObjectWhenItsNameIsReplaced()
    {
        // FileShare.Read forbids replacement on Windows; native Windows sharing
        // coverage is a separate platform gate, not exercised by this Unix case.
        if (OperatingSystem.IsWindows()) return;
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "store.json");
        string replacement = Path.Combine(directory.Path, "new.json");
        byte[] original = [1, 2, 3, 4];
        File.WriteAllBytes(path, original);
        File.WriteAllBytes(replacement, [7, 8]);
        using FileStream file = RegularProfileFile.OpenRead(path);
        File.Move(replacement, path, overwrite: true);
        byte[] result = new byte[original.Length];
        file.ReadExactly(result);
        Assert.Equal(original, result);
        Assert.Equal(-1, file.ReadByte());
        Assert.Equal(new byte[] { 7, 8 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void ExclusiveManagedWriterIsRespectedAndFailureIsOpaque()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "private-marker.json");
        File.WriteAllBytes(path, [1, 2, 3]);
        using (FileStream writer = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            IOException error = Assert.Throws<IOException>(() => RegularProfileFile.OpenRead(path));
            Assert.DoesNotContain(directory.Path, error.ToString());
            Assert.DoesNotContain("private-marker", error.ToString());
            Assert.Null(error.InnerException);
        }
        using FileStream file = RegularProfileFile.OpenRead(path);
        Assert.Equal(3, file.Length);
    }

    [Fact]
    public void WindowsInteropLayoutsMatchTheNativeFileInformationStructs()
    {
        // Pure layout check, runnable on every platform; the handle queries themselves only run on Windows.
        Assert.Equal(40, Marshal.SizeOf<RegularProfileFile.WindowsFileBasicInfo>());
        Assert.Equal(24, (int)Marshal.OffsetOf<RegularProfileFile.WindowsFileBasicInfo>(
            nameof(RegularProfileFile.WindowsFileBasicInfo.ChangeTime)));
        Assert.Equal(16, (int)Marshal.OffsetOf<RegularProfileFile.WindowsFileBasicInfo>(
            nameof(RegularProfileFile.WindowsFileBasicInfo.LastWriteTime)));
        Assert.Equal(24, Marshal.SizeOf<RegularProfileFile.WindowsFileStandardInfo>());
        Assert.Equal(8, (int)Marshal.OffsetOf<RegularProfileFile.WindowsFileStandardInfo>(
            nameof(RegularProfileFile.WindowsFileStandardInfo.EndOfFile)));
        Assert.Equal(24, Marshal.SizeOf<RegularProfileFile.WindowsFileIdInfo>());
        Assert.Equal(8, (int)Marshal.OffsetOf<RegularProfileFile.WindowsFileIdInfo>(
            nameof(RegularProfileFile.WindowsFileIdInfo.FileIdLow)));
        Assert.Equal(16, (int)Marshal.OffsetOf<RegularProfileFile.WindowsFileIdInfo>(
            nameof(RegularProfileFile.WindowsFileIdInfo.FileIdHigh)));
    }

    [Fact]
    public void WindowsHandleIdentityDistinguishesEveryFieldIncludingTheHighFileIdHalf()
    {
        RegularProfileFileIdentity baseline = RegularProfileFile.WindowsHandleIdentity(
            0xFFFF_0000_1234_5678UL, 0x8000_0000_0000_0001UL, 7UL, 100, 1_000, 2_000);
        Assert.Equal(baseline, RegularProfileFile.WindowsHandleIdentity(
            0xFFFF_0000_1234_5678UL, 0x8000_0000_0000_0001UL, 7UL, 100, 1_000, 2_000));
        // A restore that keeps size and last-write time but changes the change time, a new file id that differs
        // only in the high half (ReFS), another volume, and a size change are all different identities.
        Assert.NotEqual(baseline, RegularProfileFile.WindowsHandleIdentity(
            0xFFFF_0000_1234_5678UL, 0x8000_0000_0000_0001UL, 7UL, 100, 1_000, 2_001));
        Assert.NotEqual(baseline, RegularProfileFile.WindowsHandleIdentity(
            0xFFFF_0000_1234_5678UL, 0x8000_0000_0000_0001UL, 8UL, 100, 1_000, 2_000));
        Assert.NotEqual(baseline, RegularProfileFile.WindowsHandleIdentity(
            0xFFFF_0000_1234_5679UL, 0x8000_0000_0000_0001UL, 7UL, 100, 1_000, 2_000));
        Assert.NotEqual(baseline, RegularProfileFile.WindowsHandleIdentity(
            0xFFFF_0000_1234_5678UL, 0x8000_0000_0000_0001UL, 7UL, 101, 1_000, 2_000));
        // The path fallback keeps the previous fields: size, last-write and creation time, no file id.
        Assert.Equal(new RegularProfileFileIdentity(0, 0, 100, 1_000, 0, 2_000, 0),
            RegularProfileFile.WindowsPathIdentity(100, 1_000, 2_000));
    }

    [Fact]
    public void WindowsRegularFileFilterRejectsLinksDirectoriesAndDevices()
    {
        Assert.True(RegularProfileFile.IsWindowsRegularFile(FileAttributes.Normal));
        Assert.True(RegularProfileFile.IsWindowsRegularFile(FileAttributes.Archive | FileAttributes.ReadOnly));
        Assert.False(RegularProfileFile.IsWindowsRegularFile(FileAttributes.Archive | FileAttributes.ReparsePoint));
        Assert.False(RegularProfileFile.IsWindowsRegularFile(FileAttributes.Directory));
        Assert.False(RegularProfileFile.IsWindowsRegularFile(FileAttributes.Device));
    }

    [Fact]
    public void IdentityChangesWhenASameSizeRewriteRestoresTheTimestamps()
    {
        // Windows platform gate: the handle identity (change time) must see a restore that keeps size, last-write
        // and creation time. Not run on Unix, where ctime granularity of older kernels would make it timing-bound.
        if (!OperatingSystem.IsWindows()) return;
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "booru.json");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        DateTime written = File.GetLastWriteTimeUtc(path);
        DateTime created = File.GetCreationTimeUtc(path);
        RegularProfileFileIdentity? before = RegularProfileFile.TryGetIdentity(path);
        Assert.NotNull(before);
        Thread.Sleep(50);
        File.WriteAllBytes(path, [4, 3, 2, 1]);
        File.SetLastWriteTimeUtc(path, written);
        File.SetCreationTimeUtc(path, created);
        RegularProfileFileIdentity? after = RegularProfileFile.TryGetIdentity(path);
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
    }
}
