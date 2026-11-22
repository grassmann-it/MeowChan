using MeowChan.Admission;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class BoardAdmissionFileTests
{
    private const string Board = "https://stub.test/g/";
    private const string Valid = """{"SchemaVersion":1,"Boards":{}}""";
    private const string Future = """{"SchemaVersion":2,"Boards":["new format"]}""";

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Boards":{}}""")]
    [InlineData("""{"SchemaVersion":1}""")]
    [InlineData("""{"SchemaVersion":1,"Boards":null}""")]
    [InlineData("""{"SchemaVersion":1,"Boards":{"https://stub.test/g/":{"Exceptions":{}}}}""")]
    [InlineData("""{"SchemaVersion":1,"Boards":{"https://stub.test/g/":{"Rules":[]}}}""")]
    [InlineData("""{"SchemaVersion":1,"Boards":{"https://stub.test/g/":{"Rules":[{"Contains":"example","Field":0}],"Exceptions":{}}}}""")]
    public void IncompleteRulesDoNotActivateAnOlderBackup(string incomplete)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, BoardAdmissionStore.FileName);
        File.WriteAllText(path, incomplete);
        File.WriteAllText(path + ".bak", Valid);

        var error = Assert.Throws<InvalidDataException>(() => new BoardAdmissionStore(directory.Path, lease));
        Assert.Equal(BoardAdmissionProblem.Damaged, BoardAdmissionStore.Describe(error));
        Assert.Equal(incomplete, File.ReadAllText(path));
        Assert.Equal(Valid, File.ReadAllText(path + ".bak"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewerRulesBlockLoadAndSaveEvenWhenTheOtherFileIsReadable(bool newerBackup)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, BoardAdmissionStore.FileName);
        var store = new BoardAdmissionStore(directory.Path, lease);
        string primary = newerBackup ? Valid : Future;
        string backup = newerBackup ? Future : Valid;
        File.WriteAllText(path, primary);
        File.WriteAllText(path + ".bak", backup);

        var load = Assert.Throws<InvalidDataException>(() => new BoardAdmissionStore(directory.Path, lease));
        var save = Assert.Throws<InvalidDataException>(() => store.Save(Board, BoardAdmissionPolicy.All));
        Assert.Equal(BoardAdmissionProblem.NewerVersion, BoardAdmissionStore.Describe(load));
        Assert.Equal(BoardAdmissionProblem.NewerVersion, BoardAdmissionStore.Describe(save));
        Assert.Equal(primary, File.ReadAllText(path));
        Assert.Equal(backup, File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void BoardRuleReadAndWriteUseTheSameConfiguredByteLimit()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        lease.StoreLimits.BoardAdmissionSizeMiB = 1;
        string path = Path.Combine(directory.Path, BoardAdmissionStore.FileName);
        var store = new BoardAdmissionStore(directory.Path, lease);
        var small = new BoardAdmissionPolicy([new(ThreadTextField.Title, ThreadTextAction.Exclude, "spam")]);
        store.Save(Board, small);
        string before = File.ReadAllText(path);
        var large = new BoardAdmissionPolicy([new(ThreadTextField.Title, ThreadTextAction.Include, new string('é', 200_000))]);

        var save = Assert.Throws<InvalidDataException>(() => store.Save(Board, large));
        Assert.Equal(BoardAdmissionProblem.SizeLimit, BoardAdmissionStore.Describe(save));
        Assert.Same(small, store.Get(Board));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));

        lease.StoreLimits.BoardAdmissionSizeMiB = 2;
        store.Save(Board, large);
        Assert.Equal(large.Rules, new BoardAdmissionStore(directory.Path, lease).Get(Board).Rules);
        lease.StoreLimits.BoardAdmissionSizeMiB = 1;
        var load = Assert.Throws<InvalidDataException>(() => new BoardAdmissionStore(directory.Path, lease));
        Assert.Equal(BoardAdmissionProblem.SizeLimit, BoardAdmissionStore.Describe(load));
        Assert.Equal(before, File.ReadAllText(path + ".bak"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".bak")]
    public void NonRegularRuleFileIsRejected(string suffix)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, BoardAdmissionStore.FileName + suffix);
        Directory.CreateDirectory(path);
        var load = Assert.Throws<InvalidDataException>(() => new BoardAdmissionStore(directory.Path, lease));
        Assert.Equal(BoardAdmissionProblem.Damaged, BoardAdmissionStore.Describe(load));
        Assert.True(Directory.Exists(path));
    }
}
