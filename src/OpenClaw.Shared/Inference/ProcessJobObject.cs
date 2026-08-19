using System;
using System.Runtime.InteropServices;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// A Win32 job object configured to kill its members when the handle closes.
///
/// <para>Without this, a tray crash or a force-kill leaves <c>llama-server.exe</c>
/// running and holding tens of gigabytes of VRAM, with no UI left to stop it. The
/// user's only recourse is Task Manager, and the next launch fails on a port
/// conflict or an out-of-memory allocation. Assigning the child to a job with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> makes the OS clean up even when the
/// process that created it dies without running any code.</para>
///
/// <para>Windows-only. Construction failures are surfaced to the caller, which
/// treats the job as unavailable and falls back to best-effort shutdown rather
/// than refusing to launch.</para>
/// </summary>
public sealed class ProcessJobObject : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    /// <summary>True when a usable job handle was created.</summary>
    public bool IsValid => _handle != IntPtr.Zero;

    /// <summary>
    /// Create a job whose members are terminated when this instance is disposed
    /// or the owning process exits.
    /// </summary>
    /// <exception cref="InvalidOperationException">The job could not be created or configured.</exception>
    public ProcessJobObject()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateJobObject failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                var error = Marshal.GetLastWin32Error();
                Dispose();
                throw new InvalidOperationException(
                    $"SetInformationJobObject failed (Win32 error {error}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Add a process to the job. Returns false when the assignment failed, which
    /// the caller should log rather than treat as fatal: losing the safety net is
    /// worse than nothing but better than refusing to run.
    /// </summary>
    public bool TryAssign(IntPtr processHandle)
    {
        if (!IsValid || processHandle == IntPtr.Zero) return false;
        return AssignProcessToJobObject(_handle, processHandle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            // Closing the last handle to a KILL_ON_JOB_CLOSE job terminates
            // everything still in it. That is the point.
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int jobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
