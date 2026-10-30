// Server Meshing add-on — what a cell asks of its own process, on either scripting backend (DESIGN.md 1.31).
//
// IL2CPP's class library leaves most of System.Diagnostics.Process out (libil2cpp's Process.cpp and NativeMethods.cpp):
// TotalProcessorTime and Kill throw NotSupportedException there. A cell reads the first for every load report and stats
// line, and the second is how it fences itself, so a cell built with IL2CPP sent no load report and outlived its fence.
// These two go to the operating system directly. A map spawner needs far more of that class and stays a Mono build.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshProcessSelf reads this process's processor time and kills this process: kernel32 on Windows, /proc and libc on
    /// Linux. Anywhere else it uses the class library, which only a Mono build has.
    /// </summary>
    public static class MeshProcessSelf
    {
        private const string Kernel32 = "kernel32.dll";
        private const string LibC = "libc.so.6";
        private const string ProcStatPath = "/proc/self/stat";

        /// <summary>The exit code Process.Kill gives a process on Windows.</summary>
        private const uint KilledExitCode = 0xFFFFFFFF;

        private const int SigKill = 9;

        /// <summary>/proc counts processor time in USER_HZ ticks, 100 a second on every Linux.</summary>
        private const long ProcTicksPerSecond = 100L;

        private static readonly bool s_windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static readonly bool s_linux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        /// <summary>ProcessorTime is the processor time this process has used, kernel and user together; zero when it cannot be read.</summary>
        public static TimeSpan ProcessorTime()
        {
            if (s_windows)
                return GetProcessTimes(GetCurrentProcess(), out _, out _, out long kernel, out long user) ? TimeSpan.FromTicks(kernel + user) : TimeSpan.Zero; // a FILETIME counts in TimeSpan's ticks
            if (s_linux)
                return TryParseProcStat(File.ReadAllText(ProcStatPath), out TimeSpan cpu) ? cpu : TimeSpan.Zero;
            using (Process process = Process.GetCurrentProcess())
                return process.TotalProcessorTime;
        }

        /// <summary>Kill ends this process at once, as Process.Kill does: no quit routine runs. Any thread may call it.</summary>
        public static void Kill()
        {
            if (s_windows)
            {
                TerminateProcess(GetCurrentProcess(), KilledExitCode);
                return;
            }
            using (Process process = Process.GetCurrentProcess())
            {
                if (s_linux)
                    kill(process.Id, SigKill);
                else
                    process.Kill();
            }
        }

        /// <summary>
        /// TryParseProcStat reads a line of /proc/&lt;id&gt;/stat: utime and stime are its 14th and 15th fields, counted from
        /// the end of the command name, which sits in parentheses and may itself hold spaces and parentheses.
        /// </summary>
        public static bool TryParseProcStat(string stat, out TimeSpan cpu)
        {
            cpu = TimeSpan.Zero;
            int nameEnd = stat != null ? stat.LastIndexOf(')') : -1;
            if (nameEnd < 0)
                return false;
            string[] fields = stat.Substring(nameEnd + 1).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            const int FirstFieldAfterName = 3, UserField = 14, KernelField = 15;
            if (fields.Length <= KernelField - FirstFieldAfterName
                || !long.TryParse(fields[UserField - FirstFieldAfterName], NumberStyles.None, CultureInfo.InvariantCulture, out long user)
                || !long.TryParse(fields[KernelField - FirstFieldAfterName], NumberStyles.None, CultureInfo.InvariantCulture, out long kernel))
                return false;
            cpu = TimeSpan.FromTicks((user + kernel) * (TimeSpan.TicksPerSecond / ProcTicksPerSecond));
            return true;
        }

        [DllImport(Kernel32, ExactSpelling = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

        [DllImport(Kernel32, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport(LibC, SetLastError = true)]
        private static extern int kill(int pid, int sig);
    }
}
