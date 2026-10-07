// 作者：xxx
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tau.CodingAgent.Tools;

/// <summary>【CodingAgent】【Windows 进程组】跟踪 MSYS fork 后父进程已经退出的后代，取消时仍能结束整个命令。</summary>
internal sealed class CodingAgentWindowsJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    /// <summary>【CodingAgent】【进程组创建】创建匿名作业，正常关闭时允许已启动的后台服务继续运行。</summary>
    private CodingAgentWindowsJob() => _handle = CreateJobObjectW(IntPtr.Zero, null);

    /// <summary>【CodingAgent】【进程组绑定】在进程启动后立即将其加入独立作业，后续子进程自动继承。</summary>
    /// <param name="process">本次创建的进程。</param><returns>Windows 作业，其他平台为空。</returns>
    internal static CodingAgentWindowsJob? Attach(Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var job = new CodingAgentWindowsJob();
        if (!job._handle.IsInvalid && AssignProcessToJobObject(job._handle, process.Handle)) return job;
        var error = Marshal.GetLastWin32Error();
        job.Dispose();
        if (process.HasExited) return null;
        process.Kill(entireProcessTree: true);
        throw new Win32Exception(error, "Failed to track command process tree.");
    }

    /// <summary>【CodingAgent】【进程组取消】终止所有仍属于本次命令的 Windows 进程。</summary>
    internal void Terminate() => TerminateJobObject(_handle, 1);

    /// <summary>【CodingAgent】【进程组释放】释放内核句柄，成功执行时不终止有意启动的后台服务。</summary>
    public void Dispose() => _handle.Dispose();

    /// <summary>【CodingAgent】【系统调用】创建匿名作业句柄。</summary>
    /// <param name="attributes">默认安全属性。</param><param name="name">可选作业名。</param><returns>作业句柄。</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    /// <summary>【CodingAgent】【系统调用】将进程加入作业。</summary>
    /// <param name="job">作业句柄。</param><param name="process">进程句柄。</param><returns>是否成功。</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    /// <summary>【CodingAgent】【系统调用】结束作业内所有进程。</summary>
    /// <param name="job">作业句柄。</param><param name="exitCode">结束状态码。</param><returns>是否成功。</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
