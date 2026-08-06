using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Debug = UnityEngine.Debug;

namespace Net
{
    /// <summary>
    /// Binds spawned child processes to the lifetime of this one, so a development relay
    /// cannot outlive the game.
    /// </summary>
    /// <remarks>
    /// Killing a process on Windows does NOT kill its children. Without this, every
    /// crashed editor session, every "stop responding and force quit", and every domain
    /// reload at the wrong moment leaves an orphaned node.exe holding port 3000. The next
    /// launch then finds the port taken and fails in a way that looks like a bug in the
    /// game.
    ///
    /// A job object with KILL_ON_JOB_CLOSE is the OS-level fix: when the last handle to
    /// the job closes — including when the process holding it dies for any reason,
    /// gracefully or not — Windows terminates every process in the job. That covers the
    /// crash case, which an OnApplicationQuit hook cannot.
    ///
    /// LocalServices still kills its children explicitly on the way out. This is the
    /// backstop for when there is no way out.
    /// </remarks>
    public sealed class ChildProcessGroup : IDisposable
    {
        private IntPtr _handle;

        private static readonly bool IsWindows =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public bool IsActive => _handle != IntPtr.Zero;

        /// <summary>
        /// Creates the group, or returns a no-op instance where job objects are not
        /// available. Callers do not branch on platform; they just adopt and dispose.
        /// </summary>
        public static ChildProcessGroup Create()
        {
            var group = new ChildProcessGroup();
            if (!IsWindows) return group;

            try
            {
                // Unnamed: the handle is the only reference, which is exactly the
                // lifetime we want it tied to.
                var handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero)
                {
                    Debug.LogWarning($"[ChildProcessGroup] CreateJobObject failed ({Marshal.GetLastWin32Error()}); " +
                                     "children will only be cleaned up on a graceful quit.");
                    return group;
                }

                var limits = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose,
                    },
                };

                var size = Marshal.SizeOf(limits);
                var buffer = Marshal.AllocHGlobal(size);

                try
                {
                    Marshal.StructureToPtr(limits, buffer, false);
                    if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
                    {
                        Debug.LogWarning($"[ChildProcessGroup] SetInformationJobObject failed ({Marshal.GetLastWin32Error()}); " +
                                         "the kill-on-close guarantee is not in effect.");
                        CloseHandle(handle);
                        return group;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                group._handle = handle;
            }
            catch (DllNotFoundException)
            {
                // Not Windows after all, or a stripped runtime. Degrade to graceful-quit
                // cleanup rather than taking the launcher down with us.
            }
            catch (EntryPointNotFoundException)
            {
            }

            return group;
        }

        /// <summary>Adopts a started process. Safe to call on a process that has already exited.</summary>
        public void Adopt(Process process)
        {
            if (_handle == IntPtr.Zero || process == null) return;

            try
            {
                if (process.HasExited) return;
                if (!AssignProcessToJobObject(_handle, process.Handle))
                {
                    Debug.LogWarning($"[ChildProcessGroup] Could not adopt pid {process.Id} " +
                                     $"({Marshal.GetLastWin32Error()}); it may outlive a crash.");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ChildProcessGroup] Adoption failed: {e.Message}");
            }
        }

        /// <summary>Closing the last handle terminates every adopted process.</summary>
        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }

        // ------------------------------------------------------------------ interop

        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
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
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
