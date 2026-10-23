using System.Diagnostics;
using System.Runtime.InteropServices;
using MeowChan.PluginProtocol;
using Microsoft.Win32.SafeHandles;

namespace MeowChan.PluginHost.Runtime;

/// <summary>Owns only a started worker and its descendants, including children after parent exit.</summary>
internal sealed class OwnedProcessTree : IDisposable
{
    private readonly Process _process;
    private readonly SafeFileHandle? _job;
    private bool _confirmedGroup;
    public OwnedProcessTree(Process process)
    {
        _process = process;
        if (!OperatingSystem.IsWindows()) return;
        _job = CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        ExtendedLimit limits = new() { BasicLimit = new BasicLimit { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimit>())
            || !AssignProcessToJobObject(_job, process.Handle))
        {
            int error = Marshal.GetLastWin32Error(); _job.Dispose();
            throw new System.ComponentModel.Win32Exception(error);
        }
    }
    public void ConfirmGroup() => _confirmedGroup = ProcessGroup.IsWorkerGroup(_process.Id);
    public void Kill()
    {
        if (_job != null && !_job.IsInvalid) TerminateJobObject(_job, 1);
        else ProcessGroup.KillWorkerGroup(_process.Id, _confirmedGroup);
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    public async Task WaitForExitAsync(CancellationToken token)
    {
        await _process.WaitForExitAsync(token).ConfigureAwait(false);
        if (_job is null) return;
        // The parent can exit before descendants release their current directory and
        // loaded assemblies. Keep the job alive until its entire owned tree is gone.
        while (true)
        {
            if (!QueryInformationJobObject(_job, 1, out BasicAccounting accounting,
                    (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (accounting.ActiveProcesses == 0) return;
            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }
    public void Dispose() { Kill(); _job?.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct BasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit
    {
        public BasicLimit BasicLimit;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimit information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out BasicAccounting information, uint length, IntPtr returnLength);
}
