using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Classroom.Desktop.Electronics;

// Short roots let tools canonicalize their own paths without requesting access
// to C:\Users or any other ancestor of the actual permitted directory.
[SupportedOSPlatform("windows")]
public sealed class CompilerDrive : IDisposable
{
    readonly string device, target;
    public string Root => device + "\\";
    CompilerDrive(string device, string target) { this.device = device; this.target = target; }
    public static CompilerDrive Create(string directory, CompilerLease lease)
    {
        using var mutex = new Mutex(false, "Local\\SQLClassroom.CompilerDrive.v1");
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        try
        {
            var target = "\\??\\" + Path.GetFullPath(directory);
            for (var letter = 'Z'; letter >= 'D'; letter--)
            {
                var device = letter + ":";
                var current = new StringBuilder(32768);
                if (QueryDosDevice(device, current, current.Capacity) != 0 || Marshal.GetLastWin32Error() != 2) continue;
                lease.AddDrive(device, target);
                if (!DefineDosDevice(0x9, device, target)) throw new IOException($"Не удалось создать путь изолированного компилятора (Windows {Marshal.GetLastWin32Error()}).");
                return new(device, target);
            }
            throw new IOException("Нет свободной буквы диска для изолированной компиляции.");
        }
        finally { mutex.ReleaseMutex(); }
    }
    public void Dispose()
    {
        // Exact-match removal never pops another program's drive mapping.
        Release(device, target);
    }
    internal static bool Release(string device, string target) => DefineDosDevice(0xf, device, target) || Marshal.GetLastWin32Error() == 2;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool DefineDosDevice(uint flags, string device, string target);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint QueryDosDevice(string device, StringBuilder target, int length);
}
