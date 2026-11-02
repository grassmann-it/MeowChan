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
}
