// Server Meshing add-on — rig thread CPU (MESH_THREAD_CPU): the kernel32 calls behind the Threads window line (Windows only).
using System;
using System.Runtime.InteropServices;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshThreadCpuNative declares the kernel32 functions the thread CPU sampler calls, marshalled without unsafe
    /// code and laid out for both x64 and x86. Call it only once RuntimeInformation.IsOSPlatform(OSPlatform.Windows) is
    /// true. Every FILETIME is read as one <c>long</c> of 100 ns units: Windows stores its low half first (little-endian).
    /// Every cycle count (ULONG64) is read as one <c>long</c> too: at a few GHz it stays below 2^63 for decades.
    /// </summary>
    public static class ServerMeshThreadCpuNative
    {
        private const string Kernel32 = "kernel32.dll";

        /// <summary>TH32CS_SNAPTHREAD asks CreateToolhelp32Snapshot for every thread on the machine (the process id is ignored).</summary>
        public const uint TH32CS_SNAPTHREAD = 0x00000004;

        /// <summary>
        /// THREAD_QUERY_LIMITED_INFORMATION is the access right QueryThreadCycleTime, GetThreadTimes, GetProcessIdOfThread
        /// and GetThreadDescription need.
        /// </summary>
        public const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

        /// <summary>ERROR_NO_MORE_FILES is how Thread32First and Thread32Next report the end of the snapshot.</summary>
        public const int ERROR_NO_MORE_FILES = 18;

        /// <summary>ERROR_INVALID_PARAMETER is how OpenThread reports a thread id whose thread has already exited.</summary>
        public const int ERROR_INVALID_PARAMETER = 87;

        /// <summary>INVALID_HANDLE_VALUE is how CreateToolhelp32Snapshot fails; OpenThread fails with IntPtr.Zero instead.</summary>
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        /// <summary>
        /// ServerMeshThreadEntry32 is THREADENTRY32: seven 4-byte fields, 28 bytes on x64 and x86 alike, so no padding.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ServerMeshThreadEntry32
        {
            /// <summary>dwSize must hold the structure's size before Thread32First is called.</summary>
            public uint dwSize;

            /// <summary>cntUsage is no longer used by Windows (always 0).</summary>
            public uint cntUsage;

            /// <summary>th32ThreadID is the thread's id.</summary>
            public uint th32ThreadID;

            /// <summary>th32OwnerProcessID is the id of the process the thread belongs to.</summary>
            public uint th32OwnerProcessID;

            /// <summary>tpBasePri is the thread's base priority.</summary>
            public int tpBasePri;

            /// <summary>tpDeltaPri is no longer used by Windows (always 0).</summary>
            public int tpDeltaPri;

            /// <summary>dwFlags is no longer used by Windows (always 0).</summary>
            public uint dwFlags;

            /// <summary>Create returns an entry whose dwSize is set, ready for Thread32First.</summary>
            public static ServerMeshThreadEntry32 Create()
            {
                return new ServerMeshThreadEntry32 { dwSize = (uint)Marshal.SizeOf<ServerMeshThreadEntry32>() };
            }
        }

        /// <summary>CreateToolhelp32Snapshot takes a snapshot; it fails with <see cref="INVALID_HANDLE_VALUE"/>. Close it with CloseHandle.</summary>
        [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        /// <summary>Thread32First reads the snapshot's first thread into an entry whose dwSize is set.</summary>
        [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Thread32First(IntPtr hSnapshot, ref ServerMeshThreadEntry32 lpte);

        /// <summary>Thread32Next reads the snapshot's next thread; false with <see cref="ERROR_NO_MORE_FILES"/> at the end.</summary>
        [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Thread32Next(IntPtr hSnapshot, ref ServerMeshThreadEntry32 lpte);

        /// <summary>OpenThread opens a thread by id; it fails with IntPtr.Zero. Close the handle with CloseHandle.</summary>
        [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr OpenThread(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwThreadId);

        /// <summary>
        /// QueryThreadCycleTime reads the CPU cycles a thread has used since it was created, kernel and user together,
        /// counted at the invariant time-stamp counter rate rather than the core clock. Windows Vista or later.
        /// </summary>
        [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryThreadCycleTime(IntPtr ThreadHandle, out long CycleTime);

        /// <summary>
        /// QueryProcessCycleTime reads the CPU cycles every thread of a process has used, those that have exited included,
        /// at the same rate as <see cref="QueryThreadCycleTime"/>. Windows Vista or later.
        /// </summary>
        [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryProcessCycleTime(IntPtr ProcessHandle, out long CycleTime);

        /// <summary>GetProcessIdOfThread returns the id of the process a thread belongs to, or 0 on failure. Windows Vista or later.</summary>
        [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
        public static extern uint GetProcessIdOfThread(IntPtr Thread);

        /// <summary>
        /// GetThreadTimes reads a thread's creation, exit, kernel and user times (FILETIMEs, 100 ns units). The kernel and
        /// user times grow in whole scheduler ticks (15.625 ms by default).
        /// </summary>
        [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetThreadTimes(IntPtr hThread, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

        /// <summary>
        /// GetThreadDescription returns an HRESULT (negative on failure) and a string the caller must free with LocalFree,
        /// even when it is empty. Windows 10 1607 or later; older kernels throw EntryPointNotFoundException here.
        /// </summary>
        [DllImport(Kernel32, ExactSpelling = true)]
        public static extern int GetThreadDescription(IntPtr hThread, out IntPtr ppszThreadDescription);

        /// <summary>LocalFree frees a buffer Windows allocated; it returns IntPtr.Zero on success.</summary>
        [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>GetProcessTimes reads a process's creation, exit, kernel and user times (FILETIMEs, 100 ns units).</summary>
        [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

        /// <summary>GetCurrentProcess returns a pseudo-handle to this process, which must never be closed.</summary>
        [DllImport(Kernel32, ExactSpelling = true)]
        public static extern IntPtr GetCurrentProcess();

        /// <summary>GetCurrentThread returns a pseudo-handle to the calling thread, which must never be closed.</summary>
        [DllImport(Kernel32, ExactSpelling = true)]
        public static extern IntPtr GetCurrentThread();

        /// <summary>GetCurrentProcessId returns this process's id.</summary>
        [DllImport(Kernel32, ExactSpelling = true)]
        public static extern uint GetCurrentProcessId();

        /// <summary>GetCurrentThreadId returns the calling thread's operating-system id (Mono maps managed threads 1:1).</summary>
        [DllImport(Kernel32, ExactSpelling = true)]
        public static extern uint GetCurrentThreadId();

        /// <summary>CloseHandle closes a handle from CreateToolhelp32Snapshot or OpenThread.</summary>
        [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
