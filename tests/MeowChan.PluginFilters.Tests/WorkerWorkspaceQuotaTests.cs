using MeowChan.PluginHost.Filters;
using MeowChan.PluginHost.Runtime;
using MeowChan.Tests;

namespace MeowChan.PluginFilters.Tests;

public sealed class WorkerWorkspaceQuotaTests
{
    private static string Stat(int pid, char state, int group, ulong birth = 100, int parent = 1)
        => $"{pid} (helper ) with\nspaces) {state} {parent} {group} " + string.Join(" ", Enumerable.Repeat("0", 16)) + $" {birth}";

    private static void Process(string root, int pid, char state, int group, ulong birth = 100, int parent = 1)
    {
        string directory = Path.Combine(root, pid.ToString()); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stat"), Stat(pid, state, group, birth, parent));
        string task = Path.Combine(directory, "task", pid.ToString()); Directory.CreateDirectory(task);
        File.WriteAllText(Path.Combine(task, "stat"), Stat(pid, state, group, birth, parent));
    }

    [Fact]
    public void StableReparentedZombiesAllowQuotaReleaseOnlyAfterWorkspaceCleanup()
    {
        using TempDirectory proc = new();
        Process(proc.Path, 123, 'Z', 123); Process(proc.Path, 124, 'Z', 123, birth: 101, parent: 1);
        Process(proc.Path, 125, 'S', 999); // Other groups do not keep this quota alive.
        string? first = WorkerWorkspaceQuota.ReadLinuxZombieInventory(123, proc.Path);
        string? second = WorkerWorkspaceQuota.ReadLinuxZombieInventory(123, proc.Path);
        Assert.NotNull(first); Assert.False(WorkerWorkspaceQuota.GroupHasExited(0, 0, null, first));
        Assert.True(WorkerWorkspaceQuota.GroupHasExited(0, 0, first, second));
        FilterTemporaryBudget budget = new(100); object worker = new(); Assert.True(budget.AcceptWorker(worker, 80));
        budget.CompleteWorker(worker, 0, stoppedAndWorkspaceRemoved: false); Assert.Equal(80, budget.Total);
        budget.CompleteWorker(worker, 0, stoppedAndWorkspaceRemoved: WorkerWorkspaceQuota.GroupHasExited(0, 0, first, second));
        Assert.Equal(0, budget.Total);
    }

    [Theory]
    [InlineData('R')] [InlineData('S')] [InlineData('D')] [InlineData('T')] [InlineData('t')] [InlineData('X')]
    public void AnyExecutableOrUncertainMemberRetainsQuota(char state)
    {
        using TempDirectory proc = new(); Process(proc.Path, 123, 'Z', 123); Process(proc.Path, 124, state, 123);
        Assert.Null(WorkerWorkspaceQuota.ReadLinuxZombieInventory(123, proc.Path));
        Assert.False(WorkerWorkspaceQuota.GroupHasExited(0, 0, "123:100", null));
    }

    [Fact]
    public void ZombieLeaderWithALiveSiblingThreadCannotReleaseQuota()
    {
        using TempDirectory proc = new(); Process(proc.Path, 123, 'Z', 123);
        string task = Path.Combine(proc.Path, "123", "task", "1001"); Directory.CreateDirectory(task);
        File.WriteAllText(Path.Combine(task, "stat"), Stat(1001, 'S', 123));
        Assert.Null(WorkerWorkspaceQuota.ReadLinuxZombieInventory(123, proc.Path));
        File.WriteAllText(Path.Combine(task, "stat"), Stat(1001, 'Z', 123));
        Assert.NotNull(WorkerWorkspaceQuota.ReadLinuxZombieInventory(123, proc.Path));
        File.WriteAllText(Path.Combine(task, "stat"), "unreadable identity");
        Assert.Null(WorkerWorkspaceQuota.ReadLinuxZombieInventory(123, proc.Path));
    }

    [Fact]
    public void BirthIdentityChangeRequiresAnotherStableInventory()
    {
        using TempDirectory proc = new(); Process(proc.Path, 123, 'Z', 123, 100);
        string? before = WorkerWorkspaceQuota.ReadLinuxZombieInventory(123, proc.Path);
        Process(proc.Path, 123, 'Z', 123, 200);
        string? after = WorkerWorkspaceQuota.ReadLinuxZombieInventory(123, proc.Path);
        Assert.False(WorkerWorkspaceQuota.GroupHasExited(0, 0, before, after));
        Assert.True(WorkerWorkspaceQuota.GroupHasExited(0, 0, after, after));
    }

    [Fact]
    public void PermissionDeniedAndUnknownProbeErrorsNeverReleaseQuota()
    {
        Assert.False(WorkerWorkspaceQuota.GroupHasExited(-1, 1, "123:100", "123:100")); // EPERM
        Assert.False(WorkerWorkspaceQuota.GroupHasExited(-1, 22, "123:100", "123:100"));
        Assert.True(WorkerWorkspaceQuota.GroupHasExited(-1, 3, null, null)); // ESRCH
    }

    [Fact]
    public void EmptyMissingMalformedAndUnreadableInventoriesAreNotZombieProof()
    {
        using TempDirectory proc = new();
        Assert.Null(WorkerWorkspaceQuota.ReadLinuxZombieInventory(123, proc.Path));
        Process(proc.Path, 123, 'Z', 123);
        Directory.CreateDirectory(Path.Combine(proc.Path, "124")); // Vanished between listing and stat is harmless.
        Assert.NotNull(WorkerWorkspaceQuota.ReadLinuxZombieInventory(123, proc.Path));
        File.WriteAllText(Path.Combine(proc.Path, "124", "stat"), "truncated");
        Assert.Null(WorkerWorkspaceQuota.ReadLinuxZombieInventory(123, proc.Path));
        File.Delete(Path.Combine(proc.Path, "124", "stat"));
        Directory.CreateDirectory(Path.Combine(proc.Path, "124", "stat")); // Read fails even as root.
        Assert.Null(WorkerWorkspaceQuota.ReadLinuxZombieInventory(123, proc.Path));
        Assert.Null(WorkerWorkspaceQuota.ReadLinuxZombieInventory(123, Path.Combine(proc.Path, "missing")));
        Assert.False(WorkerWorkspaceQuota.GroupHasExited(0, 0, null, null));
    }

    [Theory]
    [InlineData("", true)] [InlineData(",hidepid=0", true)] [InlineData(",hidepid=off", true)]
    [InlineData(",hidepid=1", false)] [InlineData(",hidepid=2", false)] [InlineData(",hidepid=invisible", false)]
    public void RestrictedProcVisibilityCannotProveZombieOnlyGroup(string options, bool allowed)
    {
        Assert.Equal(allowed, WorkerWorkspaceQuota.ProcVisibilityAllowsZombieProof(
            $"proc /proc proc rw,nosuid{options} 0 0\n", Stat(123, 'S', 100), 123));
        Assert.False(WorkerWorkspaceQuota.ProcVisibilityAllowsZombieProof(
            "proc /proc proc rw 0 0\n", Stat(456, 'S', 100), 123)); // Different PID namespace.
        Assert.False(WorkerWorkspaceQuota.ProcVisibilityAllowsZombieProof("", Stat(123, 'S', 100), 123));
        Assert.False(WorkerWorkspaceQuota.ProcVisibilityAllowsZombieProof(
            "proc /proc proc rw 0 0\nproc /proc proc rw,hidepid=2 0 0\n", Stat(123, 'S', 100), 123));
    }
}
