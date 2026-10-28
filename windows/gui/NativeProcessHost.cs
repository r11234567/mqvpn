using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TruckersMP.Proxy;

internal static class HiddenConsole
{
    private const int SwHide = 0;
    private static bool _allocated;

    public static void Initialize()
    {
        if (!NativeMethods.AllocConsole())
            return;

        _allocated = true;
        NativeMethods.SetConsoleCtrlHandler(IntPtr.Zero, true);
        var window = NativeMethods.GetConsoleWindow();
        if (window != IntPtr.Zero)
            NativeMethods.ShowWindow(window, SwHide);
    }

    public static void Shutdown()
    {
        if (!_allocated)
            return;

        NativeMethods.FreeConsole();
        _allocated = false;
    }
}

internal static partial class NativeProcessHost
{
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CtrlBreakEvent = 1;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint HandleFlagInherit = 0x00000001;
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectInfoClassExtendedLimit = 9;

    public static int Run(string[] args)
    {
        try
        {
            HostNativeMethods.AttachConsole(AttachParentProcess);
            HostNativeMethods.SetConsoleCtrlHandler(IntPtr.Zero, true);

            ParseArguments(args, out var executable, out var workingDirectory, out var childArgs);
            return RunChild(executable, workingDirectory, childArgs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[host][error] {ex.Message}");
            return 1;
        }
    }

    private static int RunChild(string executable, string workingDirectory, string[] args)
    {
        var stdin = HostNativeMethods.GetStdHandle(StdInputHandle);
        var stdout = HostNativeMethods.GetStdHandle(StdOutputHandle);
        var stderr = HostNativeMethods.GetStdHandle(StdErrorHandle);
        MarkInheritable(stdin);
        MarkInheritable(stdout);
        MarkInheritable(stderr);

        var startupInfo = new StartupInfo
        {
            cb = Marshal.SizeOf<StartupInfo>(),
            dwFlags = StartfUseStdHandles,
            hStdInput = stdin,
            hStdOutput = stdout,
            hStdError = stderr,
        };

        var commandLine = new StringBuilder(BuildCommandLine(executable, args));
        if (!HostNativeMethods.CreateProcessW(
                executable,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                CreateNewProcessGroup,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out var processInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot start {executable}");
        }

        HostNativeMethods.CloseHandle(processInfo.hThread);
        var job = IntPtr.Zero;
        try
        {
            job = CreateKillOnCloseJob();
            if (!HostNativeMethods.AssignProcessToJobObject(job, processInfo.hProcess))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot assign child process to job");

            var stopTask = Task.Run(Console.ReadLine);
            while (true)
            {
                var wait = HostNativeMethods.WaitForSingleObject(processInfo.hProcess, 100);
                if (wait == WaitObject0)
                    break;

                if (wait != WaitTimeout)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject failed");

                if (!stopTask.IsCompleted)
                    continue;

                GracefulStop(processInfo.hProcess, processInfo.dwProcessId);
                break;
            }

            if (!HostNativeMethods.GetExitCodeProcess(processInfo.hProcess, out var exitCode))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetExitCodeProcess failed");

            return unchecked((int)exitCode);
        }
        catch
        {
            HostNativeMethods.TerminateProcess(processInfo.hProcess, 1);
            HostNativeMethods.WaitForSingleObject(processInfo.hProcess, 2000);
            throw;
        }
        finally
        {
            if (job != IntPtr.Zero)
                HostNativeMethods.CloseHandle(job);
            HostNativeMethods.CloseHandle(processInfo.hProcess);
        }
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = HostNativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create process job");

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, false);
            if (!HostNativeMethods.SetInformationJobObject(
                    job,
                    JobObjectInfoClassExtendedLimit,
                    buffer,
                    (uint)size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot configure process job");
            }

            return job;
        }
        catch
        {
            HostNativeMethods.CloseHandle(job);
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void GracefulStop(IntPtr processHandle, uint processId)
    {
        Console.Error.WriteLine("[host][info] graceful stop requested");
        if (!HostNativeMethods.GenerateConsoleCtrlEvent(CtrlBreakEvent, processId))
            Console.Error.WriteLine($"[host][warning] Ctrl+Break failed: {Marshal.GetLastWin32Error()}");

        if (HostNativeMethods.WaitForSingleObject(processHandle, 8000) == WaitObject0)
            return;

        Console.Error.WriteLine("[host][warning] graceful stop timed out; terminating process");
        HostNativeMethods.TerminateProcess(processHandle, 1);
        HostNativeMethods.WaitForSingleObject(processHandle, 2000);
    }

    private static void MarkInheritable(IntPtr handle)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return;

        HostNativeMethods.SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit);
    }

    private static void ParseArguments(
        string[] args,
        out string executable,
        out string workingDirectory,
        out string[] childArgs)
    {
        executable = string.Empty;
        workingDirectory = string.Empty;
        var parsedArgs = new List<string>();
        var afterSeparator = false;

        for (var i = 0; i < args.Length; i++)
        {
            if (afterSeparator)
            {
                parsedArgs.Add(args[i]);
                continue;
            }

            switch (args[i])
            {
                case "--exe" when i + 1 < args.Length:
                    executable = args[++i];
                    break;
                case "--workdir" when i + 1 < args.Length:
                    workingDirectory = args[++i];
                    break;
                case "--":
                    afterSeparator = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown host argument: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new FileNotFoundException("Child executable is missing", executable);

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"Working directory is missing: {workingDirectory}");

        childArgs = parsedArgs.ToArray();
    }

    private static string BuildCommandLine(string executable, IEnumerable<string> args)
    {
        var values = new List<string> { QuoteArgument(executable) };
        values.AddRange(args.Select(QuoteArgument));
        return string.Join(" ", values);
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"'))
            return value;

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(ch);
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

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

    private static class HostNativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(
            [MarshalAs(UnmanagedType.LPWStr)] string? applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            [MarshalAs(UnmanagedType.LPWStr)] string? currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateJobObjectW(
            IntPtr jobAttributes,
            [MarshalAs(UnmanagedType.LPWStr)] string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            IntPtr job,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);
    }
}

internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllocConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetConsoleWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}
