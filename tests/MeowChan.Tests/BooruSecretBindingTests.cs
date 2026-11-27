using MeowChan.Persistence;
using MeowChan.Secrets;

namespace MeowChan.Tests;

public sealed class BooruSecretBindingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("meowchan-account-test-").FullName;

    [Fact]
    public void OpaqueReferenceResolvesAfterReopeningTheSameProfile()
    {
        string path = Path.Combine(_root, "profile");
        Guid reference = Guid.NewGuid();
        string address;
        using (ProfileLease lease = ProfileLease.Acquire(path))
        {
            Assert.Null(BooruSecretReference.Read(lease));
            ProfileInstanceIdentity.ReadOrCreate(lease);
            BooruSecretReference.Change(lease, null, reference);
            address = BooruSecretReference.Address(lease, reference);
            Assert.Equal(reference.ToString("N"), File.ReadAllText(Path.Combine(path, BooruSecretReference.FileName)));
        }

        using ProfileLease reopened = ProfileLease.Acquire(path);
        Assert.Equal(reference, BooruSecretReference.Read(reopened));
        Assert.Equal(address, BooruSecretReference.Address(reopened, reference));
    }

    [Theory]
    [InlineData("clone")]
    [InlineData("move")]
    [InlineData("replacement")]
    public void CopyMoveAndReplacementResolveToDifferentLocalAddresses(string operation)
    {
        string path = Path.Combine(_root, "profile");
        string clone = Path.Combine(_root, "other");
        Guid reference = Guid.NewGuid();
        string address;
        using (ProfileLease lease = ProfileLease.Acquire(path))
        {
            ProfileInstanceIdentity.ReadOrCreate(lease);
            BooruSecretReference.Change(lease, null, reference);
            address = BooruSecretReference.Address(lease, reference);
        }

        if (operation == "move")
        {
            Directory.Move(path, clone);
        }
        else
        {
            if (operation == "replacement")
            {
                Directory.Move(path, clone);
                (path, clone) = (clone, path);
            }
            Directory.CreateDirectory(clone);
            foreach (string name in new[] { ProfileInstanceIdentity.FileName, BooruSecretReference.FileName })
                File.Copy(Path.Combine(path, name), Path.Combine(clone, name));
        }

        using ProfileLease copied = ProfileLease.Acquire(clone);
        Assert.Equal(reference, BooruSecretReference.Read(copied));
        Assert.NotEqual(address, BooruSecretReference.Address(copied, reference));
    }

    [Fact]
    public void StaleSelectionCannotOverwriteOrRemoveAReplacement()
    {
        using ProfileLease lease = ProfileLease.Acquire(_root);
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        BooruSecretReference.Change(lease, null, first);
        BooruSecretReference.Change(lease, first, second);

        Assert.Throws<InvalidDataException>(() => BooruSecretReference.Change(lease, first, null));
        Assert.Equal(second, BooruSecretReference.Read(lease));
        BooruSecretReference.Change(lease, second, null);
        Assert.Null(BooruSecretReference.Read(lease));
    }

    [Fact]
    public void DamagedSelectionIsRefusedWithoutBeingReplaced()
    {
        using ProfileLease lease = ProfileLease.Acquire(_root);
        string file = Path.Combine(_root, BooruSecretReference.FileName);
        File.WriteAllText(file, "not a reference");

        Assert.Throws<InvalidDataException>(() => BooruSecretReference.Read(lease));
        Assert.Throws<InvalidDataException>(() => BooruSecretReference.Change(lease, null, Guid.NewGuid()));
        Assert.Equal("not a reference", File.ReadAllText(file));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
