using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Classroom.Desktop.Electronics;

// Each invocation gets a new AppContainer identity. The only capability grants
// read/execute on the public toolchain; the identity itself gets its scratch dir.
[SupportedOSPlatform("windows")]
public static class WindowsSandboxProcess
{
    public sealed record Result(int ExitCode, string Output, string Error);
    const string RuntimeCapability = "sqlClassroomCompilerRuntimeRead";
    static readonly Lazy<byte[]> capability = new(DeriveRuntimeCapability);
    static IOException Failure(string operation) => new($"Не удалось включить изоляцию компилятора ({operation}, Windows {Marshal.GetLastWin32Error()}).");

    public static void GrantRuntimeRead(string directory)
    {
        var info = new DirectoryInfo(Path.GetFullPath(directory));
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Комплект компилятора не должен быть ссылкой.");
        var acl = info.GetAccessControl();
        acl.SetAccessRule(new FileSystemAccessRule(new SecurityIdentifier(capability.Value, 0), FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }
    static void GrantScratch(string directory, nint sid)
    {
        var info = new DirectoryInfo(Path.GetFullPath(directory));
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Временная папка не должна быть ссылкой.");
        var acl = info.GetAccessControl();
        acl.SetAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;OICI;NW;;;LW)", 1, out var descriptor, out _)) throw Failure("scratch label");
        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out _, out var sacl, out _) || SetNamedSecurityInfo(directory, 1, 0x10, 0, 0, 0, sacl) != 0) throw Failure("scratch access");
        }
        finally { LocalFree(descriptor); }
    }
    static byte[] DeriveRuntimeCapability()
    {
        if (!DeriveCapabilitySidsFromName(RuntimeCapability, out var groups, out var groupCount, out var caps, out var count)) throw Failure("capability");
        try
        {
            if (count != 1) throw new IOException("Некорректная возможность доступа компилятора.");
            var identity = new SecurityIdentifier(Marshal.ReadIntPtr(caps)); var bytes = new byte[identity.BinaryLength]; identity.GetBinaryForm(bytes, 0); return bytes;
        }
        finally
        {
            for (var i = 0; i < groupCount; i++) LocalFree(Marshal.ReadIntPtr(groups, checked((int)i * IntPtr.Size)));
            for (var i = 0; i < count; i++) LocalFree(Marshal.ReadIntPtr(caps, checked((int)i * IntPtr.Size)));
            LocalFree(groups); LocalFree(caps);
        }
    }
    static string Quote(string argument)
    {
        if (argument.Contains('\0')) throw new ArgumentException("Недопустимый аргумент компилятора.");
        var text = new StringBuilder("\""); var slashes = 0;
        foreach (var value in argument)
        {
            if (value == '\\') { slashes++; continue; }
            text.Append('\\', value == '"' ? slashes * 2 + 1 : slashes); text.Append(value); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
    static async Task<string> ReadBounded(StreamReader reader)
    {
        var output = new StringBuilder(); var buffer = new char[4096]; bool truncated = false; int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            var keep = Math.Min(count, 128000 - output.Length);
            if (keep > 0) output.Append(buffer, 0, keep);
            if (keep < count) truncated = true;
        }
        if (truncated) output.Append("\n[Журнал компилятора сокращён.]"); return output.ToString();
    }
    static Dictionary<string, string> EnvironmentFor(string executable, string scratch, string containerName, IReadOnlyDictionary<string, string> settings)
    {
        var windows = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
        var profile = Path.Combine(scratch, "profile"); var temp = Path.Combine(scratch, "tmp");
        Directory.CreateDirectory(temp); Directory.CreateDirectory(Path.Combine(profile, "AppData", "Local")); Directory.CreateDirectory(Path.Combine(profile, "AppData", "Roaming"));
        Directory.CreateDirectory(Path.Combine(profile, "AppData", "Local", "Packages", containerName.ToLowerInvariant(), "AC", "Temp"));
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows, ["WINDIR"] = windows, ["PATH"] = Path.GetDirectoryName(executable) + ";" + Path.Combine(windows, "System32"),
            ["TEMP"] = temp, ["TMP"] = temp, ["USERPROFILE"] = profile, ["HOME"] = profile,
            ["APPDATA"] = Path.Combine(profile, "AppData", "Roaming"), ["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local"),
            ["ARDUINO_METRICS_ENABLED"] = "false", ["ARDUINO_UPDATER_ENABLE_NOTIFICATION"] = "false"
        };
        foreach (var pair in settings)
        {
            if (pair.Key is not ("ARDUINO_DIRECTORIES_DATA" or "ARDUINO_DIRECTORIES_DOWNLOADS" or "ARDUINO_DIRECTORIES_USER" or "ARDUINO_BUILD_CACHE_PATH"))
                throw new ArgumentException("Неизвестная настройка среды компилятора.");
            if (pair.Value.Contains('\0')) throw new ArgumentException("Недопустимая настройка среды компилятора.");
            env[pair.Key] = pair.Value;
        }
        return env;
    }
    static (SafeFileHandle Read, SafeFileHandle Write) Pipe()
    {
        var sa = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        if (!CreatePipe(out var read, out var write, ref sa, 0)) throw Failure("pipe");
        if (!SetHandleInformation(read, 1, 0)) { read.Dispose(); write.Dispose(); throw Failure("pipe inheritance"); }
        return (read, write);
    }

    public static async Task<Result> Run(string executable, IEnumerable<string> arguments, string readOnlyRoot, string scratch,
        IReadOnlyDictionary<string, string> settings, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        executable = Path.GetFullPath(executable); scratch = Path.GetFullPath(scratch); readOnlyRoot = Path.GetFullPath(readOnlyRoot);
        if (!executable.StartsWith(readOnlyRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Компилятор находится вне изолированного комплекта.");
        using var lease = new CompilerLease(readOnlyRoot, scratch);
        var profile = lease.ProfileName;
        var hr = CreateAppContainerProfile(profile, "SQL Classroom compiler", "Temporary offline compiler", 0, 0, out var appSid);
        if (hr < 0) throw new IOException($"Не удалось создать изоляцию компилятора (Windows 0x{hr:X8}).");
        nint attributes = 0, capsPointer = 0, capSid = 0, capItem = 0, handles = 0, environment = 0, job = 0;
        var pi = new ProcessInformation(); bool initialized = false;
        SafeFileHandle? input = null, outRead = null, outWrite = null, errRead = null, errWrite = null;
        try
        {
            lease.MarkProfile();
            var stdout = Pipe(); outRead = stdout.Read; outWrite = stdout.Write;
            var stderr = Pipe(); errRead = stderr.Read; errWrite = stderr.Write;
            Directory.CreateDirectory(scratch); GrantScratch(scratch, appSid);
            using var runtimeDrive = CompilerDrive.Create(readOnlyRoot, lease);
            using var scratchDrive = CompilerDrive.Create(scratch, lease);
            string Map(string value)
            {
                var separator = value.IndexOf('=');
                foreach (var pair in new[] { (Original: readOnlyRoot, Mapped: runtimeDrive.Root), (Original: scratch, Mapped: scratchDrive.Root) })
                {
                    if (string.Equals(value, pair.Original, StringComparison.OrdinalIgnoreCase)) return pair.Mapped;
                    var prefix = pair.Original.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return Path.Combine(pair.Mapped, value[prefix.Length..]);
                    if (separator > 0 && value[(separator + 1)..].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return value[..(separator + 1)] + Path.Combine(pair.Mapped, value[(separator + 1 + prefix.Length)..]);
                }
                return value;
            }
            executable = Map(executable);
            var env = EnvironmentFor(executable, scratchDrive.Root, profile, settings.ToDictionary(p => p.Key, p => Map(p.Value)));
            environment = Marshal.StringToHGlobalUni(string.Join('\0', env.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)) + "\0\0");
            var sa = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
            input = CreateFile("NUL", 0x80000000, 3, ref sa, 3, 0, 0);
            if (input.IsInvalid) throw Failure("stdin");
            capSid = Marshal.AllocHGlobal(capability.Value.Length); Marshal.Copy(capability.Value, 0, capSid, capability.Value.Length);
            capItem = Marshal.AllocHGlobal(Marshal.SizeOf<SidAndAttributes>()); Marshal.StructureToPtr(new SidAndAttributes { Sid = capSid, Attributes = 4 }, capItem, false);
            capsPointer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(new SecurityCapabilities { AppSid = appSid, Capabilities = capItem, Count = 1 }, capsPointer, false);
            nuint length = 0; InitializeProcThreadAttributeList(0, 2, 0, ref length);
            attributes = Marshal.AllocHGlobal(checked((int)length));
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref length)) throw Failure("attributes"); initialized = true;
            if (!UpdateProcThreadAttribute(attributes, 0, 0x20009, capsPointer, (nuint)Marshal.SizeOf<SecurityCapabilities>(), 0, 0)) throw Failure("security capabilities");
            handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handles, input.DangerousGetHandle()); Marshal.WriteIntPtr(handles, IntPtr.Size, outWrite.DangerousGetHandle()); Marshal.WriteIntPtr(handles, IntPtr.Size * 2, errWrite.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(IntPtr.Size * 3), 0, 0)) throw Failure("inherited handles");
            job = CreateJobObject(0, null);
            var limits = new JobLimits { Basic = new() { Flags = 0x2000 | 0x100 | 0x200 | 0x8, Processes = 16 }, ProcessMemory = 768 * 1024 * 1024, JobMemory = (nuint)(2UL * 1024 * 1024 * 1024) };
            if (job == 0 || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>())) throw Failure("job limits");
            var startup = new StartupInfoEx { Info = new() { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100, Input = input.DangerousGetHandle(), Output = outWrite.DangerousGetHandle(), Error = errWrite.DangerousGetHandle() }, Attributes = attributes };
            var command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments.Select(Map)).Select(Quote)));
            if (!CreateProcess(executable, command, 0, 0, true, 0x08080404, environment, scratchDrive.Root, ref startup, out pi)) throw Failure("process creation");
            if (!AssignProcessToJobObject(job, pi.Process)) throw Failure("job assignment");
            using var process = Process.GetProcessById((int)pi.ProcessId);
            outWrite.Dispose(); errWrite.Dispose(); input.Dispose();
            using var output = new StreamReader(new FileStream(outRead, FileAccess.Read), Encoding.UTF8);
            using var error = new StreamReader(new FileStream(errRead, FileAccess.Read), Encoding.UTF8);
            var outputTask = ReadBounded(output); var errorTask = ReadBounded(error);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation); limit.CancelAfter(TimeSpan.FromSeconds(60));
            using var registration = limit.Token.Register(() => TerminateJobObject(job, 1));
            if (ResumeThread(pi.Thread) == uint.MaxValue) throw Failure("process resume");
            await process.WaitForExitAsync();
            // Kill descendants retaining pipes before collecting the bounded output.
            TerminateJobObject(job, 1);
            await WaitForJob(job);
            var result = new Result(process.ExitCode, await outputTask, await errorTask);
            if (limit.IsCancellationRequested) throw new IOException("Компиляция остановлена: отмена или превышен лимит 60 секунд.\n" + result.Error[..Math.Min(result.Error.Length, 8000)]);
            return result;
        }
        finally
        {
            if (job != 0) { TerminateJobObject(job, 1); CloseHandle(job); }
            if (pi.Process != 0) { TerminateProcess(pi.Process, 1); CloseHandle(pi.Process); }
            if (pi.Thread != 0) CloseHandle(pi.Thread);
            input?.Dispose();
            outRead?.Dispose(); outWrite?.Dispose(); errRead?.Dispose(); errWrite?.Dispose();
            if (initialized) DeleteProcThreadAttributeList(attributes);
            foreach (var pointer in new[] { attributes, capsPointer, capSid, capItem, handles, environment }) if (pointer != 0) Marshal.FreeHGlobal(pointer);
            FreeSid(appSid);
        }
    }

    static async Task WaitForJob(nint job)
    {
        for (var i = 0; i < 500; i++)
        {
            if (!QueryInformationJobObject(job, 1, out var info, (uint)Marshal.SizeOf<JobAccounting>(), 0)) throw Failure("job completion");
            if (info.Active == 0) return;
            await Task.Delay(10);
        }
        throw new IOException("Не удалось завершить дочерние процессы компилятора.");
    }

    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Size; public nint Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] struct SidAndAttributes { public nint Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct SecurityCapabilities { public nint AppSid, Capabilities; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct StartupInfo
    {
        public int Size; public nint Reserved, Desktop, Title; public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct StartupInfoEx { public StartupInfo Info; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public nuint MinWorking, MaxWorking; public uint Processes; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct JobLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [StructLayout(LayoutKind.Sequential)] struct JobAccounting { public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, Total, Active, Terminated; }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] static extern int CreateAppContainerProfile(string name, string display, string description, nint capabilities, uint count, out nint sid);
    [DllImport("advapi32.dll")] static extern nint FreeSid(nint sid);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint pointer);
    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool DeriveCapabilitySidsFromName(string name, out nint groups, out uint groupCount, out nint capabilities, out uint count);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out nint descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetSecurityDescriptorSacl(nint descriptor, out bool present, out nint sacl, out bool defaulted);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint SetNamedSecurityInfo(string name, uint type, uint info, nint owner, nint group, nint dacl, nint sacl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes attributes, uint mode, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcess(string executable, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(nint job, int type, ref JobLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool QueryInformationJobObject(nint job, int type, out JobAccounting info, uint size, nint returned);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateJobObject(nint job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
}
