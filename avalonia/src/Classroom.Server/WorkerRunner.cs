using System.Diagnostics;
using System.Runtime.InteropServices;
using Classroom.Contracts;
using Classroom.Domain;

namespace Classroom.Server;
public record WorkerRequest(TaskDefinition Task, string? Snapshot, string Code, bool Check);
public record WorkerReply(ExecutionDto Result, string? Snapshot);
public sealed class WorkerRunner(string executable)
{
    readonly SemaphoreSlim slots = new(4, 4);
    int pending;
    public async Task<WorkerReply> Run(WorkerRequest request, CancellationToken cancellation = default)
    {
        if (Interlocked.Increment(ref pending) > 39)
        {
            Interlocked.Decrement(ref pending);
            throw new HttpFault(429, "Очередь проверок заполнена.");
        }

        var acquired = false;
        try
        {
            await slots.WaitAsync(cancellation);
            acquired = true;
            var info = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };
            if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                info.FileName = Environment.GetEnvironmentVariable("CLASSROOM_DOTNET") ?? "dotnet";
                info.ArgumentList.Add(executable);
            }
            else
                info.FileName = executable;
            info.Environment.Remove("SQL_CLASSROOM_DATA");
            info.Environment.Remove("CLASSROOM_DATA");
            info.Environment["DOTNET_GCHeapHardLimit"] = "0xC000000";
            using var process = Process.Start(info) ?? throw new IOException("Не удалось запустить проверку SQL.");
            using var job = WindowsJob.TryAttach(process);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var error = process.StandardError.ReadToEndAsync(deadline.Token);
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                await process.StandardInput.WriteLineAsync(Wire.Write(request).AsMemory(), deadline.Token);
                process.StandardInput.Close();
                var text = await output;
                await process.WaitForExitAsync(deadline.Token);
                await error;
                if (process.ExitCode != 0)
                    throw new IOException("Рабочий процесс проверки остановлен.");
                return Wire.Read<WorkerReply>(text);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
                return new(new(false, new([], []), [], "Запрос превысил допустимое время выполнения.", "Ограничьте объём запроса или проверьте условие рекурсии."), null);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            if (acquired)
                slots.Release();
            Interlocked.Decrement(ref pending);
        }
    }
}

// A job limits native SQLite allocations too; the managed GC limit alone is insufficient.
sealed class WindowsJob : IDisposable
{
    nint handle;
    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public BasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(nint handle);
    public static WindowsJob? TryAttach(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var job = new WindowsJob
        {
            handle = CreateJobObject(0, null)
        };
        var limits = new ExtendedLimits
        {
            BasicLimitInformation = new()
            {
                LimitFlags = 0x2000 | 0x100 | 0x8,
                ActiveProcessLimit = 1
            },
            ProcessMemoryLimit = 256 * 1024 * 1024
        };
        if (job.handle == 0 || !SetInformationJobObject(job.handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(job.handle, process.Handle))
        {
            job.Dispose();
            process.Kill(true);
            throw new IOException("Не удалось ограничить рабочий процесс Windows.");
        }

        return job;
    }

    public void Dispose()
    {
        if (handle != 0)
        {
            CloseHandle(handle);
            handle = 0;
        }
    }
}
